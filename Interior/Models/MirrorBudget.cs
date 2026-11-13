using System;

// The real mirrors' cost, as pure logic (CabMirrors places the cameras and draws what it says): how often which mirror is redrawn at each
// quality, and the size of its image.
namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>What the real mirrors may cost (option "Mirror quality"). Low and Medium also leave passes out (CabMirrors).</summary>
    public enum MirrorQuality { Low = 0, Medium = 1, High = 2 }

    /// <summary>
    /// Which mirror camera renders this frame. One a frame at most; any one mirror at most kMirrorHz times a second (High: that alone,
    /// each mirror on its own); on Medium and Low all the mirrors together at most SharedHz times a second, handed round in turn to those
    /// in the driver's view (one out of view leaves its share to the others). Times are unscaled real seconds, so the rates do not follow
    /// the frame rate (below them the frame rate is the limit: one a frame). Nothing is saved up while no mirror is in view or the frames
    /// are slow: the cadence starts again from now.
    /// </summary>
    public sealed class MirrorBudget
    {
        /// <summary>Any one mirror at most, on every level (High: each mirror at this on its own; three mirrors: 60 a second).</summary>
        public const float kMirrorHz = 20f;
        /// <summary>All the mirrors together on Medium (three in view: 10 a second each; half of High's renders at 60 fps).</summary>
        public const float kMediumHz = 30f;
        /// <summary>All the mirrors together on Low (three in view: 5 a second each; a quarter of High's at 60 fps).</summary>
        public const float kLowHz = 15f;
        /// <summary>The mirror images on Low, of their size on High and Medium (both sides: a quarter of the pixels).</summary>
        public const float kLowScale = 0.5f;
        /// <summary>The smallest side of a mirror image, in pixels.</summary>
        public const int kMinPixels = 16;

        private readonly double[] m_Next;      // per mirror: unscaled time it may render again
        private double m_NextShared;           // Medium and Low: the time the shared budget has its next render
        private int m_Turn;                    // the mirror rendered last (the next one in turn goes first)

        public MirrorBudget(int count)
        {
            m_Next = new double[Math.Max(0, Math.Min(count, 31))];
            m_Turn = m_Next.Length - 1;
        }

        public MirrorQuality Quality { get; set; } = MirrorQuality.High;

        public int Count => m_Next.Length;

        /// <summary>All the mirrors' renders a second at a quality; 0: no shared limit (High: kMirrorHz each).</summary>
        public static float SharedHz(MirrorQuality quality) =>
            quality == MirrorQuality.Low ? kLowHz : quality == MirrorQuality.Medium ? kMediumHz : 0f;

        /// <summary>The share of the full image size a mirror image has at a quality.</summary>
        public static float TextureScale(MirrorQuality quality) => quality == MirrorQuality.Low ? kLowScale : 1f;

        /// <summary>A side of a mirror image at a quality, from its full size in pixels.</summary>
        public static int Pixels(int full, MirrorQuality quality) => Math.Max(kMinPixels, (int)Math.Round(full * TextureScale(quality)));

        /// <summary>True when a mirror may render now (the shared budget has one, and a mirror is due): only then the caller looks which are in view.</summary>
        public bool AnyDue(double now)
        {
            if (SharedHz(Quality) > 0f && now < m_NextShared)
                return false;
            for (int i = 0; i < m_Next.Length; i++)
                if (now >= m_Next[i])
                    return true;
            return false;
        }

        /// <summary>
        /// The mirror to render now among those in view (bit i of inView: mirror i in the driver's view), its render counted; -1: none
        /// (nothing counted). Call it once a frame: one mirror a frame at most.
        /// </summary>
        public int Take(double now, int inView)
        {
            if (inView == 0 || !AnyDue(now))
                return -1;
            int count = m_Next.Length;
            for (int k = 1; k <= count; k++)
            {
                int i = (m_Turn + k) % count;
                if ((inView & (1 << i)) == 0 || now < m_Next[i])
                    continue;
                m_Next[i] = Advance(m_Next[i], now, 1.0 / kMirrorHz);
                float shared = SharedHz(Quality);
                if (shared > 0f)
                    m_NextShared = Advance(m_NextShared, now, 1.0 / shared);
                m_Turn = i;
                return i;
            }
            return -1;
        }

        // A steady cadence while it keeps up; behind by a whole step or more (out of view, slow frames, a level just changed): from now.
        private static double Advance(double next, double now, double step) => now - next < step ? next + step : now + step;
    }
}
