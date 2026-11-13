using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Unity.Entities;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The passenger mode in the bus model (BusRide, BusRideSystem): a bus the game drives has no steering wheel angle, brake lamps or door
    /// state for the driving mod to tell, so the model reads them off the bus's motion and the game's boarding flag, and the camera starts in a
    /// passenger's seat instead of the driver's.
    /// </summary>
    public sealed partial class BusCabSystem
    {
        private Entity m_RideEstimateBus = Entity.Null;
        private bool m_RideHasLast;
        private float m_RideYaw, m_RideSpeed, m_RideWheel, m_RideAccel;

        /// <summary>
        /// While riding: the road wheels' angle (from the bus's yaw rate and speed, the bicycle model) and the brake lamps (it slows down hard, or
        /// stands with its doors open), set in the frame's readouts for what the model draws. The rest of the readouts are the driving mod's own.
        /// </summary>
        private void RideMotion(float dt)
        {
            if (!m_Core.Riding || m_Sections.Count == 0)
            {
                m_RideHasLast = false;
                return;
            }

            if (m_RideEstimateBus != m_Bus)
            {
                m_RideEstimateBus = m_Bus;
                m_RideHasLast = false;
                m_RideWheel = m_RideAccel = 0f;
            }

            if (!m_Core.BodyPosed || dt <= 1e-5f)
            {
                m_Core.WheelAngle = m_RideWheel;
                m_Core.Braking = BusRideRules.Braking(m_RideAccel, m_Core.WorldSpeed, BusRide.DoorsOpen);
                return;
            }

            Vector3 forward = m_Core.BodyRotation * Vector3.forward;
            float yaw = Mathf.Atan2(forward.x, forward.z);
            float speed = m_Core.WorldSpeed;
            if (m_RideHasLast)
            {
                float rate = Mathf.DeltaAngle(m_RideYaw * Mathf.Rad2Deg, yaw * Mathf.Rad2Deg) * Mathf.Deg2Rad / dt;
                BusInteriorSpec spec = m_Sections[0].Spec;
                m_RideWheel = BusRideRules.Smooth(m_RideWheel, BusRideRules.WheelAngle(spec.FrontAxleZ - spec.RearAxleZ, rate, speed), dt, 0.15f);
                m_RideAccel = BusRideRules.Smooth(m_RideAccel, (speed - m_RideSpeed) / dt, dt, 0.3f);
            }

            m_RideYaw = yaw;
            m_RideSpeed = speed;
            m_RideHasLast = true;
            m_Core.WheelAngle = m_RideWheel;
            m_Core.Braking = BusRideRules.Braking(m_RideAccel, speed, BusRide.DoorsOpen);
        }

        /// <summary>
        /// A ride begins in a passenger's seat: the bus's "frontPassenger" camera, else its first seat that is not the driver's. Once, when the
        /// bus's cameras are with the driving mod; a bus with none keeps the driver's seat (camera 0).
        /// </summary>
        private void PickPassengerSeat()
        {
            if (!BusRide.NeedsSeat || BusRide.Bus != m_Bus || !m_Core.Riding)
                return;

            BusRide.NeedsSeat = false;
            if (!Core.HasApi14 || m_OtherCameras.Count == 0)
                return;

            int pick = -1;
            for (int i = 0; i < m_OtherCameras.Count; i++)
            {
                VehicleProfile.CameraData camera = m_OtherCameras[i].Camera;
                if (camera.Mounted)
                    continue;
                if (camera.Name == "frontPassenger")
                {
                    pick = i;
                    break;
                }

                if (pick < 0)
                    pick = i;
            }

            if (pick >= 0)
                Core.SelectCamera(pick + 1);
        }
    }
}
