using System;
using CDLC.Asset.Articulation;
using Unity.Entities;
using UnityEngine;

namespace CDLC.BusDriver
{
    public sealed partial class BusCabSystem
    {
        private JointRenderer m_AuthoredJoint;
        private string m_JointFailure;

        private ArticulationSystem m_ArticulationJoints;
        private ArticulationSystem ArticulationJoints => m_ArticulationJoints ??= World.GetOrCreateSystemManaged<ArticulationSystem>();

        /// <summary>Read-only evidence for the disposable road test. Never advances the pose or builds a model.</summary>
        internal bool TryGetAssemblyProbeState(Entity front, Entity rear, out object evidence)
        {
            bool pair = m_Bus == front && m_Sections.Count == 2 &&
                m_Sections[0].Entity == front && m_Sections[1].Entity == rear;
            bool instances = pair && m_Sections[0].Instance != null && m_Sections[1].Instance != null &&
                m_Sections[0].Instance.Alive && m_Sections[1].Instance.Alive &&
                m_Sections[0].Instance.Visible && m_Sections[1].Instance.Visible;
            bool glass = pair && m_Sections[0].Glass?.Ready == true && m_Sections[1].Glass?.Ready == true;
            Mesh mesh = m_AuthoredJoint?.CurrentMesh;
            MeshRenderer renderer = m_AuthoredJoint?.Root?.GetComponent<MeshRenderer>();
            bool joint = mesh != null && mesh.vertexCount > 0 && renderer != null && renderer.enabled &&
                renderer.gameObject.activeInHierarchy;
            float? gap = null;
            if (pair && m_Assembly != null)
                gap = Vector3.Distance(m_Sections[0].HeldPosition+m_Sections[0].HeldRotation*ToVector(m_Assembly.FrontAnchor),
                    m_Sections[1].HeldPosition+m_Sections[1].HeldRotation*ToVector(m_Assembly.RearAnchor));
            bool ready = pair && instances && glass && joint && m_Assembly != null && !m_AssemblyPending &&
                m_AssemblyBindFailure == null && m_JointFailure == null && m_Core.BodyPosed &&
                m_Sections[0].HeldFrames == 0 && m_Sections[1].HeldFrames == 0 &&
                gap.HasValue && gap.Value <= m_Assembly.MaxSeparation;
            evidence = new { ready, front = front.ToString(), rear = rear.ToString(), same_pair = pair,
                both_instances_visible = instances, both_glass_built = glass, joint_visible = joint,
                joint_vertices = mesh != null ? mesh.vertexCount : 0, attachment_gap_m = gap,
                binding_pending = m_AssemblyPending, binding_failure = m_AssemblyBindFailure,
                joint_failure = m_JointFailure, body_pose_current = m_Core.BodyPosed };
            return ready;
        }

        private bool AuthoredJointReady()
        {
            if (m_Assembly == null) return true;
            if (m_JointFailure != null) return false;
            if (m_Sections.Count != 2 || !m_Core.BodyPosed || !m_Rear.Valid) return NotReady("joint endpoints unread");
            if (m_Articulated.JointFailure != null)
            {
                m_JointFailure = m_Articulated.JointFailure;
                Mod.log.Warn("Authored joint rejected by CDL Asset; native bodies retained: "+m_JointFailure);
                return false;
            }
            if (!m_Articulated.JointReady) return NotReady("joint mesh still being prepared by CDL Asset");
            try
            {
                if (m_AuthoredJoint == null) m_AuthoredJoint = new JointRenderer(m_Articulated);
                // Validate both live endpoints/bend before the caller hides either native body. Only a check: the joint drawn
                // (UpdateAuthoredJoint, later in this frame) is not switched off and on again every frame for it.
                return m_AuthoredJoint.Validate(m_Core.BodyPosition,m_Core.BodyRotation,m_Rear.Position,m_Rear.Rotation) || NotReady("joint endpoints rejected");
            }
            catch (Exception e)
            {
                m_JointFailure = e.Message;
                m_AuthoredJoint?.Dispose(); m_AuthoredJoint = null;
                Mod.log.Warn("Authored joint rejected; native bodies retained: "+e.Message);
                return false;
            }
        }

        private void UpdateAuthoredJoint(bool visible)
        {
            if (m_AuthoredJoint == null) return;
            bool ready = visible && m_Sections.Count == 2 && m_Sections[0].HeldFrames == 0 && m_Sections[1].HeldFrames == 0;
            if (ready)
            {
                Section f = m_Sections[0], r = m_Sections[1];
                ready = m_AuthoredJoint.Update(f.HeldPosition,f.HeldRotation,r.HeldPosition,r.HeldRotation,0,true);
            }
            if (!ready) m_AuthoredJoint.Hide();
            ArticulationJoints.SetClaim(m_Bus,ready);
        }

        private void ReleaseAuthoredJoint()
        {
            World.GetExistingSystemManaged<ArticulationSystem>()?.SetClaim(m_Bus,false);
            m_AuthoredJoint?.Dispose(); m_AuthoredJoint = null; m_JointFailure = null;
        }
    }
}
