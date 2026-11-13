using System;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver.Interior
{
    /// <summary>
    /// The real head lamps of one of our buses: a spot light at each lamp of the front section, pointing forward and a little down,
    /// the dipped beam wide and short, the main beam narrow and long (BeamSpec). No shadows and no volumetric fog, so two lights cost
    /// little; they fade out with the daylight (a spot light adds nothing to a sunlit road) and are off while the lamps give no
    /// beam. The lights hang under the model's root, so they move with the bus and go with it when it is hidden. Main thread only.
    /// </summary>
    internal sealed class CabLights
    {
        // Daylight (the game's 0..1) over which the lights fade out: full below kFadeStart, gone above kFadeEnd.
        private const float kFadeStart = 0.35f, kFadeEnd = 0.75f;
        private static readonly Color s_LowColour = new Color(1f, 0.93f, 0.82f), s_HighColour = new Color(0.9f, 0.95f, 1f);

        private sealed class Lamp
        {
            internal GameObject Object;
            internal Light Light;
            internal HDAdditionalLightData Data;
        }

        private Lamp[] m_Lamps = Array.Empty<Lamp>();
        private Transform m_Root;
        private Beam m_Applied = Beam.Off;
        private float m_AppliedIntensity = -1f;
        private bool m_Failed;

        internal bool Alive => m_Root != null && m_Lamps.Length == 2 && m_Lamps[0] != null && m_Lamps[0].Object != null && m_Lamps[1].Object != null;

        /// <summary>Makes the two lamps on a bus model (its root) at the given model-space positions. False when the pipeline does not allow it.</summary>
        internal bool Create(Transform root, float3 first, float3 second)
        {
            Destroy();
            if (m_Failed || root == null)
                return false;

            try
            {
                m_Root = root;
                m_Lamps = new[] { Make(root, first, 0), Make(root, second, 1) };
                m_Applied = Beam.Off;
                m_AppliedIntensity = -1f;
                return true;
            }
            catch (Exception ex)
            {
                m_Failed = true;
                Destroy();
                Mod.log.Warn($"Head lamps: the spot lights could not be made ({ex.GetType().Name}: {ex.Message}); the lamps only glow.");
                return false;
            }
        }

        private static Lamp Make(Transform root, float3 position, int index)
        {
            var go = new GameObject("Bus Driver head lamp " + index) { hideFlags = HideFlags.DontSave, layer = root.gameObject.layer };
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(position.x, position.y, position.z);
            go.transform.localRotation = Quaternion.identity;
            go.SetActive(false);
            Light light = go.AddComponent<Light>();
            light.type = UnityEngine.LightType.Spot;
            light.shadows = LightShadows.None;
            var data = go.AddComponent<HDAdditionalLightData>();
            return new Lamp { Object = go, Light = light, Data = data };
        }

        /// <summary>
        /// Sets the beam: what the lamps give now, the game's daylight (0 night .. 1 sun) and the strength option (1 = the mod's value).
        /// Off, or daylight, switches the lights off.
        /// </summary>
        internal void Update(Beam beam, float daylight, float strength)
        {
            if (!Alive)
                return;

            float fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(kFadeStart, kFadeEnd, daylight));
            bool on = beam != Beam.Off && fade > 0.01f && strength > 0.001f;
            BeamSpec spec = BeamSpec.For(beam);
            float intensity = on ? spec.Candela * fade * strength : 0f;
            foreach (Lamp lamp in m_Lamps)
            {
                if (lamp.Object.activeSelf != on)
                    lamp.Object.SetActive(on);
                if (!on)
                    continue;

                if (beam != m_Applied)
                {
                    lamp.Light.range = spec.Range;
                    lamp.Light.color = beam == Beam.High ? s_HighColour : s_LowColour;
                    lamp.Data.SetSpotAngle(spec.OuterAngle, spec.InnerPercent);
                    lamp.Object.transform.localRotation = Quaternion.Euler(spec.PitchDown, 0f, 0f);
                }
                if (beam != m_Applied || Mathf.Abs(intensity - m_AppliedIntensity) > 1f)
                    lamp.Data.SetIntensity(intensity, LightUnit.Candela);
            }

            if (on)
            {
                m_Applied = beam;
                m_AppliedIntensity = intensity;
            }
        }

        internal void Destroy()
        {
            foreach (Lamp lamp in m_Lamps)
                if (lamp != null && lamp.Object != null)
                    UnityEngine.Object.Destroy(lamp.Object);
            m_Lamps = Array.Empty<Lamp>();
            m_Root = null;
            m_Applied = Beam.Off;
        }
    }
}
