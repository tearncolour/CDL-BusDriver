using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CDLC.BusDriver;

static class BusPackageExportTests
{
    const string Cache = "StreamingData~/TS512_MB3.MidMips";
    const string Id = "0123456789abcdef0123456789abcdef";

    internal static void Run(Action<bool, string> check)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "cdlc-package-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string source = Path.Combine(temporary, "candidate.zip"), output = Path.Combine(temporary, "output.cok");
            MakePackage(source, Empty());
            string original = BusPackageExport.FileHash(source);
            File.WriteAllText(output, "old output replaced only after verification");
            var cleaned = BusPackageExport.ExportChecked(source, output);
            check(cleaned.RemovedEntries.SequenceEqual(new[] { Cache, Cache + ".cid" }), "package: removes only exact empty cache and its sidecar");
            check(BusPackageExport.FileHash(source) == original, "package: input remains untouched");
            using (var zip = ZipFile.OpenRead(output))
                check(zip.Entries.Select(e => e.FullName).SequenceEqual(new[] { "payload.bin", "payload.bin.cid" }), "package: all other fixture entries remain");
            bool candidateSeen = false;
            BusPackageExport.ExportChecked(source, output, candidate =>
            {
                candidateSeen = candidate.EndsWith(".tmp", StringComparison.Ordinal) && File.Exists(candidate);
                using (var zip = ZipFile.OpenRead(candidate))
                    candidateSeen &= zip.GetEntry(Cache) == null && zip.GetEntry(Cache + ".cid") == null;
            });
            check(candidateSeen, "package: validator inspects the filtered private candidate before publication");
            File.WriteAllText(output, "preserve output when a content validator rejects the candidate");
            string previousOutput = BusPackageExport.FileHash(output);
            bool validatorRejected = false;
            try { BusPackageExport.ExportChecked(source, output, candidate => { throw new InvalidDataException("fixture missing glass coat"); }); }
            catch (InvalidDataException error) { validatorRejected = error.Message == "fixture missing glass coat"; }
            check(validatorRejected && BusPackageExport.FileHash(source) == original && BusPackageExport.FileHash(output) == previousOutput &&
                Directory.GetFiles(temporary, "*.tmp").Length == 0,
                "package: content validation failure preserves both files and removes only its candidate");
            bool changedCandidateRejected = false;
            try { BusPackageExport.ExportChecked(source, output, candidate => File.AppendAllText(candidate, "unexpected write")); }
            catch (InvalidDataException) { changedCandidateRejected = true; }
            check(changedCandidateRejected && BusPackageExport.FileHash(output) == previousOutput && Directory.GetFiles(temporary, "*.tmp").Length == 0,
                "package: a validator cannot publish a modified candidate");

            MakePackage(source, Valid(duplicate: true, disabled: false));
            var duplicate = BusPackageExport.ExportChecked(source, output);
            check(duplicate.RemovedEntries.Length == 0 && duplicate.SourceSha256 == duplicate.OutputSha256,
                "package: legitimate VT duplicate entries remain byte-identical");
            MakePackage(source, Valid(duplicate: true, disabled: true));
            var mixed = BusPackageExport.ExportChecked(source, output);
            check(mixed.RemovedEntries.Length == 0 && mixed.SourceSha256 == mixed.OutputSha256,
                "package: mixed disabled and registered VT categories remain byte-identical");
            MakePackage(source, null);
            var without = BusPackageExport.ExportChecked(source, output);
            check(without.SourceSha256 == without.OutputSha256, "package: a non-VT package without a cache is untouched");

            var cases = new Dictionary<string, byte[]>
            {
                { "truncated cache", Empty().Take(43).ToArray() },
                { "trailing cache bytes", Empty().Concat(new byte[] { 1 }).ToArray() },
                { "nonempty disabled-only cache", DisabledOnly() },
                { "truncated valid payload", Valid(true, false).Take(200).ToArray() },
                { "unknown header", Empty().Select((b,i) => i == 1 ? (byte)1 : b).ToArray() },
            };
            foreach (var item in cases)
            {
                MakePackage(source, item.Value);
                RejectedWithoutReplacing(check, source, output, item.Key, temporary);
            }
            MakePackage(source, Empty(), sidecar: false);
            RejectedWithoutReplacing(check, source, output, "missing cache sidecar", temporary);
            MakePackage(source, null, orphan: true);
            RejectedWithoutReplacing(check, source, output, "orphan cache sidecar", temporary);
            MakePackage(source, Empty(), collision: true);
            RejectedWithoutReplacing(check, source, output, "case-insensitive entry collision", temporary);
            MakePackage(source, Empty(), compressed: true);
            RejectedWithoutReplacing(check, source, output, "non-Stored ZIP entry", temporary);
            MakePackage(source, Empty());
            byte[] corrupt = File.ReadAllBytes(source);
            int firstData = 30 + BitConverter.ToUInt16(corrupt, 26) + BitConverter.ToUInt16(corrupt, 28);
            corrupt[firstData] ^= 16;
            File.WriteAllBytes(source, corrupt);
            RejectedWithoutReplacing(check, source, output, "corrupt Stored content with stale CRC", temporary);

            SelfIdentifierCases(check, temporary, source, output);

            string root = Repo();
            if (root != null)
            {
                foreach (string name in new[] { "CDLCBus12", "SWB6710EV01G" })
                {
                    string healthy = Path.Combine(root, "Asset", name + ".cok");
                    check(File.Exists(healthy), "package: real healthy fixture exists " + name);
                    if (!File.Exists(healthy)) continue;
                    // The repo's packages come either as the game packed them (each prefab carrying its own identity once, before
                    // 0.9.11) or already through this export (repacked 17 October 14:00 with the strip in the packer: nothing left to
                    // take out). Either way the output must hold no prefab's own identity, keep its caches, and export again
                    // byte-identical; the console line says which case the file was.
                    int prefabs = SelfIdentities(healthy, out int selfBefore);
                    var result = BusPackageExport.ExportChecked(healthy, output);
                    SelfIdentities(output, out int selfAfter);
                    bool asPacked = result.RewrittenEntries.Length == prefabs && result.RemovedIdentifiers == selfBefore && selfBefore == prefabs;
                    bool alreadyStripped = selfBefore == 0 && result.RewrittenEntries.Length == 0 && result.SourceSha256 == result.OutputSha256;
                    check(prefabs > 0 && result.RemovedEntries.Length == 0 && selfAfter == 0 && (asPacked || alreadyStripped),
                        "package: real " + name + " keeps its caches and comes out without a prefab's own identity (" + selfBefore + " of " + prefabs + " prefabs had one, " + result.RemovedIdentifiers + " taken out)");
                    var again = BusPackageExport.ExportChecked(output, Path.Combine(temporary, name + "-again.cok"));
                    check(again.RemovedEntries.Length == 0 && again.RewrittenEntries.Length == 0 && again.SourceSha256 == again.OutputSha256,
                        "package: real " + name + " exported again is byte-identical");
                    Console.WriteLine("PACKAGE_EXPORT_REAL " + name + " before=" + result.SourceSha256 + " after=" + result.OutputSha256 + " removed=" + result.RemovedEntries.Length + " rewritten=" + result.RewrittenEntries.Length +
                        " self_identities_before=" + selfBefore + " after=" + selfAfter + (alreadyStripped ? " (already stripped)" : " (as the game packed it)"));
                }
                string failed = Path.Combine(root, "out", "swb6109-game-review", "20261015-045644", "quarantine", "repo-SWB6109EV12G.cok");
                if (File.Exists(failed))
                {
                    var result = BusPackageExport.ExportChecked(failed, output);
                    check(result.RemovedEntries.SequenceEqual(new[] { Cache, Cache + ".cid" }), "package: real isolated failing package loses exactly the two empty-cache entries");
                    Console.WriteLine("PACKAGE_EXPORT_REAL SWB6109-isolated before=" + result.SourceSha256 + " after=" + result.OutputSha256 + " removed=" + string.Join(",", result.RemovedEntries) + " retained=" + result.RetainedEntries);
                    string oldOutput = BusPackageExport.FileHash(output);
                    bool glassRejected = false;
                    try { BusPackageExport.ExportChecked(failed, output, BusPackageGlass.Verify); }
                    catch (InvalidDataException) { glassRejected = true; }
                    check(glassRejected && BusPackageExport.FileHash(output) == oldOutput && Directory.GetFiles(temporary, "*.tmp").Length == 0,
                        "package: real missing-coat surface is rejected before replacing the prior output");
                }
            }
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    // A prefab's own identity among its obsolete ones (the game's packer puts it there; PrefabSystem then warns "Duplicate prefab ID").
    static void SelfIdentifierCases(Action<bool, string> check, string temporary, string source, string output)
    {
        const string own = "e855a3c971f351d204ebdc10da58e9df", older = "4fdf58acd98ed089b903fbd33269cea7", other = "13c4cb240720fa6094b333bdebeb6f30";
        const string definition = "18|Game.Prefabs.PrefabIdentifierInfo, Game";
        // the identifiers of a prefab text: the list, its stated length, and the text before and after the identifiers object
        (OdinPrefabText.Node list, long length, string before, string after) Parts(string text)
        {
            OdinPrefabText.Node ids = OdinPrefabText.Parse(text).Member("components").Member("$rcontent").Items[1].Member("m_PrefabIdentifiers");
            return (ids.Member("$rcontent"), long.Parse(ids.Member("$rlength").Text), text.Substring(0, ids.Start), text.Substring(ids.End));
        }
        string Field(OdinPrefabText.Node entry, string name) => entry.Member(name)?.StringValue;

        // the only alias, the prefab itself: the list empties, its length says so, nothing else changes, and once is enough
        string one = Prefab("CarPrefab", "SWB6710EV01G", (true, "CarPrefab", "SWB6710EV01G", own));
        check(BusPackageExport.TryStripSelfIdentifiers(one, own, out string stripped, out int removed) && removed == 1, "identity: the prefab's own entry goes");
        var emptied = Parts(stripped);
        var original = Parts(one);
        check(emptied.length == 0 && emptied.list.Items.Count == 0, "identity: an emptied list says its length is 0");
        check(emptied.before == original.before && emptied.after == original.after, "identity: the text before and after the identifiers is byte-identical");
        check(!BusPackageExport.TryStripSelfIdentifiers(stripped, own, out _, out _), "identity: done once, there is nothing more to take out");

        // real aliases (earlier GUIDs) stay, in their order, with their type definition
        string many = Prefab("CarPrefab", "SWB6710EV01G", (true, "CarPrefab", "SWB6710EV01G", older), (false, "CarPrefab", "SWB6710EV01G", other), (false, "CarPrefab", "SWB6710EV01G", own));
        check(BusPackageExport.TryStripSelfIdentifiers(many, own, out stripped, out removed) && removed == 1, "identity: among real aliases only the own entry goes");
        var manyLeft = Parts(stripped);
        check(manyLeft.list.Items.Count == 2 && Field(manyLeft.list.Items[0], "m_Hash") == older && Field(manyLeft.list.Items[1], "m_Hash") == other &&
            Field(manyLeft.list.Items[0], "$type") == definition && manyLeft.list.Items[1].Member("$type").Text == "18" && manyLeft.length == 2 &&
            manyLeft.before == Parts(many).before && manyLeft.after == Parts(many).after,
            "identity: the real aliases keep their order, their type definition and the length; nothing else changes");

        // the own entry first, carrying the type definition: the definition moves to the entry left
        string first = Prefab("CarPrefab", "SWB6710EV01G", (true, "CarPrefab", "SWB6710EV01G", own), (false, "CarPrefab", "SWB6710EV01G", older));
        check(BusPackageExport.TryStripSelfIdentifiers(first, own, out stripped, out removed) && removed == 1, "identity: an own entry that defines the type goes too");
        var firstLeft = Parts(stripped);
        check(firstLeft.list.Items.Count == 1 && Field(firstLeft.list.Items[0], "$type") == definition && Field(firstLeft.list.Items[0], "m_Hash") == older && firstLeft.length == 1,
            "identity: the type definition moves to the entry left");

        // not the prefab itself: an earlier GUID, another name, another type, another asset's ID, no obsolete identifiers at all
        check(!BusPackageExport.TryStripSelfIdentifiers(Prefab("CarPrefab", "SWB6710EV01G", (true, "CarPrefab", "SWB6710EV01G", older)), own, out _, out _), "identity: an earlier GUID stays");
        check(!BusPackageExport.TryStripSelfIdentifiers(Prefab("CarPrefab", "SWB6710EV01G", (true, "CarPrefab", "SWB6710EV01G_LOD1 Mesh", own)), own, out _, out _), "identity: another name stays");
        check(!BusPackageExport.TryStripSelfIdentifiers(Prefab("CarPrefab", "SWB6710EV01G", (true, "RenderPrefab", "SWB6710EV01G", own)), own, out _, out _), "identity: another type stays");
        check(!BusPackageExport.TryStripSelfIdentifiers(one, other, out _, out _), "identity: a prefab whose asset ID is another is left alone");
        check(!BusPackageExport.TryStripSelfIdentifiers(Prefab("RenderPrefab", "Bus Mesh"), own, out _, out _), "identity: a prefab without obsolete identifiers is left alone");
        check(BusPackageExport.TryStripSelfIdentifiers(Prefab("RenderPrefab", "SWB6710EV01G_LOD1 Mesh", (true, "RenderPrefab", "SWB6710EV01G_LOD1 Mesh", own.ToUpperInvariant())), own, out _, out removed) && removed == 1,
            "identity: a mesh prefab's own entry goes whatever the case of its hash");

        // an entry something refers to is not taken out
        bool refused = false;
        try { BusPackageExport.TryStripSelfIdentifiers(one.Replace("\"m_Meshes\"", "\"m_Other\": \"$iref:21\",\n    \"m_Meshes\""), own, out _, out _); }
        catch (InvalidDataException) { refused = true; }
        check(refused, "identity: an entry referred to by $iref is refused");
        // Odin's dialect: values without a name (a Vector3's numbers), escapes in strings
        OdinPrefabText.Node dialect = OdinPrefabText.Parse("{\n  \"$type\": \"5|UnityEngine.Vector3, UnityEngine.CoreModule\",\n  0,\n  -1.5,\n  2E-05,\n  \"a\\\"b\\u0041\": [ true, null, {} ]\n}");
        check(dialect.Items.Count == 5 && dialect.Names[1] == null && dialect.Items[2].Text == "-1.5" && dialect.Items[3].Text == "2E-05" &&
            dialect.Names[4] == "a\"bA" && dialect.Items[4].Items.Count == 3 && dialect.Items[4].Items[2].Kind == OdinPrefabText.Kind.Object,
            "identity: the reader takes values without a name, bare numbers and escaped strings");

        // in a package: the prefab is written again, Stored, with its time stamp and attributes; its .cid and every other entry as they were
        string bus = "Bus_" + own + ".Prefab", mesh = "Mesh_" + other + ".Prefab", meshText = Prefab("RenderPrefab", "Bus Mesh");
        MakePackage(source, null, prefabs: new[] { (bus, one, own), (mesh, meshText, other) });
        File.WriteAllText(output, "old");
        var result = BusPackageExport.ExportChecked(source, output);
        check(result.RewrittenEntries.SequenceEqual(new[] { bus }) && result.RemovedIdentifiers == 1 && result.RemovedEntries.Length == 0 && result.SourceSha256 != result.OutputSha256,
            "identity: the prefab with an own entry is written again, the mesh prefab without one is not");
        using (var zip = ZipFile.OpenRead(output))
        {
            ZipArchiveEntry entry = zip.GetEntry(bus);
            string text;
            using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
            check(Parts(text).list.Items.Count == 0 && entry.LastWriteTime.Year == 2023 && entry.LastWriteTime.Month == 10 && entry.LastWriteTime.Day == 24 && entry.ExternalAttributes == 32,
                "identity: the entry written again has the list emptied, its time stamp and its attributes");
            using (var reader = new StreamReader(zip.GetEntry(bus + ".cid").Open())) check(reader.ReadToEnd() == own, "identity: the asset ID beside it is untouched");
            using (var reader = new StreamReader(zip.GetEntry(mesh).Open())) check(reader.ReadToEnd() == meshText, "identity: the other prefab is byte-identical");
            using (var stream = zip.GetEntry("payload.bin").Open()) { var bytes = new byte[8]; check(stream.Read(bytes, 0, 8) == 4 && bytes[0] == 1 && bytes[3] == 4, "identity: the payload is untouched"); }
        }
        // the archive is written by this code, not by ZipArchive (which writes Deflate for "no compression" on .NET Framework, the
        // game's runtime): every entry Stored, the untouched ones' local header and data byte for byte as in the source
        var sourceEntries = BusPackageExport.LocalEntries(source);
        var outputEntries = BusPackageExport.LocalEntries(output);
        check(outputEntries.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(sourceEntries.Keys.OrderBy(k => k, StringComparer.Ordinal)), "identity: the output holds the same entries as the source");
        check(outputEntries.Values.All(v => v.method == 0), "identity: every entry of the output is Stored");
        check(outputEntries.Where(e => e.Key != bus).All(e => e.Value.bytes.SequenceEqual(sourceEntries[e.Key].bytes)), "identity: the untouched entries' local headers and data are byte-identical");
        check(!outputEntries[bus].bytes.SequenceEqual(sourceEntries[bus].bytes) && outputEntries[bus].bytes.Take(8).SequenceEqual(sourceEntries[bus].bytes.Take(8)),
            "identity: the rewritten entry differs in content but keeps its local header's start");
        var again = BusPackageExport.ExportChecked(output, Path.Combine(temporary, "again.cok"));
        check(again.RewrittenEntries.Length == 0 && again.RemovedEntries.Length == 0 && again.SourceSha256 == again.OutputSha256, "identity: the export of an export is byte-identical");

        // each fix on its own
        MakePackage(source, Empty(), prefabs: new[] { (bus, one, own) });
        var cachesOnly = BusPackageExport.ExportChecked(source, output, null, BusPackageExport.Fixes.EmptyCaches);
        check(cachesOnly.RewrittenEntries.Length == 0 && cachesOnly.RemovedIdentifiers == 0 && cachesOnly.RemovedEntries.Length == 2, "identity: the cache fix alone leaves the prefabs");
        var cachesSource = BusPackageExport.LocalEntries(source);
        var cachesOutput = BusPackageExport.LocalEntries(output);
        check(!cachesOutput.ContainsKey(Cache) && cachesOutput.Count == cachesSource.Count - 2 && cachesOutput.All(e => e.Value.bytes.SequenceEqual(cachesSource[e.Key].bytes)),
            "identity: with the caches removed every other entry is byte-identical in place");
        var identitiesOnly = BusPackageExport.ExportChecked(source, output, null, BusPackageExport.Fixes.SelfIdentifiers);
        check(identitiesOnly.RemovedEntries.Length == 0 && identitiesOnly.RewrittenEntries.SequenceEqual(new[] { bus }), "identity: the identity fix alone leaves the caches");
        using (var zip = ZipFile.OpenRead(output)) check(zip.GetEntry(Cache) != null && zip.GetEntry(Cache + ".cid") != null, "identity: the empty cache stays when only identities are fixed");
        var none = BusPackageExport.ExportChecked(source, output, null, BusPackageExport.Fixes.None);
        check(none.RemovedEntries.Length == 0 && none.RewrittenEntries.Length == 0 && none.SourceSha256 == none.OutputSha256, "identity: no fix, a byte-identical copy");

        // a prefab without its asset ID beside it: refused, the output preserved
        MakePackage(source, null, prefabs: new[] { (bus, one, own) }, prefabCids: false);
        RejectedWithoutReplacing(check, source, output, "prefab without its asset ID", temporary);
    }

    /// <summary>A prefab's text as the game's Odin writer lays it out: a component before the identifiers (type ids defined on first use), an empty mesh list after.</summary>
    static string Prefab(string type, string name, params (bool define, string type, string name, string hash)[] aliases)
    {
        var text = new System.Text.StringBuilder();
        text.Append("{\n    \"$id\": 0,\n    \"$type\": \"0|Game.Prefabs.").Append(type).Append(", Game\",\n    \"name\": \"").Append(name).Append("\",\n    \"active\": true,\n    \"version\": 1,\n    \"m_prefabFormat\": 0,\n");
        text.Append("    \"components\": {\n        \"$id\": 1,\n        \"$type\": \"1|System.Collections.Generic.List`1[[Game.Prefabs.ComponentBase, Game]], mscorlib\",\n        \"$rlength\": ").Append(aliases.Length > 0 ? 2 : 1).Append(",\n        \"$rcontent\": [\n");
        text.Append("            {\n                \"$id\": 2,\n                \"$type\": \"2|Game.Prefabs.PublicTransport, Game\",\n                \"name\": \"PublicTransport\",\n                \"active\": true,\n                \"m_PassengerCapacity\": 51,\n                \"m_RoadWear\": {\n                    \"$type\": \"3|Unity.Mathematics.float2, Unity.Mathematics\",\n                    0.25,\n                    2\n                }\n            }");
        if (aliases.Length > 0)
        {
            text.Append(",\n            {\n                \"$id\": 3,\n                \"$type\": \"16|Game.Prefabs.ObsoleteIdentifiers, Game\",\n                \"name\": \"ObsoleteIdentifiers\",\n                \"active\": true,\n                \"m_PrefabIdentifiers\": {\n                    \"$id\": 20,\n                    \"$type\": \"17|Game.Prefabs.PrefabIdentifierInfo[], Game\",\n                    \"$rlength\": ").Append(aliases.Length).Append(",\n                    \"$rcontent\": [\n");
            for (int i = 0; i < aliases.Length; i++)
            {
                var alias = aliases[i];
                text.Append("                        {\n                            \"$id\": ").Append(21 + i).Append(",\n                            \"$type\": ").Append(alias.define ? "\"18|Game.Prefabs.PrefabIdentifierInfo, Game\"" : "18")
                    .Append(",\n                            \"m_Name\": \"").Append(alias.name).Append("\",\n                            \"m_Type\": \"").Append(alias.type).Append("\",\n                            \"m_Hash\": \"").Append(alias.hash).Append("\"\n                        }").Append(i + 1 < aliases.Length ? ",\n" : "\n");
            }
            text.Append("                    ]\n                }\n            }");
        }
        text.Append("\n        ]\n    },\n    \"m_Meshes\": {\n        \"$id\": 30,\n        \"$type\": \"19|Game.Prefabs.ObjectMeshInfo[], Game\",\n        \"$rlength\": 0,\n        \"$rcontent\": []\n    }\n}");
        return text.ToString();
    }

    /// <summary>The prefab entries of a package and how many of them carry their own identity (an ObsoleteIdentifiers entry whose hash is the entry's own .cid).</summary>
    static int SelfIdentities(string package, out int withOwn)
    {
        int prefabs = 0;
        withOwn = 0;
        using (var zip = ZipFile.OpenRead(package))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".Prefab", StringComparison.Ordinal)) continue;
                prefabs++;
                ZipArchiveEntry cid = zip.GetEntry(entry.FullName + ".cid");
                if (cid == null) continue;
                string own;
                using (var reader = new StreamReader(cid.Open())) own = reader.ReadToEnd().Trim();
                string text;
                using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
                if (text.IndexOf("\"m_Hash\": \"" + own + "\"", StringComparison.OrdinalIgnoreCase) >= 0) withOwn++;
            }
        }
        return prefabs;
    }

    static void RejectedWithoutReplacing(Action<bool, string> check, string source, string output, string name, string temporary)
    {
        File.WriteAllText(output, "preserve this destination");
        string old = BusPackageExport.FileHash(output), input = BusPackageExport.FileHash(source);
        bool rejected = false;
        try { BusPackageExport.ExportChecked(source, output); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "package: rejects " + name);
        check(BusPackageExport.FileHash(output) == old && BusPackageExport.FileHash(source) == input && Directory.GetFiles(temporary, "*.tmp").Length == 0,
            "package: rejection preserves both files and cleans its candidate for " + name);
    }

    static void MakePackage(string path, byte[] cache, bool sidecar = true, bool orphan = false, bool collision = false, bool compressed = false,
        (string name, string text, string cid)[] prefabs = null, bool prefabCids = true)
    {
        if (File.Exists(path)) File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data, CompressionLevel level = CompressionLevel.NoCompression)
            {
                var entry = zip.CreateEntry(name, level);
                entry.LastWriteTime = new DateTimeOffset(2023, 10, 24, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = 32;
                using (var stream = entry.Open()) stream.Write(data, 0, data.Length);
            }
            Add("payload.bin", new byte[] { 1, 2, 3, 4 }, compressed ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
            Add("payload.bin.cid", System.Text.Encoding.ASCII.GetBytes(Id));
            foreach (var prefab in prefabs ?? Array.Empty<(string, string, string)>())
            {
                Add(prefab.name, new System.Text.UTF8Encoding(false).GetBytes(prefab.text));
                if (prefabCids) Add(prefab.name + ".cid", System.Text.Encoding.ASCII.GetBytes(prefab.cid));
            }
            if (collision) Add("PAYLOAD.bin", new byte[] { 9 });
            if (cache != null) Add(Cache, cache);
            if ((cache != null && sidecar) || orphan) Add(Cache + ".cid", System.Text.Encoding.ASCII.GetBytes(Id));
        }
    }

    static byte[] Empty()
    {
        using (var stream = new MemoryStream())
        using (var w = new BinaryWriter(stream))
        {
            foreach (int value in new[] { 512, 3, 64, 2, 0, 0, 0, 0, 0 }) w.Write(value);
            w.Write((long)36); return stream.ToArray();
        }
    }

    static byte[] Valid(bool duplicate, bool disabled)
    {
        using (var stream = new MemoryStream())
        using (var w = new BinaryWriter(stream))
        {
            foreach (int value in new[] { 512, 3, 64, 2, 1, 1 + (duplicate ? 1 : 0) + (disabled ? 1 : 0), 1 }) w.Write(value);
            Category(w, false); w.Write(disabled ? 1 : 0);
            if (disabled) Category(w, true);
            w.Write(Enumerable.Repeat((byte)1, 16).ToArray()); w.Write(1); w.Write((short)0); w.Write(duplicate ? 1 : 0);
            if (duplicate) w.Write(Enumerable.Repeat((byte)2, 16).ToArray());
            for (int i = 0; i < 3; i++) { w.Write(0); w.Write(1); }
            w.Write(1); w.Write(stream.Position);
            w.Write(new byte[(512 + 16) * (512 + 16) * 5 / 4]); return stream.ToArray();
        }
    }

    static byte[] DisabledOnly()
    {
        using (var stream = new MemoryStream())
        using (var w = new BinaryWriter(stream))
        {
            foreach (int value in new[] { 512, 3, 64, 2, 0, 1, 1 }) w.Write(value);
            Category(w, true); w.Write(0); w.Write(0); w.Write(stream.Position); return stream.ToArray();
        }
    }

    static void Category(BinaryWriter w, bool disabled)
    {
        foreach (int value in new[] { disabled ? 1024 : 512, 512, 1, 0, disabled ? 0 : 1 }) w.Write(value);
        w.Write(disabled); w.Write(-1);
    }

    static string Repo()
    {
        for (string dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "CDLC.BusDriver.csproj"))) return dir;
        return null;
    }
}
