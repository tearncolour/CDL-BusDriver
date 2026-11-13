using System;
using System.IO;
using System.IO.Compression;
using CDLC.BusDriver;

static class BusPackageGlassTests
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "cdlc-glass-surface-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "fixture.cok");
        try
        {
            Write(path, .65f);
            BusPackageGlass.Verify(path);
            check(true, "package glass: saved coat and both disabled prepass switches are read from surface bytes");
            // the coat is the model's own (pack.glassCoatMask): the surface must carry that value, not the audited default
            Write(path, .4f);
            BusPackageGlass.Verify(path, null, .4f);
            check(true, "package glass: a model's own coat 0.4 is accepted when the surface carries it");
            foreach (float asked in new[] { .65f, .5f })
            {
                bool rejected = false;
                try { BusPackageGlass.Verify(path, null, asked); }
                catch (InvalidDataException) { rejected = true; }
                check(rejected, "package glass: a surface with coat 0.4 is rejected for a model that asks " + asked);
            }
            bool defaultRejected = false;
            try { BusPackageGlass.Verify(path); }
            catch (InvalidDataException) { defaultRejected = true; }
            check(defaultRejected, "package glass: without a model's coat the audited 0.65 is still what is required");
            Write(path, .65f);
            foreach (float? value in new float?[] { null, 0f, float.NaN, float.PositiveInfinity })
            {
                Write(path, value);
                bool rejected = false;
                try { BusPackageGlass.Verify(path); }
                catch (InvalidDataException) { rejected = true; }
                check(rejected, "package glass: rejects missing/incorrect/nonfinite persisted coat " + value);
            }
            foreach (float? value in new float?[] { null, 1f, float.Epsilon, float.NaN, float.PositiveInfinity })
                foreach (bool ssr in new[] { true, false })
                {
                    Write(path, .65f, ssr ? value : 0f, ssr ? 0f : value);
                    bool rejected = false;
                    try { BusPackageGlass.Verify(path); }
                    catch (InvalidDataException) { rejected = true; }
                    check(rejected, "package glass: rejects missing/enabled/nonfinite " +
                        (ssr ? "transparent SSR " : "transparent prepass ") + value);
                }
            WriteAssembly(path, "Front_Gls_fixture.Surface", "Rear_Gls_fixture.Surface");
            BusPackageGlass.Verify(path, new[] { "Front", "Rear" });
            check(true, "package glass: verifies both independently declared section surfaces");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("Rear_Gls_fixture.Surface").Delete();
                using (var writer = new BinaryWriter(zip.CreateEntry("Rear_Gls_fixture.Surface").Open()))
                {
                    writer.Write((ushort)1); writer.Write(11); writer.Write(false); writer.Write((byte)255); writer.Write(3);
                    writer.Write("_CoatMask"); writer.Write(.65f);
                    writer.Write("_ReceivesSSRTransparent"); writer.Write(1f);
                    writer.Write("_TransparentDepthPrepassEnable"); writer.Write(0f);
                }
            }
            bool badRear = false;
            try { BusPackageGlass.Verify(path, new[] { "Front", "Rear" }); }
            catch (InvalidDataException) { badRear = true; }
            check(badRear, "package glass: checks persisted compatibility properties on the rear surface too");
            foreach (string[] entries in new[]
            {
                new[] { "Front_Gls_fixture.Surface" },
                new[] { "Front_Gls_fixture.Surface", "Other_Gls_fixture.Surface" },
                new[] { "Front_Gls_fixture.Surface", "Rear_Gls_fixture.Surface", "Extra_Gls_fixture.Surface" },
                new[] { "Front_Gls_fixture.Surface", "Front_Gls_duplicate.Surface" },
            })
            {
                WriteAssembly(path, entries);
                bool rejected = false;
                try { BusPackageGlass.Verify(path, new[] { "Front", "Rear" }); }
                catch (InvalidDataException) { rejected = true; }
                check(rejected, "package glass: rejects missing, undeclared or duplicated assembly surfaces");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    static void WriteAssembly(string path, params string[] names)
    {
        if (File.Exists(path)) File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (string name in names)
                using (var writer = new BinaryWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open()))
                {
                    writer.Write((ushort)1); writer.Write(11); writer.Write(false); writer.Write((byte)255); writer.Write(3);
                    writer.Write("_CoatMask"); writer.Write(.65f);
                    writer.Write("_ReceivesSSRTransparent"); writer.Write(0f);
                    writer.Write("_TransparentDepthPrepassEnable"); writer.Write(0f);
                }
    }

    static void Write(string path, float? coat, float? ssr = 0f, float? prepass = 0f)
    {
        if (File.Exists(path)) File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new BinaryWriter(zip.CreateEntry("SWB6109EV12G_Gls_fixture.Surface", CompressionLevel.NoCompression).Open()))
        {
            writer.Write((ushort)1); writer.Write(11); writer.Write(false);
            writer.Write((byte)255);
            writer.Write((coat.HasValue ? 1 : 0) + (ssr.HasValue ? 1 : 0) + (prepass.HasValue ? 1 : 0));
            if (coat.HasValue) { writer.Write("_CoatMask"); writer.Write(coat.Value); }
            if (ssr.HasValue) { writer.Write("_ReceivesSSRTransparent"); writer.Write(ssr.Value); }
            if (prepass.HasValue) { writer.Write("_TransparentDepthPrepassEnable"); writer.Write(prepass.Value); }
        }
    }
}
