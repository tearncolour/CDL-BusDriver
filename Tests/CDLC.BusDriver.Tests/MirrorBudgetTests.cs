using System;
using CDLC.BusDriver.Interior.Models;

// The real mirrors' budget (MirrorBudget): which mirror renders when at each quality, one a frame at most, the rates in real time
// whatever the frame rate, the share of mirrors out of view, nothing saved up over a pause, the image sizes.
static class MirrorBudgetTests
{
    const string Prefix = "mirror budget: ";
    const double Seconds = 20.0;

    internal static void Run(Action<bool, string> check)
    {
        Rates(check);
        Shares(check);
        OutOfView(check);
        Pause(check);
        Jitter(check);
        Switch(check);
        Sizes(check);
    }

    // Simulates frames (frame(n): the n-th frame's length) from start for the given time, one Take a frame; counts the renders per
    // mirror; checks no frame has more than one render, no one-second window more of one mirror than kMirrorHz (+1 for the window's
    // edges) and, on Medium and Low, no window more of all of them than the shared rate (+1).
    static int[] Simulate(MirrorBudget budget, double start, double seconds, Func<int, double> frame, Func<double, int> inView,
        Action<bool, string> check, string what)
    {
        var counts = new int[budget.Count];
        var all = new System.Collections.Generic.List<double>();
        var each = new System.Collections.Generic.List<double>[budget.Count];
        for (int i = 0; i < each.Length; i++) each[i] = new System.Collections.Generic.List<double>();
        int frames = 0;
        double t = start;
        for (int n = 0; t < start + seconds; n++)
        {
            frames++;
            int pick = budget.Take(t, inView(t));
            if (pick >= 0)
            {
                counts[pick]++;
                all.Add(t);
                each[pick].Add(t);
            }
            t += frame(n);
        }
        check(all.Count <= frames, Prefix + what + ": one render a frame at most (" + all.Count + " in " + frames + " frames)");
        int worstMirror = 0;
        foreach (var times in each) worstMirror = Math.Max(worstMirror, Densest(times));
        check(worstMirror <= MirrorBudget.kMirrorHz + 1, Prefix + what + ": a mirror at most " + MirrorBudget.kMirrorHz + " (+1) in any second (" + worstMirror + ")");
        float shared = MirrorBudget.SharedHz(budget.Quality);
        if (shared > 0f)
        {
            int worst = Densest(all);
            check(worst <= shared + 1, Prefix + what + ": at most " + shared + " (+1) renders in any second (" + worst + ")");
        }
        return counts;
    }

    // The most renders within any one second.
    static int Densest(System.Collections.Generic.List<double> times)
    {
        int worst = 0;
        for (int a = 0, b = 0; b < times.Count; b++)
        {
            while (times[b] - times[a] >= 1.0) a++;
            worst = Math.Max(worst, b - a + 1);
        }
        return worst;
    }

    static int Sum(int[] counts) { int s = 0; foreach (int c in counts) s += c; return s; }

    static bool Near(double value, double expected, double tolerance) => Math.Abs(value - expected) <= tolerance;

    // All the mirrors in view at steady frame rates: one a frame at most, so the rate is the level's or the frame rate, whichever is lower.
    static void Rates(Action<bool, string> check)
    {
        foreach (MirrorQuality quality in new[] { MirrorQuality.High, MirrorQuality.Medium, MirrorQuality.Low })
            foreach (double fps in new[] { 10.0, 24.0, 30.0, 60.0, 75.0, 144.0, 240.0 })
            {
                var budget = new MirrorBudget(3) { Quality = quality };
                double dt = 1.0 / fps;
                int[] counts = Simulate(budget, 100.0, Seconds, _ => dt, _ => 0b111, check, quality + " at " + fps + " fps");
                double cap = quality == MirrorQuality.High ? 3 * MirrorBudget.kMirrorHz : MirrorBudget.SharedHz(quality);
                double expected = Math.Min(cap, fps);
                double rate = Sum(counts) / Seconds;
                check(Near(rate, expected, expected * 0.03 + 0.1), Prefix + quality + " at " + fps + " fps: " + rate.ToString("0.00") + " renders a second, expected " + expected);
            }

        // the rates follow real time, not frames
        foreach (MirrorQuality quality in new[] { MirrorQuality.Medium, MirrorQuality.Low })
        {
            double at60 = Sum(Simulate(new MirrorBudget(3) { Quality = quality }, 0.0, Seconds, _ => 1.0 / 60.0, _ => 0b111, check, quality + " 60")) / Seconds;
            double at144 = Sum(Simulate(new MirrorBudget(3) { Quality = quality }, 0.0, Seconds, _ => 1.0 / 144.0, _ => 0b111, check, quality + " 144")) / Seconds;
            check(Near(at60, at144, 0.02 * at60 + 0.1), Prefix + quality + ": the same rate at 60 and 144 fps (" + at60.ToString("0.00") + ", " + at144.ToString("0.00") + ")");
        }
    }

    // Round robin: the mirrors in view share the budget evenly.
    static void Shares(Action<bool, string> check)
    {
        foreach (MirrorQuality quality in new[] { MirrorQuality.High, MirrorQuality.Medium, MirrorQuality.Low })
        {
            int[] counts = Simulate(new MirrorBudget(3) { Quality = quality }, 0.0, Seconds, _ => 1.0 / 144.0, _ => 0b111, check, quality + " shares");
            int min = Math.Min(counts[0], Math.Min(counts[1], counts[2])), max = Math.Max(counts[0], Math.Max(counts[1], counts[2]));
            check(max - min <= 1, Prefix + quality + ": three mirrors in view get even turns (" + string.Join(", ", counts) + ")");
            double each = quality == MirrorQuality.High ? MirrorBudget.kMirrorHz : MirrorBudget.SharedHz(quality) / 3.0;
            check(Near(counts[0] / Seconds, each, each * 0.03 + 0.1), Prefix + quality + ": each of three at " + each.ToString("0.00") + " a second");
        }

        // two mirrors (a bus without an authored model) at High: each at its own limit, 40 a second together
        int[] two = Simulate(new MirrorBudget(2) { Quality = MirrorQuality.High }, 0.0, Seconds, _ => 1.0 / 144.0, _ => 0b11, check, "two at High");
        check(Near(two[0] / Seconds, MirrorBudget.kMirrorHz, 0.5) && Near(two[1] / Seconds, MirrorBudget.kMirrorHz, 0.5), Prefix + "two mirrors at High: 20 a second each");
    }

    // A mirror out of view renders nothing and leaves its share to the others; none in view: nothing at all.
    static void OutOfView(Action<bool, string> check)
    {
        int[] counts = Simulate(new MirrorBudget(3) { Quality = MirrorQuality.Medium }, 0.0, Seconds, _ => 1.0 / 60.0, _ => 0b011, check, "Medium, mirror 2 out of view");
        check(counts[2] == 0, Prefix + "a mirror out of view is not rendered");
        double half = MirrorBudget.kMediumHz / 2.0;
        check(Near(counts[0] / Seconds, half, half * 0.04 + 0.1) && Near(counts[1] / Seconds, half, half * 0.04 + 0.1),
            Prefix + "Medium: the two in view share the whole budget (" + counts[0] + ", " + counts[1] + ")");

        int[] one = Simulate(new MirrorBudget(3) { Quality = MirrorQuality.Medium }, 0.0, Seconds, _ => 1.0 / 60.0, _ => 0b100, check, "Medium, mirror 2 alone");
        double alone = Math.Min(MirrorBudget.kMediumHz, MirrorBudget.kMirrorHz);
        check(Near(one[2] / Seconds, alone, alone * 0.03 + 0.1) && one[0] == 0 && one[1] == 0,
            Prefix + "Medium: one mirror in view gets the whole budget, under its own limit");

        int[] high = Simulate(new MirrorBudget(3) { Quality = MirrorQuality.High }, 0.0, Seconds, _ => 1.0 / 144.0, _ => 0b001, check, "High, mirror 0 alone");
        check(Near(high[0] / Seconds, MirrorBudget.kMirrorHz, 0.5) && high[1] == 0 && high[2] == 0, Prefix + "High: one mirror in view stays at its own 20 a second");

        foreach (MirrorQuality quality in new[] { MirrorQuality.High, MirrorQuality.Medium, MirrorQuality.Low })
        {
            var budget = new MirrorBudget(3) { Quality = quality };
            int[] none = Simulate(budget, 0.0, Seconds, _ => 1.0 / 60.0, _ => 0, check, quality + ", none in view");
            check(Sum(none) == 0, Prefix + quality + ": no mirror in view, no render");
        }

        check(new MirrorBudget(0).Take(1.0, 0b111) == -1 && !new MirrorBudget(0).AnyDue(1.0), Prefix + "no mirrors: nothing to render");
    }

    // Out of view for a while (the driver looking into the saloon): back in view, no burst of saved-up renders.
    static void Pause(Action<bool, string> check)
    {
        foreach (MirrorQuality quality in new[] { MirrorQuality.High, MirrorQuality.Medium, MirrorQuality.Low })
        {
            var budget = new MirrorBudget(3) { Quality = quality };
            Simulate(budget, 0.0, 5.0, _ => 1.0 / 60.0, _ => 0b111, check, quality + " before the pause");
            Simulate(budget, 5.0, 10.0, _ => 1.0 / 60.0, _ => 0, check, quality + " paused");
            int[] after = Simulate(budget, 15.0, 1.0, _ => 1.0 / 60.0, _ => 0b111, check, quality + " after the pause");
            double cap = quality == MirrorQuality.High ? 3 * MirrorBudget.kMirrorHz : MirrorBudget.SharedHz(quality);
            check(Sum(after) <= cap + 1 && Sum(after) >= cap * 0.9 - 1, Prefix + quality + ": the first second back in view at the usual rate (" + Sum(after) + ")");
        }
    }

    // Uneven frames (5 to 45 ms): the shared rates hold.
    static void Jitter(Action<bool, string> check)
    {
        foreach (MirrorQuality quality in new[] { MirrorQuality.Medium, MirrorQuality.Low })
        {
            var random = new Random(7);
            double[] frames = new double[100000];
            for (int i = 0; i < frames.Length; i++) frames[i] = 0.005 + 0.04 * random.NextDouble();
            int[] counts = Simulate(new MirrorBudget(3) { Quality = quality }, 0.0, Seconds, n => frames[n % frames.Length], _ => 0b111, check, quality + " uneven frames");
            // never over the budget; frames of up to 45 ms (40 a second on average) are close to Medium's 30, so a few turns fall on
            // the same frame and are lost: within 10 %
            double expected = MirrorBudget.SharedHz(quality), rate = Sum(counts) / Seconds;
            check(rate <= expected * 1.01 + 0.1 && rate >= expected * 0.9, Prefix + quality + ": uneven frames still " + rate.ToString("0.00") + " a second, expected " + expected);
        }
    }

    // A change of the level while driving: the new rate from then on.
    static void Switch(Action<bool, string> check)
    {
        var budget = new MirrorBudget(3) { Quality = MirrorQuality.High };
        double t = 0.0;
        double[] rates = new double[3];
        MirrorQuality[] order = { MirrorQuality.Medium, MirrorQuality.Low, MirrorQuality.High };
        for (int step = 0; step < order.Length; step++)
        {
            budget.Quality = order[step];
            Simulate(budget, t, 1.0, _ => 1.0 / 60.0, _ => 0b111, check, "switch settle " + order[step]);
            rates[step] = Sum(Simulate(budget, t + 1.0, 10.0, _ => 1.0 / 60.0, _ => 0b111, check, "switched to " + order[step])) / 10.0;
            t += 11.0;
        }
        check(Near(rates[0], MirrorBudget.kMediumHz, 0.5) && Near(rates[1], MirrorBudget.kLowHz, 0.5) && Near(rates[2], 60.0, 1.0),
            Prefix + "High to Medium to Low to High while running: " + string.Join(", ", rates));
    }

    static void Sizes(Action<bool, string> check)
    {
        check(MirrorBudget.Pixels(192, MirrorQuality.High) == 192 && MirrorBudget.Pixels(256, MirrorQuality.High) == 256 &&
              MirrorBudget.Pixels(192, MirrorQuality.Medium) == 192 && MirrorBudget.Pixels(128, MirrorQuality.Medium) == 128,
            Prefix + "High and Medium keep the full image size");
        check(MirrorBudget.Pixels(192, MirrorQuality.Low) == 96 && MirrorBudget.Pixels(256, MirrorQuality.Low) == 128 && MirrorBudget.Pixels(128, MirrorQuality.Low) == 64,
            Prefix + "Low halves both sides (96 x 128, convex 96 x 64)");
        check(MirrorBudget.Pixels(8, MirrorQuality.Low) == MirrorBudget.kMinPixels, Prefix + "never under " + MirrorBudget.kMinPixels + " pixels");
        check(MirrorBudget.SharedHz(MirrorQuality.High) == 0f && MirrorBudget.SharedHz(MirrorQuality.Medium) == MirrorBudget.kMediumHz &&
              MirrorBudget.SharedHz(MirrorQuality.Low) == MirrorBudget.kLowHz && MirrorBudget.kLowHz < MirrorBudget.kMediumHz &&
              MirrorBudget.kMediumHz < 3 * MirrorBudget.kMirrorHz, Prefix + "Low below Medium below High");
        check(new MirrorBudget(3).Quality == MirrorQuality.High, Prefix + "a new budget is at High (the probe's mirrors)");
    }
}
