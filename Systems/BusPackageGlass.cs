using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CDLC.BusDriver.Interior.Models;

namespace CDLC.BusDriver
{
    /// <summary>Reads the persisted Surface bytes, independently of loaded Unity materials.</summary>
    internal static class BusPackageGlass
    {
        internal static void Verify(string packagePath) => Verify(packagePath, null, PackSettings.AuditedGlassCoatMask);

        internal static void Verify(string packagePath, IEnumerable<string> sectionPrefabs) =>
            Verify(packagePath, sectionPrefabs, PackSettings.AuditedGlassCoatMask);

        /// <summary>One explicitly named shared glass surface per section; no unrelated glass may enter the package. coatMask: the
        /// model's pack.glassCoatMask, which every persisted surface must carry.</summary>
        internal static void Verify(string packagePath, IEnumerable<string> sectionPrefabs, float coatMask)
        {
            using (var package = ZipFile.OpenRead(packagePath))
            {
                var glass = package.Entries.Where(e => e.FullName.EndsWith(".Surface", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.IndexOf("_Gls_", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                string[] expected = sectionPrefabs?.ToArray();
                if (expected == null)
                {
                    if (glass.Length != 1) throw new InvalidDataException("The vehicle requires exactly one persisted shared glass surface.");
                }
                else
                {
                    if (expected.Length == 0 || expected.Distinct(StringComparer.Ordinal).Count() != expected.Length || glass.Length != expected.Length)
                        throw new InvalidDataException("The assembly requires one distinct shared glass surface per declared section.");
                    foreach (string prefab in expected)
                        if (glass.Count(e => Path.GetFileName(e.FullName).StartsWith(prefab + "_Gls_", StringComparison.Ordinal)) != 1)
                            throw new InvalidDataException("Missing or duplicated persisted glass surface for section " + prefab + ".");
                }
                foreach (ZipArchiveEntry entry in glass) VerifySurface(entry, coatMask);
            }
        }

        private static void VerifySurface(ZipArchiveEntry entry, float coatMask)
        {
                try
                {
                    using (var reader = new BinaryReader(entry.Open()))
                    {
                        // SurfaceAsset.SaveData: version, material hash, VT flag;
                        // then the nullable string/float dictionary (0xff=present).
                        if (reader.ReadUInt16() != 1 || reader.ReadInt32() != 11 || reader.ReadByte() != 0)
                            throw new InvalidDataException(entry.FullName + " has an unknown persisted glass format/template.");
                        if (reader.ReadByte() != 255)
                            throw new InvalidDataException(entry.FullName + " has no persisted glass float properties.");
                        int count = reader.ReadInt32();
                        if (count < 0 || count > 4096) throw new InvalidDataException("Invalid persisted glass float count.");
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        var required = new Dictionary<string, float>(StringComparer.Ordinal)
                        {
                            ["_CoatMask"] = coatMask,
                            // HDRP otherwise enables TransparentDepthPrepass for transparent SSR even when
                            // its explicit prepass toggle is zero. Both settings must survive packaging.
                            ["_ReceivesSSRTransparent"] = 0f,
                            ["_TransparentDepthPrepassEnable"] = 0f,
                        };
                        for (int i = 0; i < count; i++)
                        {
                            string name = reader.ReadString();
                            float value = reader.ReadSingle();
                            if (!names.Add(name)) throw new InvalidDataException("Duplicate persisted glass float property.");
                            if (!required.TryGetValue(name, out float expected)) continue;
                            // A tiny positive flag still enables the pass: zero-valued switches are exact.
                            if (float.IsNaN(value) || float.IsInfinity(value) ||
                                (expected == 0f ? value != 0f : Math.Abs(value - expected) > .0001f))
                                throw new InvalidDataException($"{entry.FullName}: persisted glass {name} differs from {expected}.");
                        }
                        foreach (string name in required.Keys)
                            if (!names.Contains(name))
                                throw new InvalidDataException(entry.FullName + ": persisted glass is missing " + name + ".");
                    }
                }
                catch (EndOfStreamException error)
                {
                    throw new InvalidDataException("Truncated persisted glass surface " + entry.FullName + ".", error);
                }
        }
    }
}
