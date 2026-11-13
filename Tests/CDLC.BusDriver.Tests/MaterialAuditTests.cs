using System;
using System.Collections.Generic;
using CDLC.BusDriver;

// The pure parts of the material audit (MaterialAuditRules): the decal layers as text, the grouped table of the game's materials, and the
// experiment file that tries a change on Bus Driver's own materials.
static class MaterialAuditTests
{
    public static void Run(Action<bool, string> check)
    {
        Layers(check);
        Stored(check);
        Census(check);
        Experiment(check);
    }

    static void Layers(Action<bool, string> check)
    {
        check(MaterialAuditRules.Layers(0) == "none", "audit: no layer is 'none'");
        check(MaterialAuditRules.Layers(8) == "Vehicles", "audit: 8 is the vehicle layer");
        check(MaterialAuditRules.Layers(7) == "Terrain|Roads|Buildings", "audit: 7, the default of a decal, is the ground, the roads and the buildings");
        check(MaterialAuditRules.Layers(0x3F) == "Terrain|Roads|Buildings|Vehicles|Creatures|Other", "audit: 0x3F is every layer of the game");
        check(MaterialAuditRules.Layers(0x40) == "0x40", "audit: a bit the game does not name is shown as a number");
        check(MaterialAuditRules.Layers(-1) == "Terrain|Roads|Buildings|Vehicles|Creatures|Other|0xffffffc0", "audit: all bits set (-1) names the layers and the rest");
        check(MaterialAuditRules.Layers(0x48) == "Vehicles|0x40", "audit: a named layer and an unnamed one");
    }

    static void Stored(Action<bool, string> check)
    {
        bool same = true;
        foreach (int mask in new[] { 0, 1, 7, 8, 0x3F, 0x40, 0x7FFFFFFF, -1, int.MinValue, 0x7FC00001 })
            same &= MaterialAuditRules.MaskOf(MaterialAuditRules.StoredOf(mask)) == mask;
        check(same, "audit: a mask kept in a float property comes back as the same bits, NaN patterns included");
    }

    static MaterialAuditRules.Row Row(string shader, string name, int queue, int mask, int disable = 1, float support = 0f) =>
        new MaterialAuditRules.Row { Shader = shader, Name = name, Queue = queue, Mask = mask, DisableDecals = disable, SupportDecals = support };

    static void Census(Action<bool, string> check)
    {
        var rows = new List<MaterialAuditRules.Row>
        {
            Row("BH/SG_DefaultShader", "bus A", 2000, 8),
            Row("BH/SG_DefaultShader", "bus B", 2000, 8),
            Row("BH/SG_DefaultShader", "bus A", 2000, 8),
            Row("BH/SG_DefaultShader", "bus C", 2000, 8),
            Row("BH/SG_DefaultShader", "bus D", 2000, 8),
            Row("BH/Decal", "paving", 2050, 7, -1, float.NaN),
            Row("BH/Decal", "cracks", 2050, 0x3F, -1, float.NaN),
            Row("HDRP/Lit", "CDL cab Seat", 2225, 8, 1, 0f),
        };
        List<string> lines = MaterialAuditRules.Census(rows, 40);
        check(lines.Count == 4, "audit: four groups of materials (" + lines.Count + ")");
        check(lines[0].StartsWith("5 x [BH/SG_DefaultShader] queue 2000, layer mask Vehicles (VEHICLES)"), "audit: the biggest group first, with its layer named: " + lines[0]);
        check(lines[0].Contains("e.g. bus A, bus B, bus C"), "audit: three different names as examples (a repeated name once): " + lines[0]);
        check(lines[0].Contains("_DISABLE_DECALS on, _SupportDecals 0"), "audit: the keyword and the float of the group: " + lines[0]);
        bool paving = false, cracks = false;
        foreach (string line in lines)
        {
            paving |= line.Contains("[BH/Decal] queue 2050, layer mask Terrain|Roads|Buildings,") && !line.Contains("VEHICLES") && line.Contains("_DISABLE_DECALS n/a, _SupportDecals n/a");
            cracks |= line.Contains("Terrain|Roads|Buildings|Vehicles|Creatures|Other (VEHICLES)") && line.Contains("e.g. cracks");
        }

        check(paving, "audit: a decal of the ground, the roads and the buildings is not flagged as taking vehicles");
        check(cracks, "audit: a decal whose mask includes the vehicle layer is flagged");

        var shuffled = new List<MaterialAuditRules.Row>(rows);
        shuffled.Reverse();
        check(string.Join("\n", MaterialAuditRules.Census(shuffled, 40)) == string.Join("\n", lines), "audit: the same materials in another order give the same lines");

        List<string> capped = MaterialAuditRules.Census(rows, 2);
        check(capped.Count == 3 && capped[2] == "... and 2 more groups", "audit: a cap on the lines says how many groups it left out: " + string.Join(" | ", capped));
        check(MaterialAuditRules.Census(new List<MaterialAuditRules.Row>(), 40).Count == 0, "audit: no materials, no lines");
    }

    static void Experiment(Action<bool, string> check)
    {
        var problems = new List<string>();
        MaterialAuditRules.Experiment all = MaterialAuditRules.ParseExperiment(
            "# a first try\r\nqueue = 2000\r\ndecalMask = Vehicles|Other   # the vehicle layer and the other one\r\nsupportDecals = 0\r\ndisableDecals = ON\r\nrenderingLayer = 0x8\r\n", problems);
        check(problems.Count == 0, "audit: a good file has no problem: " + string.Join("; ", problems));
        check(all.Queue == 2000 && all.DecalMask == 0x28 && all.SupportDecals == 0f && all.DisableDecals == true && all.RenderingLayer == 8u, "audit: every setting is read");
        check(all.Any && all.Describe() == "queue 2000, layer mask Vehicles|Other, _SupportDecals 0, _DISABLE_DECALS on, rendering layer mask 0x8", "audit: it describes itself: " + all.Describe());

        problems.Clear();
        MaterialAuditRules.Experiment none = MaterialAuditRules.ParseExperiment("queue = keep\nrenderingLayer =\n# nothing else\n\n", problems);
        check(!none.Any && problems.Count == 0 && none.Describe() == "nothing", "audit: 'keep' and an empty value leave a setting alone");
        check(!MaterialAuditRules.ParseExperiment(null, null).Any && !MaterialAuditRules.ParseExperiment("", null).Any, "audit: no text asks for nothing");

        problems.Clear();
        MaterialAuditRules.Experiment bad = MaterialAuditRules.ParseExperiment("queue = 99999\nqueue = soon\ndecalMask = Vehicle\nsupportDecals = 2\ndisableDecals = maybe\nrenderingLayer = -1\nglow = 3\n", problems);
        check(!bad.Any, "audit: values that are not valid set nothing");
        check(problems.Count == 7, "audit: every bad line is reported (" + problems.Count + "): " + string.Join("; ", problems));
        check(problems[6] == "line 7: unknown setting 'glow'", "audit: an unknown setting is named with its line: " + problems[6]);

        problems.Clear();
        MaterialAuditRules.Experiment layers = MaterialAuditRules.ParseExperiment("DecalMask = none\nrenderingLayer = 4294967295\nQUEUE=2225\n", problems);
        check(problems.Count == 0 && layers.DecalMask == 0 && layers.RenderingLayer == uint.MaxValue && layers.Queue == 2225, "audit: names are not case sensitive; 'none' is mask 0; all bits are a valid rendering layer mask");
        check(MaterialAuditRules.ParseExperiment("decalMask = 8", null).DecalMask == 8 && MaterialAuditRules.ParseExperiment("decalMask = 0x3f", null).DecalMask == 0x3F, "audit: a layer mask may be a decimal or a hex number");
        check(MaterialAuditRules.ParseExperiment("decalMask = 4294967295", null).DecalMask == -1, "audit: a mask of 32 bits keeps its bits");
        check(MaterialAuditRules.ParseExperiment("queue = 2000 = 3", null).Queue == null, "audit: two equals signs are not a number");

        // any float property or local keyword by name (the detail normal of the cab, the smoothness, a screen-space keyword)
        problems.Clear();
        MaterialAuditRules.Experiment named = MaterialAuditRules.ParseExperiment(
            "float _DetailNormalScale = 0   # the baked relief off\r\nFLOAT _Smoothness = 0.25\r\nkeyword _DETAIL_MAP = off\r\nKeyword _DISABLE_SSR = ON\r\nfloat _Smoothness = 0.5\r\n", problems);
        check(problems.Count == 0, "audit: float and keyword lines of a good file have no problem: " + string.Join("; ", problems));
        check(named.Any && named.Floats.Count == 2 && named.Floats["_DetailNormalScale"] == 0f && named.Floats["_Smoothness"] == 0.5f, "audit: a float property is read as written and the later line wins");
        check(named.Keywords.Count == 2 && named.Keywords["_DETAIL_MAP"] == false && named.Keywords["_DISABLE_SSR"] == true, "audit: a keyword is on or off");
        check(named.Describe() == "_DetailNormalScale 0, _Smoothness 0.5, _DETAIL_MAP off, _DISABLE_SSR on", "audit: they are named in the description: " + named.Describe());
        check(new MaterialAuditRules.Experiment().Any == false && MaterialAuditRules.ParseExperiment("float _X = 1", null).Any, "audit: a float line alone is an experiment");

        problems.Clear();
        MaterialAuditRules.Experiment wrong = MaterialAuditRules.ParseExperiment(
            "float _X = soon\nfloat _Y = NaN\nfloat 3d = 1\nfloat _Bad-Name = 1\nkeyword _lower = on\nkeyword _OK = maybe\nkeyword = on\nfloat _Z = 1 = 2\nfloat = 1\n", problems);
        check(!wrong.Any && problems.Count == 9, "audit: bad property or keyword lines set nothing and are all reported (" + problems.Count + "): " + string.Join("; ", problems));
        check(problems[0] == "line 1: 'soon' is not a value for _X" && problems[2] == "line 3: '3d' is not a property name" && problems[4] == "line 5: '_lower' is not a keyword name" &&
            problems[5] == "line 6: 'maybe' is not on or off for _OK", "audit: they are told with their line: " + string.Join(" | ", problems));
        check(MaterialAuditRules.ParseExperiment("float _Scale = 1e-3", null).Floats["_Scale"] == 0.001f && MaterialAuditRules.ParseExperiment("float _S = -0.5", null).Floats["_S"] == -0.5f,
            "audit: a float may be negative or in exponent form");
        check(MaterialAuditRules.ParseExperiment("float _S = keep", null).Floats.Count == 0, "audit: 'keep' leaves a named float alone too");

        // the main camera's decal pass
        problems.Clear();
        MaterialAuditRules.Experiment camera = MaterialAuditRules.ParseExperiment("cameraDecals = OFF", problems);
        check(camera.Any && camera.CameraDecals == false && camera.Describe() == "camera decals off", "audit: cameraDecals off is an experiment: " + camera.Describe());
        check(MaterialAuditRules.ParseExperiment("CameraDecals = on", null).CameraDecals == true, "audit: cameraDecals on");
        problems.Clear();
        check(!MaterialAuditRules.ParseExperiment("cameraDecals = maybe", problems).Any && problems.Count == 1 && problems[0] == "line 1: 'maybe' is not a value for cameradecals",
            "audit: cameraDecals takes on or off only: " + string.Join("; ", problems));

        // the game's shader for the bus's own materials
        problems.Clear();
        MaterialAuditRules.Experiment shader = MaterialAuditRules.ParseExperiment("gameShader = ON", problems);
        check(shader.Any && shader.GameShader == true && shader.Describe() == "game shader on" && problems.Count == 0, "audit: gameShader on is an experiment: " + shader.Describe());
        check(MaterialAuditRules.ParseExperiment("gameShader = off", null).GameShader == false, "audit: gameShader off");
        check(!MaterialAuditRules.ParseExperiment("gameShader = plain", problems).Any && problems.Count == 1, "audit: gameShader takes on or off only");
        check(MaterialAuditRules.GameShaderMovable("Bus Driver cab Satin", "HDRP/Lit", 0f) && MaterialAuditRules.GameShaderMovable("Bus Driver livery c59a00", "HDRP/Lit", 0f) &&
              MaterialAuditRules.GameShaderMovable("Bus Driver cab Satin baked relief", "HDRP/Lit", 0f), "audit: the cab's and the livery's HDRP/Lit materials move, the baked relief ones too");
        check(!MaterialAuditRules.GameShaderMovable("Bus Driver dimmed lamp 1ad9ff0130000c", "HDRP/Lit", 3.5f) && !MaterialAuditRules.GameShaderMovable("Bus Driver cab Glow", "HDRP/Lit", 0.2f), "audit: what glows stays on HDRP/Lit");
        check(!MaterialAuditRules.GameShaderMovable("Bus Driver mirror 0", "HDRP/Unlit", 0f) && !MaterialAuditRules.GameShaderMovable("CDLCBus12_Gls_x (CDL glass)", "BH/GlsShader", 0f) &&
              !MaterialAuditRules.GameShaderMovable("Male_Body#0_x", "BH/Characters/SG_CharacterSkin", 0f) && !MaterialAuditRules.GameShaderMovable("Default", "HDRP/Lit", 0f) && !MaterialAuditRules.GameShaderMovable(null, "HDRP/Lit", 0f),
            "audit: mirrors, glass, people and the game's own materials never move");

        // the queue the road's decals are cut against
        check(!MaterialAuditRules.InDecalQueue(2000) && !MaterialAuditRules.InDecalQueue(2224) && MaterialAuditRules.InDecalQueue(2225) && MaterialAuditRules.InDecalQueue(2226) &&
              MaterialAuditRules.InDecalQueue(2475) && MaterialAuditRules.InDecalQueue(2500) && !MaterialAuditRules.InDecalQueue(2501) && !MaterialAuditRules.InDecalQueue(3000), "audit: the depth prepass takes the queues 2225 to 2500");
        check(MaterialAuditRules.WantsDecalQueue("Bus Driver cab Satin baked relief", "HDRP/Lit") && MaterialAuditRules.WantsDecalQueue("Bus Driver livery c59a00", "BH/SG_DefaultShader") &&
              !MaterialAuditRules.WantsDecalQueue("Bus Driver mirror 0", "HDRP/Unlit") && !MaterialAuditRules.WantsDecalQueue("CDLCBus12_Gls_x (CDL glass)", "BH/GlsShader") &&
              !MaterialAuditRules.WantsDecalQueue("Male_Body#0_x", "BH/Characters/SG_CharacterSkin") && !MaterialAuditRules.WantsDecalQueue(null, "HDRP/Lit"), "audit: which materials are asked for the decal queue");

        // a texture read back from the graphics card against the bytes it was made from
        byte[] made = new byte[4 * 4 * 2];
        for (int i = 0; i < made.Length; i += 4) { made[i] = (byte)i; made[i + 1] = 20; made[i + 2] = 30; made[i + 3] = 255; }
        byte[] same = (byte[])made.Clone();
        check(MaterialAuditRules.CompareTexels(same, made, 4) == "identical to the bytes it was made from", "audit: a texture read back unchanged is identical");
        byte[] changed = (byte[])made.Clone();
        changed[5 * 4] = 200; changed[5 * 4 + 1] = 100; changed[5 * 4 + 2] = 50; changed[7 * 4 + 3] = 0;
        string diff = MaterialAuditRules.CompareTexels(changed, made, 4);
        check(diff.StartsWith("2 of 8 texels differ; the first at (1,1) is 200,100,50,255 against 20,20,30,255; mean colour "), "audit: different texels are counted and the first is placed and shown: " + diff);
        check(MaterialAuditRules.CompareTexels(new byte[8], made, 4).Contains("has 8 bytes, the texture was made from 32"), "audit: a read-back of another size says so");
        check(MaterialAuditRules.CompareTexels(null, made, 4) == "no data to compare" && MaterialAuditRules.CompareTexels(made, null, 4) == "no data to compare", "audit: no data is not a comparison");
    }
}
