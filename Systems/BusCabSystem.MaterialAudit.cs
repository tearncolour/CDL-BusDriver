using System;
using System.Collections.Generic;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>The material audit (BusMaterialAudit) of the bus the player drives or rides: its renderers, its materials, and what the game has for decals.</summary>
    public sealed partial class BusCabSystem
    {
        private readonly BusMaterialAudit m_MaterialAudit = new BusMaterialAudit();
        private readonly List<Transform> m_AuditRoots = new List<Transform>();
        private bool m_MaterialAuditFailed;

        /// <summary>Called every frame from Run after the native body's check. A failure of the audit stops the audit, never the bus.</summary>
        private void AuditMaterials()
        {
            if (m_MaterialAuditFailed)
                return;

            try
            {
                if (!m_Core.Viewing)
                {
                    m_MaterialAudit.Stop();
                    return;
                }

                double now = UnityEngine.Time.unscaledTimeAsDouble;
                if (!m_MaterialAudit.Due(now))
                    return;

                m_AuditRoots.Clear();
                foreach (Section section in m_Sections)
                    if (section.Instance != null && section.Instance.Alive && section.Instance.Root != null)
                        m_AuditRoots.Add(section.Instance.Root);
                m_MaterialAudit.Update(now, $"{(m_Core.Riding ? "ridden" : "driven")} {m_Bus}", m_AuditRoots);
            }
            catch (Exception failure)
            {
                m_MaterialAuditFailed = true;
                Mod.log.Warn($"Material audit stopped: {failure}");
                try { m_MaterialAudit.Stop(); }
                catch (Exception cleanup) { Mod.log.Warn($"Material audit: putting back what the experiment changed failed too: {cleanup.Message}"); }
            }
        }
    }
}
