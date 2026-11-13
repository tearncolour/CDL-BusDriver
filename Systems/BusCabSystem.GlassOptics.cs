#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using CDLC.BusDriver.Interior.Models;
using Unity.Entities;
using UnityEngine;

namespace CDLC.BusDriver
{
    public sealed partial class BusCabSystem
    {
        /// <summary>
        /// Whether the cab draws the exact controlled bus a glass capture is about: an authored assembly through
        /// TryGetAssemblyProbeState; a single-section bus (<paramref name="rear"/> null) with its one replacement instance
        /// visible, its panes built and the body pose of this frame.
        /// </summary>
        internal bool TryGetGlassProbeState(Entity front, Entity rear, out object evidence)
        {
            if (rear != Entity.Null) return TryGetAssemblyProbeState(front, rear, out evidence);
            bool single = m_Bus == front && m_Assembly == null && m_Sections.Count == 1 && m_Sections[0].Entity == front;
            Section section = single ? m_Sections[0] : null;
            bool instance = section?.Instance != null && section.Instance.Alive && section.Instance.Visible;
            bool glass = section?.Glass?.Ready == true;
            var visibleReplaced = new List<int>();
            bool nativeReady = section != null && NativeReady(section);
            if (section?.Native?.Ready == true)
                foreach (int part in NativeCabOwnership.ReplacedParts(VehicleModels.Find(section.Spec.ModelVariant).Mesh))
                {
                    var renderer = section.Instance.Part(part)?.GetComponent<Renderer>();
                    if (renderer != null && renderer.enabled) visibleReplaced.Add(part);
                }
            bool ready = single && instance && glass && nativeReady && visibleReplaced.Count == 0 && m_Core.BodyPosed && section.HeldFrames == 0;
            evidence = new { ready, front = front.ToString(), single_section = single, instance_visible = instance,
                glass_built = glass, native_ready = nativeReady, visible_replaced_parts = visibleReplaced,
                body_pose_current = m_Core.BodyPosed };
            return ready;
        }

        internal List<BusGlassOpticsBinding> CollectGlassOpticsBindings(Entity front, Entity rear)
        {
            if (!TryGetGlassProbeState(front, rear, out _))
                throw new InvalidOperationException("Optics requires the same complete controlled bus");
            var result = new List<BusGlassOpticsBinding>();
            foreach (Section section in m_Sections)
                section.Glass.CollectOpticsBindings(result, section.Entity.ToString());
            return result;
        }

        internal BusRoadGlassMirrorFrames WatchGlassMirrors(Entity front, Entity rear, Camera eye, RenderTexture target,
            Action check, Func<bool> owns, IDictionary<string, object> report)
        {
            if (!TryGetGlassProbeState(front, rear, out _))
                throw new InvalidOperationException("Mirror observation requires the same complete controlled bus");
            return new BusRoadGlassMirrorFrames(m_Mirrors, m_MirrorInstance, eye, target, check, owns, report);
        }
    }
}
#endif
