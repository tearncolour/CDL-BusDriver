using System.Collections.Generic;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The card readers' lights (BusInterior.CardReaders): the colour screen and the QR code scanner's window always lit (dimmed
    /// at night like the other LEDs), the green OK lamp of the fare box's reader flashing with each passenger's beep.
    /// </summary>
    public sealed partial class BusCabSystem
    {
        private static readonly float3 s_ReaderScreen = new float3(0.3f, 0.55f, 1f), s_ReaderScan = new float3(0.6f, 0.95f, 1f);
        private const float kReaderScreenNits = 90f, kReaderScreenNight = 14f, kReaderScanNits = 140f, kReaderScanNight = 20f;
        private const float kReaderOkNits = 400f, kReaderOkNight = 60f, kReaderFlashSeconds = 0.35f;

        private sealed class ReaderLights
        {
            internal CabTelltales Lamps;
            internal int Ok = -1;
        }

        private readonly Dictionary<CabInstance, ReaderLights> m_Readers = new Dictionary<CabInstance, ReaderLights>();

        /// <summary>Every frame for a posed section: makes its readers' lights once, flashes the OK lamp with the beep.</summary>
        private void UpdateReaders(Section section, bool front)
        {
            CabInstance instance = section.Instance;
            if (instance == null || !instance.Alive || instance.Root == null)
                return;

            if (!m_Readers.TryGetValue(instance, out ReaderLights lights) || lights.Lamps == null || !lights.Lamps.Alive)
            {
                lights?.Lamps?.Destroy();
                lights = new ReaderLights { Lamps = new CabTelltales() };
                m_Readers[instance] = lights;
                Material screen = CabMaterials.DimmedLamp(s_ReaderScreen, kReaderScreenNits, kReaderScreenNight);
                Material scan = CabMaterials.DimmedLamp(s_ReaderScan, kReaderScanNits, kReaderScanNight);
                Material ok = CabMaterials.DimmedLamp(s_Green, kReaderOkNits, kReaderOkNight);
                bool first = true;
                foreach ((float3 origin, quaternion frame, float xSign) in BusInterior.CardReaders(section.Spec))
                {
                    // the quads face -Z of their frame: turned to face out of the reader's face
                    quaternion facing = math.mul(frame, quaternion.RotateY(math.PI));
                    float3 At(float x, float y, float z) => origin + math.mul(frame, new float3(xSign * x, y, z));
                    lights.Lamps.Set(lights.Lamps.Add(instance.Root, At(0f, 0.075f, 0.0479f), facing, new float2(0.118f, 0.078f), screen), true);
                    lights.Lamps.Set(lights.Lamps.Add(instance.Root, At(0f, -0.09f, 0.0478f), facing, new float2(0.088f, 0.033f), scan), true);
                    if (first && front)
                        lights.Ok = lights.Lamps.Add(instance.Root, At(0.058f, -0.09f, 0.0482f), facing, new float2(0.011f, 0.011f), ok);
                    first = false;
                }
            }

            if (lights.Ok >= 0)
                lights.Lamps.Set(lights.Ok, UnityEngine.Time.unscaledTime - m_CabSounds.LastBeep < kReaderFlashSeconds);
        }

        private readonly CabMirrors m_Mirrors = new CabMirrors();
        private CabInstance m_MirrorInstance;
        private Game.Rendering.CameraUpdateSystem m_Cameras;      // looked up until found, then kept (the world's own system)

        /// <summary>The real mirrors in the driver's view (option "Real mirrors"): made on the front section, rendered while shown.</summary>
        private void UpdateMirrors(bool show)
        {
            Section front = m_Sections.Count > 0 ? m_Sections[0] : null;
            bool want = show && Mod.Settings != null && Mod.Settings.RealMirrors && front != null && front.Spec.Section != BusSection.Rear &&
                front.Instance != null && front.Instance.Alive && front.Instance.Root != null;
            if (!want)
            {
                m_Mirrors.Update(null, false);
                return;
            }

            if (!m_Mirrors.Alive || m_MirrorInstance != front.Instance)
            {
                m_MirrorInstance = front.Instance;
                if (!m_Mirrors.Create(front.Instance.Root, front.Spec))
                    return;
            }

            if (m_Cameras == null)
                m_Cameras = World.GetExistingSystemManaged<Game.Rendering.CameraUpdateSystem>();
            Game.Rendering.CameraUpdateSystem cameras = m_Cameras;
            m_Mirrors.Update(cameras != null && cameras.activeCamera != null ? cameras.activeCamera : Camera.main, true, Mod.Settings.MirrorQuality);
        }

        private void ReleaseReaders()
        {
            m_Mirrors.Destroy();
            m_MirrorInstance = null;
            ReleaseReaderLights();
        }

        private void ReleaseReaderLights()
        {
            foreach (ReaderLights lights in m_Readers.Values)
                lights.Lamps?.Destroy();
            m_Readers.Clear();
        }
    }
}
