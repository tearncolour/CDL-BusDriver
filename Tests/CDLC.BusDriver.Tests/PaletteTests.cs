using System;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

static class PaletteTests
{
    internal static void Run(Action<bool, string> check)
    {
        check((int)Sw.ShanghaiAqua == 83 && (int)Sw.FloorDarkBlue == 84 && (int)Sw.SeatMouldedYellow == 85,
            "palette: new colours preserve existing resource indices");
        check(math.distance(KitPalette.ToLinear(KitPalette.Srgb(Sw.FloorDarkBlue)), new float3(.040f, .072f, .118f)) < 1e-6f &&
              math.distance(KitPalette.ToLinear(KitPalette.Srgb(Sw.SeatMouldedYellow)), new float3(.86f, .58f, .020f)) < 1e-6f,
            "palette: explicit blue floor / yellow seat retain authored linear colours");
        check(KitPalette.FinOf(Sw.FloorDarkBlue) == Fin.Matte && KitPalette.FinOf(Sw.SeatMouldedYellow) == Fin.MouldedPlastic &&
              KitPalette.Metallic(KitPalette.FinOf(Sw.SeatMouldedYellow)) == 0f,
            "palette: floor remains matte and moulded seat remains dielectric");
        check((int)Sw.RedGloss == 86 && (int)Sw.YellowGloss == 87 && (int)Sw.BlueGloss == 88 && (int)Sw.WhiteGloss == 89 && (int)Sw.Count == 90,
            "palette: the glossy paints are appended after every existing row");
        // The finish mask of a baked submesh layout is 16 bits wide (CabMaterials.Set, KitLights.CodeFor): a 17th finish would overflow it.
        check((int)Fin.Count <= 16, "palette: every finish has a bit in the 16-bit finish mask");
        // The three demonstration paints are their plain row with less colour (85 % chroma, so the spread between the channels is 50-100 % of the plain row's); WhiteGloss stays the
        // plain white. All four are the glossy dielectric finish.
        foreach ((Sw gloss, Sw plain) in new[] { (Sw.RedGloss, Sw.Red), (Sw.YellowGloss, Sw.Yellow), (Sw.BlueGloss, Sw.Blue), (Sw.WhiteGloss, Sw.White) })
        {
            Fin finish = KitPalette.FinOf(gloss);
            float3 g = KitPalette.Srgb(gloss), p = KitPalette.Srgb(plain);
            float spreadG = math.cmax(g) - math.cmin(g), spreadP = math.cmax(p) - math.cmin(p);
            bool colour = gloss == Sw.WhiteGloss ? math.all(g == p) : spreadG < spreadP && spreadG >= .5f * spreadP;
            check(colour && KitPalette.FinOf(plain) == Fin.Satin &&
                  finish == Fin.MouldedPlastic && KitPalette.Metallic(finish) == 0f && KitPalette.Smoothness(finish) > KitPalette.Smoothness(Fin.Satin),
                "palette: " + gloss + " is " + plain + " in the glossy dielectric finish");
        }
        foreach (string model in new[] { "SWB6710EV01G", "SWB6109EV12G" })
            check(!TestPacks.Profile(model).Mesh.LiveryGloss, "palette: " + model + " keeps its plain livery material");
        // the clear coat of the paint rows on the game's shader
        check(KitPalette.ClearCoat(Sw.Yellow) && KitPalette.ClearCoat(Sw.White) && KitPalette.ClearCoat(Sw.Black) && KitPalette.ClearCoat(Sw.RedGloss) && KitPalette.ClearCoat(Sw.WhiteGloss) &&
              !KitPalette.ClearCoat(Sw.DarkGrey) && !KitPalette.ClearCoat(Sw.Rubber) && !KitPalette.ClearCoat(Sw.Glass) && !KitPalette.ClearCoat(Sw.BrushedStainless) && !KitPalette.ClearCoat(Sw.Panel) &&
              !KitPalette.ClearCoat(Sw.DashBlack) && !KitPalette.ClearCoat(Sw.SeatMouldedYellow) && !KitPalette.ClearCoat(Sw.SafetyWhite), "palette: the paints and the glossy paints take a clear coat, nothing else does");
        bool coatedFinishes = true;
        for (int i = 0; i < (int)Sw.Count; i++)
            if (KitPalette.ClearCoat((Sw)i) && !KitPalette.HasClearCoat(KitPalette.FinOf((Sw)i))) coatedFinishes = false;
        check(coatedFinishes && !KitPalette.HasClearCoat(Fin.Matte) && !KitPalette.HasClearCoat(Fin.Metal) && !KitPalette.HasClearCoat(Fin.Glass) && !KitPalette.HasClearCoat(Fin.BrushedMetal), "palette: every coated row is in a finish that has a coated mask");
        foreach (Fin fin in new[] { Fin.Satin, Fin.MouldedPlastic })
        {
            byte[] mask = KitPalette.MaskBytes(fin);
            bool layout = mask.Length == KitPalette.Width * KitPalette.Height * 4, coat = true, numbers = true;
            byte metal = (byte)math.round(KitPalette.Metallic(fin) * 255f), smooth = (byte)math.round(KitPalette.Smoothness(fin) * 255f);
            for (int r = 0; layout && r < KitPalette.Rows; r++)
                for (int y = 0; y < KitPalette.RowHeight; y++)
                    for (int x = 0; x < KitPalette.Width; x++)
                    {
                        int i = ((r * KitPalette.RowHeight + y) * KitPalette.Width + x) * 4;
                        coat &= mask[i + 1] == (KitPalette.ClearCoat((Sw)r) ? 255 : 0);
                        numbers &= mask[i] == metal && mask[i + 2] == 0 && mask[i + 3] == smooth;
                    }

            check(layout && coat && numbers, "palette: the " + fin + " mask map is laid out as its palette: the paint rows coated, the finish's own metallic and smoothness everywhere");
        }

        for (int i = 0; i < (int)Sw.Count; i++)
        {
            Sw sw = (Sw)i;
            check((int)math.floor(KitPalette.RowV(sw) * KitPalette.Rows) == i &&
                  math.all(math.isfinite(KitPalette.Srgb(sw))) && KitPalette.FinOf(sw) < Fin.Count,
                "palette: valid row centre, colour and finish " + sw);
        }
    }
}
