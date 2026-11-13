using System;
using System.IO;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>The joint a two-section pack's profile declares (CJA2, read, prepared and drawn by CDL Asset): its file, hash and
    /// verified bend range. The cab draws it only when the pack's articulation manifest names the same file and hash.</summary>
    internal sealed class VehicleArticulation
    {
        internal const string Format = "cdlc-joint-v2";
        internal readonly string MeshPath, Sha256;
        internal readonly float MaxBendDegrees;

        internal VehicleArticulation(string meshPath, string sha256, float maxBendDegrees)
        {
            if (string.IsNullOrEmpty(meshPath) || sha256 == null || sha256.Length != 64)
                throw new InvalidDataException("Joint resource path/hash missing");
            for (int i = 0; i < sha256.Length; i++)
                if (!Uri.IsHexDigit(sha256[i])) throw new InvalidDataException("Joint SHA256 is not hexadecimal");
            if (float.IsNaN(maxBendDegrees) || maxBendDegrees < 5 || maxBendDegrees > 89)
                throw new InvalidDataException("Joint bend limit must be 5..89 degrees");
            MeshPath = meshPath; Sha256 = sha256; MaxBendDegrees = maxBendDegrees;
        }
    }
}
