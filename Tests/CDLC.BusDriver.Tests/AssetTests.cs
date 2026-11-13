using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// The bus's vehicle-asset files the mod ships (Asset/CDLCBus12, made by the asset exporter; BusVehicleAssetSystem imports them in
// the game): the names follow the game importer's convention (asset, LOD, "_Gls" glass material, texture suffix), every LOD has
// its body and its glass with their texture sets, the models are binary FBX 7.4, the textures square power-of-two 8-bit PNG,
// and each emissive map matches its ID map in size and format (the importer's lamp baker requires it); the package made from
// them is there and up to date.
static class AssetTests
{
    const string Name = "CDLCBus12";
    static readonly string[] s_Body = { "BaseColor", "MaskMap", "Normal", "ControlMask", "Emissive0", "EmissiveID0" };
    static readonly string[] s_Glass = { "BaseColor", "MaskMap", "Normal" };

    internal static void Run(Action<bool, string> check)
    {
        string dir = AssetDir();
        check(dir != null, $"Asset/{Name} found in the repo");
        if (dir == null)
            return;

        string[] files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
        var pattern = new Regex($"^{Name}(_LOD[12])?(_Gls)?(_({string.Join("|", s_Body)}))?\\.(fbx|png)$");
        string[] odd = files.Where(f => !pattern.IsMatch(f)).ToArray();
        check(odd.Length == 0, $"asset file names follow the importer's convention ({string.Join(", ", odd)})");

        int expected = 0;
        for (int lod = 0; lod <= 2; lod++)
        {
            string body = lod == 0 ? Name : $"{Name}_LOD{lod}";
            string glass = body + "_Gls";
            foreach (string model in new[] { body, glass })
            {
                string path = Path.Combine(dir, model + ".fbx");
                check(File.Exists(path) && FbxVersion(path) == 7400, $"{model}.fbx is a binary FBX 7.4");
                expected++;
            }

            foreach ((string mesh, string[] maps) in new[] { (body, s_Body), (glass, s_Glass) })
            {
                foreach (string map in maps)
                {
                    string path = Path.Combine(dir, $"{mesh}_{map}.png");
                    expected++;
                    if (!File.Exists(path))
                    {
                        check(false, $"{mesh}_{map}.png exists");
                        continue;
                    }

                    (int w, int h, int depth, int type) = PngHeader(path);
                    check(w == h && w >= 16 && (w & (w - 1)) == 0 && depth == 8, $"{mesh}_{map}.png: {w} x {h}, {depth} bit (square, a power of two, 8 bit)");
                }
            }

            string emissive = Path.Combine(dir, body + "_Emissive0.png"), ids = Path.Combine(dir, body + "_EmissiveID0.png");
            if (File.Exists(emissive) && File.Exists(ids))
                check(PngHeader(emissive) == PngHeader(ids), $"{body}: the emissive map and its ID map have the same size and format");
        }

        check(files.Length == expected, $"{files.Length} asset files, {expected} expected");

        // The package the mod ships (pack_asset.sh), made from these files: there, and not older than any of them.
        var package = new FileInfo(Path.Combine(Path.GetDirectoryName(dir), Name + ".cok"));
        DateTime newest = Directory.GetFiles(dir).Max(f => File.GetLastWriteTimeUtc(f));
        check(package.Exists && package.Length > 1000000, $"Asset/{Name}.cok is packed ({(package.Exists ? package.Length : 0)} bytes)");
        check(package.Exists && package.LastWriteTimeUtc >= newest, $"Asset/{Name}.cok is newer than its model files (run pack_asset.sh after build_asset.sh)");
    }

    static string AssetDir()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "Asset", Name);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir, "CDLC.BusDriver.csproj")))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    static int FbxVersion(string path)
    {
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            byte[] magic = reader.ReadBytes(23);
            if (System.Text.Encoding.ASCII.GetString(magic, 0, 18) != "Kaydara FBX Binary")
                return -1;
            return (int)reader.ReadUInt32();
        }
    }

    // The IHDR fields: width, height, bit depth, colour type.
    static (int, int, int, int) PngHeader(string path)
    {
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            byte[] head = reader.ReadBytes(29);
            if (head.Length < 29 || head[1] != 'P' || head[12] != 'I' || head[13] != 'H')
                return (0, 0, 0, 0);
            int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            return (w, h, head[24], head[25]);
        }
    }
}
