using System;
using System.Text.Json;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// The driver view offset (Systems/CabViewRules.cs): the step is in the right direction for a bus driven from either side, held to its range, added to the camera eye and
// to nothing else; the options have texts in both languages.
static class CabViewTests
{
    const string Prefix = "cab view offset: ";

    internal static void Run(Action<bool, string> check)
    {
        Steps(check);
        Eyes(check);
        Texts(check);
        Clearances(check);
    }

    // The nearest solid triangle (any part, at rest, LOD0) to a point in model space: how far, and which part.
    static float Nearest(KitModelData data, float3 point, out string part)
    {
        float best = float.MaxValue;
        part = "";
        foreach (KitPartDef p in data.Parts)
        {
            KitMeshData d = p.Data[0] ?? (p.MeshOf >= 0 ? data.Parts[p.MeshOf].Data[0] : null);
            if (d == null) continue;
            float4x4 toModel = math.inverse(p.ModelToPart);
            for (int t = 0; t + 2 < d.Idx.Length; t += 3)
            {
                float3 a = math.transform(toModel, d.P[d.Idx[t]]), b = math.transform(toModel, d.P[d.Idx[t + 1]]), c = math.transform(toModel, d.P[d.Idx[t + 2]]);
                float distance = math.distance(point, ClosestOnTriangle(point, a, b, c));
                if (distance < best) { best = distance; part = p.Name; }
            }
        }

        return best;
    }

    // Ericson, Real-Time Collision Detection, 5.1.5
    static float3 ClosestOnTriangle(float3 p, float3 a, float3 b, float3 c)
    {
        float3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = math.dot(ab, ap), d2 = math.dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;
        float3 bp = p - b;
        float d3 = math.dot(ab, bp), d4 = math.dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
        float3 cp = p - c;
        float d5 = math.dot(ab, cp), d6 = math.dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    static void Clearances(Action<bool, string> check)
    {
        var subjects = new System.Collections.Generic.List<(string name, BusInteriorSpec spec)>();
        var generic = BusInteriorSpec.Default;
        subjects.Add(("generic bus (driver on the left)", generic));
        generic.DriverSide = 1f;
        subjects.Add(("generic bus (driver on the right)", generic));
        foreach (string model in new[] { "SWB6710EV01G", "SWB6109EV12G" })
            subjects.Add((model, TestPacks.Spec(model)));
        foreach (var (name, spec) in subjects)
        {
            KitModelData data = KitBuild.BuildAll(new BusInterior(spec));
            if (data.Error != null) { check(false, Prefix + name + " builds: " + data.Error); continue; }
            float3 camera = BusInterior.CameraEye(spec);
            float worst = float.MaxValue, atRest = Nearest(data, camera, out _);
            string worstPart = "", worstAt = "";
            foreach (int h in new[] { -25, 0, 25 })
                foreach (int s in new[] { -25, 0, 25 })
                {
                    float d = Nearest(data, camera + CabViewRules.Offset(h, s), out string part);
                    if (d < worst) { worst = d; worstPart = part; worstAt = "height " + h + ", side " + s; }
                }

            check(worst >= kClearance, Prefix + name + ": the camera at any corner of the range stays " + (kClearance * 100f) + " cm from every solid surface (nearest " +
                  (worst * 100f).ToString("0.0") + " cm, " + worstPart + ", at " + worstAt + "; the seat itself " + (atRest * 100f).ToString("0.0") + " cm)");
        }
    }

    // A guard on the range, not a proof that the view is good: the model geometry changes, and a range that put the camera into a roof, a window frame or a seat would show
    // here. The smallest distance measured when the range was chosen was 10.4 cm (the generic bus, 25 cm down).
    const float kClearance = .08f;

    static bool Near(float3 a, float3 b) => math.all(math.abs(a - b) < 1e-6f);

    static void Steps(Action<bool, string> check)
    {
        check(Near(CabViewRules.Offset(0, 0), float3.zero), Prefix + "no number is no step");
        check(Near(CabViewRules.Offset(10, 0), new float3(0f, .10f, 0f)) && Near(CabViewRules.Offset(-10, 0), new float3(0f, -.10f, 0f)), Prefix + "height: up is +Y, 1 cm a step");
        check(Near(CabViewRules.Offset(0, 7), new float3(.07f, 0f, 0f)) && Near(CabViewRules.Offset(0, -7), new float3(-.07f, 0f, 0f)), Prefix + "side: right is +X, 1 cm a step");
        check(Near(CabViewRules.Offset(5, -3), new float3(-.03f, .05f, 0f)), Prefix + "both at once; never a step forward or back");
        check(Near(CabViewRules.Offset(25, 25), new float3(.25f, .25f, 0f)) && Near(CabViewRules.Offset(-25, -25), new float3(-.25f, -.25f, 0f)), Prefix + "the range is 25 cm either way");
        check(Near(CabViewRules.Offset(26, -26), new float3(-.25f, .25f, 0f)) && Near(CabViewRules.Offset(int.MaxValue, int.MinValue), new float3(-.25f, .25f, 0f)) &&
              Near(CabViewRules.Offset(1000, 1000), new float3(.25f, .25f, 0f)), Prefix + "a number outside the range (a file edited by hand) is held to it");
        bool finite = true;
        foreach (int n in new[] { int.MinValue, -1000, -26, 0, 26, 1000, int.MaxValue })
            finite &= math.all(math.isfinite(CabViewRules.Offset(n, n)));
        check(finite, Prefix + "every number gives a finite step");
    }

    // Where the offset goes: the camera eye of a bus, not its formal eye; "right" is the same direction on a bus driven from the left and on one driven from the right.
    static void Eyes(Action<bool, string> check)
    {
        foreach (float side in new[] { -1f, 1f })
        {
            var spec = BusInteriorSpec.Default;
            spec.DriverSide = side;
            float3 formal = BusInterior.Eye(spec), camera = BusInterior.CameraEye(spec), step = CabViewRules.Offset(10, 5);
            check(Near(camera, formal + new float3(0f, -.06f, 0f)), Prefix + "side " + side + ": the camera eye is the formal one 6 cm lower, as before");
            float3 moved = camera + step;
            check(Near(moved - camera, new float3(.05f, .10f, 0f)) && Near(BusInterior.Eye(spec), formal), Prefix + "side " + side + ": the step is added to the camera eye and the formal eye stays");
            // toward the window or toward the aisle is a different way on the two kinds of bus; right is not. The eye sits on the driver side of the middle: a step to the
            // right goes away from the window of a bus driven from the left and towards the window of one driven from the right.
            bool rightIsTowardsWindow = math.sign(formal.x) == math.sign(CabViewRules.Offset(0, 10).x);
            check(math.sign(formal.x) == side && rightIsTowardsWindow == (side > 0f), Prefix + "side " + side + ": the eye is on the driver side, and +10 cm to the right is " + (side > 0f ? "towards" : "away from") + " the window");
        }
    }

    static void Texts(Action<bool, string> check)
    {
        const string Key = "Options.OPTION[CDLC.BusDriver.CDLC.BusDriver.Mod.BusSettings.";
        string dir = LangPack.Dir();
        foreach (string locale in new[] { "en-US", "zh-HANS" })
        {
            string text = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, locale + ".json")).TrimStart('\uFEFF');
            JsonElement root = JsonDocument.Parse(text).RootElement;
            foreach (string option in new[] { "CabEyeHeight", "CabEyeSide" })
            {
                bool name = root.TryGetProperty(Key + option + "]", out JsonElement n) && !string.IsNullOrWhiteSpace(n.GetString());
                bool description = root.TryGetProperty(Key.Replace("OPTION[", "OPTION_DESCRIPTION[") + option + "]", out JsonElement d) && !string.IsNullOrWhiteSpace(d.GetString());
                check(name && description, Prefix + locale + ": " + option + " has a name and a description");
                if (description)
                    check(d.GetString().Contains("25"), Prefix + locale + ": " + option + " says the range (25 cm)");
            }
        }
    }
}
