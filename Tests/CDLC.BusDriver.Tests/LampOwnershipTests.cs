using System;
using System.Collections.Generic;
using System.Linq;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

// A native-cab bus draws its lamp faces twice at one depth: the pack's game asset holds them unlit (its light buffer is all zero in
// the cab) and the runtime mesh shows the same faces lit (parts 23..28), within 0.13 mm of each other in every pack measured (the
// ZK6128H session's report of 12 November; LampOwnership.cs measures it on a pack's files). The cab now cuts the asset's copies of
// those faces into pieces of their own and hides a piece while its lamp is lit: unlit, the lamp is the asset's own face; lit, the
// runtime's alone. Checked here: which lamp parts are lit for a pose (one rule for the runtime mesh and the cab), and how the cab
// splits an asset's triangles, on a real pack's runtime mesh.
static class LampOwnershipTests
{
    const string Prefix = "lamp ownership: ";

    internal static void Run(Action<bool, string> check)
    {
        LitParts(check);
        Split(check);
    }

    static int Bit(int part) => NativeCabOwnership.LampBit(part);

    static void LitParts(Action<bool, string> check)
    {
        check(Bit(BusInterior.HeadLamps) == 1 && Bit(BusInterior.ReverseLamps) == 32 && NativeCabOwnership.LampParts().SequenceEqual(new[] { 23, 24, 25, 26, 27, 28 }),
            Prefix + "the lamp parts are 23..28, one bit each from the head lamps");

        BusInteriorSpec solo = TestPacks.Spec("SWB6710EV01G");
        var all = BusInterior.ExteriorLamps.Head | BusInterior.ExteriorLamps.Tail | BusInterior.ExteriorLamps.Brake |
                  BusInterior.ExteriorLamps.Left | BusInterior.ExteriorLamps.Right | BusInterior.ExteriorLamps.Reverse;
        var pose = new BusInterior.Pose { Lamps = all };
        check(BusInterior.LitLampParts(pose, solo) == 63, Prefix + "a solo bus with every lamp on shows all six lamp parts lit");
        pose.Lamps = BusInterior.ExteriorLamps.None;
        check(BusInterior.LitLampParts(pose, solo) == 0, Prefix + "no lamp on: none lit (the asset's unlit faces all show)");
        pose.Lamps = BusInterior.ExteriorLamps.Brake | BusInterior.ExteriorLamps.Left;
        check(BusInterior.LitLampParts(pose, solo) == (Bit(BusInterior.BrakeLamps) | Bit(BusInterior.IndicatorL)),
            Prefix + "braking with the left indicator: those two parts, no other");

        BusInteriorSpec front = solo, rear = solo;
        front.Section = BusSection.Front;
        rear.Section = BusSection.Rear;
        pose.Lamps = all;
        check(BusInterior.LitLampParts(pose, front) == (Bit(BusInterior.HeadLamps) | Bit(BusInterior.IndicatorL) | Bit(BusInterior.IndicatorR)),
            Prefix + "a front section has no tail, brake or reverse lamps to light");
        check(BusInterior.LitLampParts(pose, rear) == (63 & ~Bit(BusInterior.HeadLamps)), Prefix + "a rear section has no head lamps to light");

        BusInteriorSpec generic = BusInteriorSpec.Default;
        pose.Outside = false;
        check(BusInterior.LitLampParts(pose, generic) == 0, Prefix + "the generic bus in the driver's view draws no exterior part, so no lamp part");
        pose.Outside = true;
        check(BusInterior.LitLampParts(pose, generic) == 63, Prefix + "the generic bus seen from outside lights them");

        // the runtime mesh shows exactly the parts the rule names, for every combination of lamps
        var shown = new RecordingPoser(TestPacks.Profile("SWB6710EV01G"));
        bool same = true;
        for (int lamps = 0; lamps < 64 && same; lamps++)
            foreach (BusInteriorSpec spec in new[] { solo, front, rear })
            {
                pose.Lamps = (BusInterior.ExteriorLamps)lamps;
                shown.Clear();
                BusInterior.Apply(shown, pose, spec);
                int lit = 0;
                for (int part = BusInterior.HeadLamps; part <= BusInterior.ReverseLamps; part++)
                    if (shown.Visible(part)) lit |= Bit(part);
                same &= lit == BusInterior.LitLampParts(pose, spec);
            }
        check(same, Prefix + "the runtime mesh shows lit exactly the parts the rule names (64 lamp sets on a solo, a front and a rear section)");
    }

    // The cab's split on a real pack's runtime mesh: triangles taken from the mesh's own parts must come out as their part's group.
    static void Split(Action<bool, string> check)
    {
        VehicleProfile profile = TestPacks.Profile("SWB6710EV01G");
        VehicleMesh mesh = profile.Mesh;
        var split = new NativeCabOwnership.Split(profile);
        var corners = new List<float3>();

        int lampTriangles = 0, notItsLamp = 0, shared = 0;
        foreach (int part in NativeCabOwnership.LampParts())
        {
            corners.Clear();
            mesh.Corners(part, 0, corners);
            for (int i = 0; i + 2 < corners.Count; i += 3)
            {
                lampTriangles++;
                int lamps = NativeCabOwnership.LampsOf(split.GroupOf(corners[i], corners[i + 1], corners[i + 2]));
                if ((lamps & Bit(part)) == 0) notItsLamp++;
                if ((lamps & ~Bit(part)) != 0) shared++;
            }
        }
        check(lampTriangles > 0 && notItsLamp == 0, Prefix + $"every triangle of a lamp part is a piece of that lamp ({lampTriangles} triangles, {notItsLamp} not)");
        // the SWB6710EV01G's tail and brake lamps light the same lens: its triangles are both lamps' and hide while either is lit
        check(shared > 0, Prefix + $"a face two lamp parts share is a piece of both ({shared} triangle entries of the tail and brake lamps)");
        check(NativeCabOwnership.LampsOf(NativeCabOwnership.LampGroup + 6) == 6 && NativeCabOwnership.LampsOf(NativeMeshCut.Body) == 0 &&
              NativeCabOwnership.LampsOf(NativeMeshCut.Drop) == 0 && !NativeCabOwnership.IsLampGroup(BusInterior.HeadLamps),
            Prefix + "a lamp group carries its lamp parts; the body and a dropped triangle carry none");

        int movingTriangles = 0, movingKept = 0;
        foreach (int part in NativeCabOwnership.MovingParts(mesh))
        {
            corners.Clear();
            mesh.Corners(part, 0, corners);
            for (int i = 0; i + 2 < corners.Count; i += 3)
            {
                movingTriangles++;
                if (split.GroupOf(corners[i], corners[i + 1], corners[i + 2]) != NativeMeshCut.Drop) movingKept++;
            }
        }
        check(movingTriangles > 0 && movingKept == 0, Prefix + $"every triangle of a moving part is still dropped ({movingTriangles} triangles, {movingKept} kept)");

        // the static exterior (the lamp housings are in it): body, unless a triangle's three corners all lie on lamp corners
        corners.Clear();
        mesh.Corners(BusInterior.Exterior, 0, corners);
        int staticTriangles = 0, staticBody = 0, staticDropped = 0;
        for (int i = 0; i + 2 < corners.Count; i += 3)
        {
            staticTriangles++;
            int group = split.GroupOf(corners[i], corners[i + 1], corners[i + 2]);
            if (group == NativeMeshCut.Body) staticBody++;
            else if (group == NativeMeshCut.Drop) staticDropped++;
        }
        check(staticTriangles > 0 && staticDropped == 0 && staticBody >= staticTriangles * 0.99,
            Prefix + $"the static exterior stays body and none of it is dropped ({staticBody} of {staticTriangles} triangles body, {staticDropped} dropped)");

        // a lamp triangle moved 5 mm off its face is no copy of it
        corners.Clear();
        mesh.Corners(BusInterior.HeadLamps, 0, corners);
        float3 off = new float3(0f, 0f, 0.005f);
        check(split.GroupOf(corners[0] + off, corners[1] + off, corners[2] + off) == NativeMeshCut.Body, Prefix + "a face 5 mm off the lamp face is not taken for its copy");
        check(NativeCabOwnership.LampsOf(split.GroupOf(corners[0] + 0.0001f, corners[1] + 0.0001f, corners[2] + 0.0001f)) == Bit(BusInterior.HeadLamps),
            Prefix + "a face 0.1 mm off it is (the copies are within 0.13 mm in the packs measured)");
    }

    // An IKitPoser that only remembers which parts are shown.
    sealed class RecordingPoser : CDLC.BusDriver.Kit.IKitPoser
    {
        readonly Dictionary<int, bool> m_Shown = new Dictionary<int, bool>();
        readonly CDLC.BusDriver.Kit.KitModelData m_Data;
        internal RecordingPoser(VehicleProfile profile)
        {
            m_Data = CDLC.BusDriver.Kit.KitBuild.BuildAll(new BusInterior(profile.Spec.Snapped()), new CDLC.BusDriver.Kit.MeshKit());
        }
        internal void Clear() => m_Shown.Clear();
        internal bool Visible(int part) => m_Shown.TryGetValue(part, out bool shown) && shown;
        public void SetRoot(float3 position, quaternion rotation) { }
        public void Pose(int part, float3 offset, quaternion delta) { }
        public void PoseLocal(int part, float3 localPos, quaternion localRot) { }
        public void Scale(int part, float3 scale) { }
        public void Show(int part, bool visible) => m_Shown[part] = visible;
        public void SetLights(in CDLC.BusDriver.Kit.KitLights lights) { }
        public CDLC.BusDriver.Kit.KitPartDef Def(int part) => m_Data.Parts[part];
        public int PartCount => m_Data.Parts.Length;
        public float4x4 PartToModel(int part) => new float4x4(m_Data.Parts[part].Rot, m_Data.Parts[part].Pivot);
    }
}
