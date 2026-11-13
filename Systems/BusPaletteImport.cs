using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.AssetPipeline;
using Colossal.IO.AssetDatabase;
using Colossal.Json;
using Game.Prefabs;
using TextureAsset = Colossal.IO.AssetDatabase.TextureAsset;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Local import settings for an authored model's constant-colour palette (the strict pipeline, SWB6109EV12G first). Small UV
    /// islands on dense triangles must not sample neighbouring palette cells.
    /// Staging leaves the authored FBX/PNG directory and other imports intact.
    /// </summary>
    internal sealed class BusPaletteImport : IDisposable
    {
        private readonly string m_Root;
        private readonly string m_TemporaryParent;
        internal string Source { get; }

        private BusPaletteImport(string root, string temporaryParent, string model)
        {
            m_Root = root;
            m_TemporaryParent = temporaryParent;
            Source = Path.Combine(root, model);
        }

        internal static BusPaletteImport Create(string source, string model)
        {
            source = Path.GetFullPath(source);
            if (!string.Equals(new DirectoryInfo(source).Name, model, StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(source, model + ".fbx")))
                throw new InvalidOperationException("Palette import requires the exact " + model + " asset directory.");
            if (Directory.GetDirectories(source).Length != 0)
                throw new InvalidOperationException("Palette import expects a flat FBX/PNG source directory.");

            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CDLC.BusDriver", "palette-import"));
            string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            var staged = new BusPaletteImport(root, parent, model);
            try
            {
                Directory.CreateDirectory(staged.Source);
                var settings = Settings.GetDefault(model);
                settings.assetMapping = new Dictionary<string, List<string>>();
                var colour = Settings.Defaults.Texture.GetDefault();
                var normal = Settings.Defaults.Texture.GetNormal();
                var linear = Settings.Defaults.Texture.GetLinear();
                // Preserve the installed importer's existing glass sampling contract.
                var glassNormal = Settings.Defaults.Texture.GetNormal(UnityEngine.TextureWrapMode.Repeat);
                var glassLinear = Settings.Defaults.Texture.GetLinear(UnityEngine.TextureWrapMode.Repeat);
                colour.computeMips = normal.computeMips = linear.computeMips =
                    glassNormal.computeMips = glassLinear.computeMips = false;
                const string colourKey = "#6109PaletteColour.Texture";
                const string normalKey = "#6109PaletteNormal.Texture";
                const string linearKey = "#6109PaletteLinear.Texture";
                settings.importSettings.Add(colourKey, colour);
                settings.importSettings.Add(normalKey, normal);
                settings.importSettings.Add(linearKey, linear);
                const string glassNormalKey = "#6109PaletteGlassNormal.Texture";
                const string glassLinearKey = "#6109PaletteGlassLinear.Texture";
                settings.importSettings.Add(glassNormalKey, glassNormal);
                settings.importSettings.Add(glassLinearKey, glassLinear);
                int textures = 0;
                foreach (string file in Directory.GetFiles(source))
                {
                    string filename = Path.GetFileName(file);
                    string extension = Path.GetExtension(file);
                    if (!filename.StartsWith(model, StringComparison.Ordinal) ||
                        (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                         !extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Unrecognised palette source file: " + filename);
                    if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        RequirePaletteSize(file);
                        textures++;
                        string stem = Path.GetFileNameWithoutExtension(file);
                        string key;
                        if (stem.EndsWith("_BaseColor", StringComparison.Ordinal)) key = colourKey;
                        else if (stem.EndsWith("_Gls_Normal", StringComparison.Ordinal)) key = glassNormalKey;
                        else if (stem.EndsWith("_Gls_MaskMap", StringComparison.Ordinal)) key = glassLinearKey;
                        else if (stem.EndsWith("_Normal", StringComparison.Ordinal)) key = normalKey;
                        else if (stem.EndsWith("_MaskMap", StringComparison.Ordinal) ||
                                 stem.EndsWith("_ControlMask", StringComparison.Ordinal)) key = linearKey;
                        // These two are inputs to the lamp-ID baker, whose existing
                        // uncompressed, linear, single-mip settings must remain.
                        else if (stem.EndsWith("_Emissive0", StringComparison.Ordinal) ||
                                 stem.EndsWith("_EmissiveID0", StringComparison.Ordinal)) key = null;
                        else throw new InvalidOperationException("Unknown palette texture: " + filename);
                        if (key != null) settings.assetMapping.Add(stem + "{Texture}", new List<string> { key });
                    }
                    File.Copy(file, Path.Combine(staged.Source, filename), overwrite: false);
                }
                if (textures == 0) throw new InvalidOperationException("No palette textures found.");
                // The baker creates these in memory, then consults the same
                // settings using its generated .Texture name.
                foreach (string suffix in new[] { "", "_LOD1", "_LOD2" })
                    settings.assetMapping.Add(model + suffix + "_Emissive{Texture}", new List<string> { colourKey });
                File.WriteAllText(Path.Combine(staged.Source, Settings.kMainSettings), settings.ToJSONString());
                return staged;
            }
            catch
            {
                staged.Dispose();
                throw;
            }
        }

        private static void RequirePaletteSize(string path)
        {
            var header = new byte[24];
            using (var input = File.OpenRead(path))
            {
                if (input.Read(header, 0, header.Length) != header.Length ||
                    header[0] != 137 || header[1] != 80 || header[2] != 78 || header[3] != 71 ||
                    header[12] != 73 || header[13] != 72 || header[14] != 68 || header[15] != 82 ||
                    header[16] != 0 || header[17] != 0 || header[18] != 0 || header[19] != 64 ||
                    header[20] != 0 || header[21] != 0 || header[22] != 0 || header[23] != 64)
                    throw new InvalidOperationException("Single-mip import is limited to this model's 64x64 palettes: " + path);
            }
        }

        /// <summary>
        /// Run after SetUpMesh shares the three Gls aliases. Inspect persisted
        /// texture headers without creating or changing a Unity texture.
        /// </summary>
        internal static void Verify(RenderPrefab mesh, string model)
        {
            if (mesh == null || !string.Equals(mesh.name, model + " Mesh", StringComparison.Ordinal))
                throw new InvalidOperationException("Palette verification requires the exact " + model + " root mesh.");
            if (!mesh.TryGet(out LodProperties lods) || lods.m_LodMeshes == null || lods.m_LodMeshes.Length != 2 ||
                lods.m_LodMeshes[0] == null || lods.m_LodMeshes[1] == null)
                throw new InvalidDataException(model + " palette verification requires all three native LODs.");

            var surfaces = new HashSet<string>(StringComparer.Ordinal);
            var textures = new HashSet<string>(StringComparer.Ordinal);
            foreach (RenderPrefab lod in new[] { mesh, lods.m_LodMeshes[0], lods.m_LodMeshes[1] })
            {
                if (lod.materialCount != 2)
                    throw new InvalidDataException(lod.name + " must retain separate body and glass surfaces.");
                for (int i = 0; i < lod.materialCount; i++)
                {
                    SurfaceAsset surface = lod.GetSurfaceAsset(i);
                    if (surface == null) throw new InvalidDataException(lod.name + " has a missing surface.");
                    if (!surfaces.Add(surface.id.ToString())) continue;
                    surface.LoadProperties(false);
                    try
                    {
                        bool glass = surface.name.IndexOf("_Gls", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (surface.textures.Count != (glass ? 3 : 5))
                            throw new InvalidDataException(surface.name + " has an unexpected palette map count.");
                        foreach (var binding in surface.textures)
                        {
                            TextureAsset texture = binding.Value;
                            if (texture == null) throw new InvalidDataException(surface.name + " has a missing texture.");
                            textures.Add(texture.id.ToString());
                            VerifyTextureHeader(texture, glass, binding.Key);
                        }
                    }
                    finally { surface.UnloadProperties(); }
                }
            }
            // Three body surfaces with five maps each, plus one shared Gls
            // surface with three maps. Missing/duplicate references cannot pass.
            if (surfaces.Count != 4 || textures.Count != 18)
                throw new InvalidDataException(model + " expected four surfaces and eighteen palette textures; found " +
                    surfaces.Count + " and " + textures.Count + ".");
        }

        internal static void VerifyAssembly(IEnumerable<(RenderPrefab mesh, string model)> sections)
        {
            var surfaceIds = new HashSet<string>(StringComparer.Ordinal);
            var textureIds = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            foreach (var section in sections)
            {
                Verify(section.mesh, section.model);
                count++;
                section.mesh.TryGet(out LodProperties lods);
                foreach (RenderPrefab lod in new[] { section.mesh }.Concat(lods.m_LodMeshes))
                    foreach (SurfaceAsset surface in lod.surfaceAssets)
                    {
                        if (!surfaceIds.Add(surface.id.ToString())) continue;
                        surface.LoadProperties(false);
                        try { foreach (TextureAsset texture in surface.textures.Values) textureIds.Add(texture.id.ToString()); }
                        finally { surface.UnloadProperties(); }
                    }
            }
            if (count < 1 || surfaceIds.Count != 4 * count || textureIds.Count != 18 * count)
                throw new InvalidDataException("Each assembly section must retain its own four surfaces and eighteen palette textures.");
        }

        private static void VerifyTextureHeader(TextureAsset texture, bool glass, string property)
        {
            try
            {
                using (var reader = new BinaryReader(texture.GetReadStream()))
                {
                    ushort version = reader.ReadUInt16();
                    ushort width = reader.ReadUInt16(), height = reader.ReadUInt16(), depth = reader.ReadUInt16();
                    byte mips = reader.ReadByte();
                    byte format = reader.ReadByte(), dimension = reader.ReadByte(), filter = reader.ReadByte();
                    byte wrap = reader.ReadByte(), aniso = reader.ReadByte();
                    int compression = reader.ReadInt32();
                    bool colour = property == "_BaseColorMap" || property == "_EmissiveColorMap";
                    bool linear = property == "_NormalMap" || property == "_MaskMap" || property == "_ControlMask";
                    if ((!colour && !linear) ||
                        (glass && property != "_BaseColorMap" && property != "_NormalMap" && property != "_MaskMap"))
                        throw new InvalidDataException("Unknown palette texture binding: " + property);
                    // Audited TextureAsset v5:108=RGBA_BC7_SRGB,109=RGBA_BC7_UNorm.
                    byte expectedFormat = colour ? (byte)108 : (byte)109;
                    byte expectedWrap = glass && (property == "_NormalMap" || property == "_MaskMap")
                        ? (byte)UnityEngine.TextureWrapMode.Repeat : (byte)UnityEngine.TextureWrapMode.Clamp;
                    if (version != TextureAsset.kFormatVersion || width != 64 || height != 64 || depth != 1 || mips != 1 ||
                        format != expectedFormat || dimension != (byte)UnityEngine.Rendering.TextureDimension.Tex2D ||
                        filter != (byte)UnityEngine.FilterMode.Trilinear || wrap != expectedWrap || aniso != 16 || compression != -1)
                        throw new InvalidDataException(texture.name + " changed its audited palette header: binding=" + property +
                            ", version=" + version + ", dimensions=" + width + "x" + height + "x" + depth +
                            ", mips=" + mips + ", format=" + format + ", dimension=" + dimension +
                            ", filter=" + filter + ", wrap=" + wrap + ", aniso=" + aniso + ", compression=" + compression + ".");
                }
            }
            catch (EndOfStreamException error)
            {
                throw new InvalidDataException("Truncated palette texture header: " + texture.name, error);
            }
        }

        public void Dispose()
        {
            string root = Path.GetFullPath(m_Root);
            string parent = m_TemporaryParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
                throw new InvalidOperationException("Refusing to remove an unexpected palette staging directory.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
