using System.Collections.Generic;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Which of a native-cab bus's surfaces the pack's game asset draws and which the runtime mesh keeps (profile "cabRender": "native",
    /// BusNativeCab): the runtime parts the asset stands in for, the runtime parts that stay, and the test that finds the asset's own
    /// copies of the parts that stay, so a surface is drawn once (a moving part's copy is dropped, a lamp face's copy shows while unlit).
    /// </summary>
    internal static class NativeCabOwnership
    {
        // a corner of the asset lies on a runtime corner within this: both come from the same source, through float32 and the frame change
        internal const float kMatchMetres = 0.0015f;

        /// <summary>The runtime parts the asset stands in for: static parts; selected wrap faces and moving door overlays remain runtime-owned.</summary>
        internal static IEnumerable<int> ReplacedParts(VehicleMesh mesh)
        {
            yield return BusInterior.Shell;
            for (int part = BusInterior.Fittings; part <= BusInterior.Livery; part++) yield return part;
            for (int part = mesh.FirstSurface; part < mesh.FirstStrap; part++)
                if (mesh.SurfaceParent(part) == BusInterior.Shell) yield return part;
        }

        /// <summary>The runtime parts that keep moving in the driver's view: their asset copies are dropped.</summary>
        internal static IEnumerable<int> MovingParts(VehicleMesh mesh)
        {
            for (int part = BusInterior.SteeringWheel; part <= BusInterior.DoorRearB; part++) yield return part;
            for (int part = BusInterior.WheelFL; part <= BusInterior.WheelRR; part++) yield return part;
            for (int part = BusInterior.PartCount; part < mesh.FirstSurface; part++) yield return part;
            for (int part = mesh.FirstStrap; part < mesh.PartCount; part++) yield return part;
        }

        /// <summary>
        /// The lamp faces the runtime mesh lights (head, tail, brake, the two indicators, reverse), shown only while their lamp is on.
        /// The asset holds the same faces unlit (its light buffer is all zero in the cab), within 0.13 mm of the runtime ones in every
        /// pack measured: a lit face and its unlit copy shared a depth. The copies are kept as pieces of their own (<see cref="Split"/>)
        /// and hidden while a lamp they copy is lit, so a lamp looks unlit exactly as the asset draws it and lit as the runtime mesh does.
        /// </summary>
        internal static IEnumerable<int> LampParts()
        {
            for (int part = BusInterior.HeadLamps; part <= BusInterior.ReverseLamps; part++) yield return part;
        }

        /// <summary>The bit of a lamp part in a set of lit lamps (BusInterior.LitLampParts): bit 0 the head lamps .. bit 5 the reverse lamps.</summary>
        internal static int LampBit(int part) => 1 << (part - BusInterior.HeadLamps);

        /// <summary>
        /// A lamp face's group in the cut: LampGroup + the bits (LampBit) of the lamp parts it is a copy of. A face can be several
        /// parts' at once (a lens the tail lamp and the brake lamp both light: 312 triangles of the SWB6710EV01G).
        /// </summary>
        internal const int LampGroup = 1 << 16;
        internal static bool IsLampGroup(int group) => group > LampGroup;
        internal static int LampsOf(int group) => IsLampGroup(group) ? group - LampGroup : 0;

        /// <summary>
        /// What the native cab does with a triangle of the asset: drops it (a copy of a moving part, or inside a card reader the mod
        /// draws), keeps it as the unlit face of a lamp part, or keeps it as body.
        /// </summary>
        internal sealed class Split
        {
            private readonly Corners m_Moving;
            private readonly List<(int part, Corners corners)> m_Lamps = new List<(int, Corners)>();
            /// <summary>The runtime corners the moving parts' copies are found by (the log line).</summary>
            internal int MovingCorners => m_Moving.Count;

            internal Split(VehicleProfile profile)
            {
                m_Moving = new Corners(profile);
                foreach (int part in LampParts())
                    if (profile.Mesh.TriangleCount(part, 0) > 0)
                        m_Lamps.Add((part, new Corners(profile.Mesh, new[] { part })));
            }

            /// <summary>NativeMeshCut.Drop, a lamp group (LampGroup + the lamp parts the face is a copy of), or NativeMeshCut.Body. A moving part's copy is dropped even on lamp corners.</summary>
            internal int GroupOf(float3 a, float3 b, float3 c)
            {
                if (m_Moving.Covers(a, b, c)) return NativeMeshCut.Drop;
                int lamps = 0;
                foreach ((int part, Corners corners) in m_Lamps)
                    if (corners.Covers(a, b, c)) lamps |= LampBit(part);
                return lamps != 0 ? LampGroup + lamps : NativeMeshCut.Body;
            }
        }

        /// <summary>
        /// Runtime parts' corners on a 1.5 mm grid (each corner marked in its cell and the 26 around it, so a lookup is one cell), and
        /// card readers' housings: a triangle is covered when its three corners are marked or its centre is in a housing.
        /// </summary>
        internal sealed class Corners
        {
            private readonly HashSet<long> m_Cells = new HashSet<long>();
            private readonly List<(float3 origin, quaternion inverse)> m_Readers = new List<(float3, quaternion)>();
            internal int Count { get; }

            /// <summary>What the native cab drops: the moving parts' copies and the card readers the mod draws.</summary>
            internal Corners(VehicleProfile profile) : this(profile.Mesh, MovingParts(profile.Mesh), profile.CardReaders) { }

            internal Corners(VehicleMesh mesh, IEnumerable<int> parts, (float3 origin, quaternion frame, float xSign)[] readers = null)
            {
                var corners = new List<float3>();
                foreach (int part in parts)
                    mesh.Corners(part, 0, corners);
                Count = corners.Count;
                foreach (float3 c in corners)
                {
                    int3 cell = Cell(c);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dz = -1; dz <= 1; dz++)
                                m_Cells.Add(Key(cell + new int3(dx, dy, dz)));
                }
                if (readers != null)
                    foreach ((float3 origin, quaternion frame, float _) in readers)
                        m_Readers.Add((origin, math.inverse(frame)));
            }

            private static int3 Cell(float3 p) => (int3)math.floor(p / kMatchMetres);
            private static long Key(int3 c) => ((long)(c.x & 0x1FFFFF) << 42) | ((long)(c.y & 0x1FFFFF) << 21) | (long)(c.z & 0x1FFFFF);

            internal bool Covers(float3 a, float3 b, float3 c)
            {
                if (m_Cells.Count > 0 && m_Cells.Contains(Key(Cell(a))) && m_Cells.Contains(Key(Cell(b))) && m_Cells.Contains(Key(Cell(c))))
                    return true;
                if (m_Readers.Count == 0) return false;
                float3 centre = (a + b + c) / 3f;
                foreach ((float3 origin, quaternion inverse) in m_Readers)
                {
                    // BusInterior.CardReader: the housing 0.17 x 0.27 x 0.09 about its frame's origin, the face plate 0.05 out along +Z
                    float3 local = math.mul(inverse, centre - origin);
                    if (math.abs(local.x) <= 0.090f && math.abs(local.y) <= 0.140f && local.z >= -0.050f && local.z <= 0.056f)
                        return true;
                }
                return false;
            }
        }
    }
}
