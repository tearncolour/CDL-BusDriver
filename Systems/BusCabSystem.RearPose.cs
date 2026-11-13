using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The towed section of an authored two-section assembly as the cab draws it: Core's render pose of the rear, hung on the
    /// front's anchor and given the kerb roll of the kneel. One read per frame, shared by the readiness checks, the section
    /// poses and the joint.
    /// </summary>
    public sealed partial class BusCabSystem
    {
        // Core draws the front body from its own ring (+ sway + kneel) and the rear body from the rear's ring (+ its own sway), so
        // the two anchors differ by interpolation and sway residue: centimetres that flicker the joint, and a curve or a crest
        // that opens them past MaxSeparation hid the whole assembly. The game's own trailer job (ObjectInterpolateSystem) hangs
        // a trailer on the swayed hitch of the unit ahead; the rear is moved the same way, by the offset between the two anchors,
        // up to this distance. Beyond it the pair really is torn apart and the native bodies come back.
        private const float kPinMeters = .45f;

        // The assembly drops back to the native bodies only after this many bad frames in a row, and returns after a longer
        // calm spell once it has dropped, so one bad frame of a crest does not toggle two bodies' Hidden flags.
        private const int kDropFrames = 6, kRaiseFrames = 30;

        private struct RearFrame
        {
            internal bool Valid;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal quaternion Kneel;          // the roll of the kneel the front body was given, for the rear's wheels
            internal float3 KneelOffset;        // the rear body's offset from its unkneeled place, in its own frame
            internal float RawGap;              // the anchors' distance before the rear was hung on the front
            internal bool Pinned;
        }

        private RearFrame m_Rear;
        private bool m_AssemblyUp, m_AssemblyDropped;
        private int m_BadFrames, m_GoodFrames;
        private string m_NotReady = "";
        private float m_NextTransitionLog;

        /// <summary>Reads the rear section's pose for this frame (call once per frame, after m_Core).</summary>
        private void ReadRearPose()
        {
            m_Rear = new RearFrame { Rotation = Quaternion.identity, Kneel = quaternion.identity };
            if (m_Assembly == null || m_Sections.Count != 2 || !m_Core.BodyPosed ||
                !Core.TryGetRenderPose(m_Sections[1].Entity, out Vector3 position, out Quaternion rotation))
                return;

            Vector3 frontAnchor = ToVector(m_Assembly.FrontAnchor);
            Vector3 rearAnchor = ToVector(m_Assembly.RearAnchor);
            if (m_Core.HasApi4 && Core.KneelLevel() > 0f)
            {
                // Core knelt only the front body; the rear takes the roll of it and the front anchor's move (see RearKneel).
                Core.GetKneelTransform(out Vector3 drop, out Quaternion k);
                RearKneel.Compute(Q(m_Core.BodyRotation), Q(k), new float3(drop.x, drop.y, drop.z), new float3(frontAnchor.x, frontAnchor.y, frontAnchor.z),
                    out float3 delta, out quaternion roll);
                rotation *= new Quaternion(roll.value.x, roll.value.y, roll.value.z, roll.value.w);
                position += ToVector(delta);
                m_Rear.Kneel = roll;
                m_Rear.KneelOffset = RearKneel.LocalOffset(Q(rotation), delta);
            }

            Vector3 pin = (m_Core.BodyPosition + m_Core.BodyRotation * frontAnchor) - (position + rotation * rearAnchor);
            m_Rear.RawGap = pin.magnitude;
            if (m_Rear.RawGap <= kPinMeters)
            {
                position += pin;
                m_Rear.Pinned = true;
            }

            m_Rear.Position = position;
            m_Rear.Rotation = rotation;
            m_Rear.Valid = math.isfinite(position.x + position.y + position.z) &&
                math.isfinite(rotation.x + rotation.y + rotation.z + rotation.w);
        }

        private bool AssemblyCanReplace()
        {
            if (m_Assembly == null) return true;
            if (m_Sections.Count != 2) return NotReady("section count");
            if (!m_Core.BodyPosed) return NotReady("no front body pose");
            if (m_Sections[0].Glass?.Ready != true || m_Sections[1].Glass?.Ready != true)
                return NotReady(m_Sections[0].Glass?.Ready != true ? "front glass missing" : "rear glass missing");
            if (!m_Rear.Valid) return NotReady("no rear render pose");
            Vector3 frontAnchor = m_Core.BodyPosition + m_Core.BodyRotation * ToVector(m_Assembly.FrontAnchor);
            Vector3 rearAnchor = m_Rear.Position + m_Rear.Rotation * ToVector(m_Assembly.RearAnchor);
            float distance = Vector3.Distance(frontAnchor, rearAnchor);
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance > m_Assembly.MaxSeparation)
                return NotReady("anchors " + distance.ToString("0.000") + " m apart (raw " + m_Rear.RawGap.ToString("0.000") + " m)");
            return true;
        }

        private static quaternion Q(Quaternion q) => new quaternion(q.x, q.y, q.z, q.w);

        private bool NotReady(string reason)
        {
            m_NotReady = reason;
            return false;
        }

        /// <summary>The raw per-frame readiness of the authored assembly, debounced into the state the cab acts on.</summary>
        private bool DebouncedAssemblyReady(bool ready)
        {
            if (ready)
            {
                m_BadFrames = 0;
                m_GoodFrames++;
                if (!m_AssemblyUp && m_GoodFrames >= (m_AssemblyDropped ? kRaiseFrames : 1))
                    SetAssemblyUp(true);
            }
            else
            {
                m_GoodFrames = 0;
                m_BadFrames++;
                if (m_AssemblyUp && m_BadFrames >= kDropFrames)
                    SetAssemblyUp(false);
            }

            return m_AssemblyUp;
        }

        private void SetAssemblyUp(bool up)
        {
            m_AssemblyUp = up;
            if (!up) m_AssemblyDropped = true;
            float now = UnityEngine.Time.unscaledTime;
            if (now < m_NextTransitionLog) return;
            m_NextTransitionLog = now + 1f;
            Mod.log.Info("Authored assembly " + (up ? "drawn" : "dropped to the native bodies") + " at frame " + UnityEngine.Time.frameCount +
                (up ? "" : ": " + m_NotReady) + "; speed " + (m_Core.Speed * 3.6f).ToString("0.0") + " km/h, kneel " + (m_Core.HasApi4 ? Core.KneelLevel() : 0f).ToString("0.00") +
                ", rear " + (m_Rear.Valid ? (m_Rear.Pinned ? "hung on the front anchor (raw " + m_Rear.RawGap.ToString("0.000") + " m)" : "not hung, raw gap " + m_Rear.RawGap.ToString("0.000") + " m") : "unread") + ".");
        }

        private void ResetAssemblyState()
        {
            m_Rear = default;
            m_AssemblyUp = m_AssemblyDropped = false;
            m_BadFrames = m_GoodFrames = 0;
            m_NotReady = "";
        }
    }
}
