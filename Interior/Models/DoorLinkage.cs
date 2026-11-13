using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>How the linkage of a leaf holds it (doors[].linkage[].kind of a version 4 profile; before that only the guide rail existed).</summary>
    internal enum DoorLinkageKind : byte
    {
        /// <summary>A roller on the top of the leaf runs in a straight rail along the body, so the leaf turns as it moves out.</summary>
        GuideRail,
        /// <summary>A drive arm and a balance arm of equal length turn on two fixed shafts and carry the leaf as a parallelogram: it keeps its closed heading.</summary>
        ParallelFourBar,
    }

    /// <summary>
    /// One outward-swing door leaf's linkage (外摆门), in the model's plan (x across the bus, z along it; metres), as a vehicle
    /// profile declares it (doors[].linkage). A fixed vertical door shaft turns its arms through <see cref="ShaftRadians"/> while the
    /// door opens; the arms carry the leaf at a joint, and a roller on the leaf's top (its guide) runs in a straight rail along the
    /// body (through the guide's closed position, along z). So the leaf turns and moves at once, out of its opening and round beside
    /// it, instead of turning about a hinge post. All positions are the closed ones; the closed pose is exactly the rest pose.
    /// Angles turn as quaternion.RotateY does: a positive angle takes +z towards +x.
    /// The <see cref="DoorLinkageKind.ParallelFourBar"/> kind has no roller: a second fixed shaft (<see cref="BalanceShaft"/>) with its own
    /// arm to a second joint on the leaf (<see cref="BalanceJoint"/>), which must make a parallelogram with the first (the vectors of the
    /// two arms equal): both arms turn through the same angle, the joints of the leaf go along the arcs of the arms and the leaf does not turn.
    /// </summary>
    internal readonly struct DoorLinkage
    {
        internal const int Samples = 64;
        internal const float MinSlack = .005f;          // the roller's link never closer than this to square with the rail (a dead point)
        internal const float ParallelTolerance = .001f; // the most the vectors of the two arms of a four-bar may differ by (metres)
        internal const float MinShaftGap = .02f;        // the two shafts of a four-bar are at least this far apart

        internal readonly DoorLinkageKind Kind;
        internal readonly float2 Shaft, Joint, Guide;
        internal readonly float ShaftRadians;
        internal readonly int DrivePart;                 // the part that turns with the shaft (its arms), -1 for none
        // four-bar only: the second fixed shaft, its joint on the leaf, and the part that turns about it (-1 for none)
        internal readonly float2 BalanceShaft, BalanceJoint;
        internal readonly int BalanceDrivePart;
        private readonly float m_Link, m_Branch;

        internal DoorLinkage(float2 shaft, float2 joint, float2 guide, float shaftDegrees, int drivePart)
        {
            Kind = DoorLinkageKind.GuideRail;
            Shaft = shaft; Joint = joint; Guide = guide; ShaftRadians = math.radians(shaftDegrees); DrivePart = drivePart;
            BalanceShaft = BalanceJoint = float2.zero; BalanceDrivePart = -1;
            m_Link = math.distance(joint, guide);
            m_Branch = guide.y >= joint.y ? 1f : -1f;
        }

        private DoorLinkage(float2 shaft, float2 joint, float2 balanceShaft, float2 balanceJoint, float shaftDegrees, int drivePart, int balanceDrivePart)
        {
            Kind = DoorLinkageKind.ParallelFourBar;
            Shaft = shaft; Joint = joint; Guide = float2.zero; ShaftRadians = math.radians(shaftDegrees); DrivePart = drivePart;
            BalanceShaft = balanceShaft; BalanceJoint = balanceJoint; BalanceDrivePart = balanceDrivePart;
            m_Link = 0f; m_Branch = 0f;
        }

        /// <summary>A parallel four-bar linkage: <see cref="Check"/> says whether the numbers make a parallelogram.</summary>
        internal static DoorLinkage ParallelFourBar(float2 shaft, float2 joint, float2 balanceShaft, float2 balanceJoint, float shaftDegrees, int drivePart, int balanceDrivePart) =>
            new DoorLinkage(shaft, joint, balanceShaft, balanceJoint, shaftDegrees, drivePart, balanceDrivePart);

        /// <summary>Turns <paramref name="v"/> (x, z) by <paramref name="a"/> as quaternion.RotateY(a) does.</summary>
        internal static float2 Turn(float2 v, float a)
        {
            math.sincos(a, out float s, out float c);
            return new float2(v.x * c + v.y * s, -v.x * s + v.y * c);
        }

        // The heading of a plan direction: Turn(v, a) adds a to it.
        private static float Heading(float2 v) => -math.atan2(v.y, v.x);

        private static float Wrap(float a) => a - 2f * math.PI * math.floor((a + math.PI) / (2f * math.PI));

        /// <summary>
        /// The linkage at opening <paramref name="open"/> (0 shut .. 1 open, eased like every door, once): the shaft's turn (the balance shaft of a
        /// four-bar turns by the same angle), where the joint is now and how far the leaf has turned about it, and how far the roller's link is from
        /// square with its rail (its slack). False when the roller has no place on the rail. The leaf of a four-bar never turns (leafYaw 0) and has
        /// no roller (slack 0).
        /// </summary>
        internal bool Solve(float open, out float shaft, out float2 joint, out float leafYaw, out float slack)
        {
            shaft = ShaftRadians * BusInterior.DoorEase(open);
            joint = Shaft + Turn(Joint - Shaft, shaft);
            leafYaw = 0f; slack = 0f;
            if (Kind == DoorLinkageKind.ParallelFourBar) return true;
            float dx = Guide.x - joint.x, d2 = m_Link * m_Link - dx * dx;
            if (!(d2 > 0f)) return false;
            slack = math.sqrt(d2);
            var guide = new float2(Guide.x, joint.y + m_Branch * slack);
            leafYaw = Wrap(Heading(guide - joint) - Heading(Guide - Joint));
            return true;
        }

        /// <summary>Where a rest point of the leaf (model space) is with the joint at <paramref name="joint"/> and the leaf turned by <paramref name="leafYaw"/>.</summary>
        internal float3 Move(float3 rest, float2 joint, float leafYaw)
        {
            float2 p = joint + Turn(rest.xz - Joint, leafYaw);
            return new float3(p.x, rest.y, p.y);
        }

        /// <summary>Where the balance joint is with the balance shaft turned by <paramref name="shaft"/> (the angle of the drive shaft): a four-bar only.</summary>
        internal float2 BalanceJointAt(float shaft) => BalanceShaft + Turn(BalanceJoint - BalanceShaft, shaft);

        /// <summary>Null when the linkage moves the leaf all the way open without a dead point or a jump; else why not.</summary>
        internal string Check()
        {
            if (Kind == DoorLinkageKind.ParallelFourBar) return CheckFourBar();
            if (math.distance(Joint, Shaft) < .03f) return "the joint must be at least 3 cm from the shaft";
            if (m_Link < .05f) return "the roller must be at least 5 cm from the joint";
            float degrees = math.degrees(math.abs(ShaftRadians));
            if (degrees < 5f || degrees > 170f) return "shaftDegrees must turn 5..170 degrees";
            if (!Solve(1f, out _, out _, out float end, out _)) return "the roller has no place on its rail when open";
            float last = 0f, way = math.sign(end);
            for (int i = 0; i <= Samples; i++)
            {
                if (!Solve(i / (float)Samples, out _, out _, out float yaw, out float slack) || slack < MinSlack)
                    return $"the roller reaches a dead point at {100f * i / Samples:0}% open";
                if (math.abs(yaw) > math.radians(170f) || math.abs(yaw - last) > .25f)
                    return $"the leaf turns too far or jumps at {100f * i / Samples:0}% open";
                // one way only: a leaf that turns back has passed its roller's dead point
                if ((yaw - last) * way < -1e-4f)
                    return $"the leaf turns back at {100f * i / Samples:0}% open";
                last = yaw;
            }
            return null;
        }

        // The numbers of a four-bar make a parallelogram (J - S = K - B, which is K - J = B - S) with two separate shafts: then the two joints of the
        // leaf move by the same step all along the travel. An arbitrary quadrilateral is refused, not forced into a leaf that does not turn.
        private string CheckFourBar()
        {
            if (!math.all(math.isfinite(Shaft)) || !math.all(math.isfinite(Joint)) || !math.all(math.isfinite(BalanceShaft)) ||
                !math.all(math.isfinite(BalanceJoint)) || !math.isfinite(ShaftRadians))
                return "every shaft, joint and angle must be a finite number";
            if (math.distance(Joint, Shaft) < .03f) return "the main arm must be at least 3 cm long (shaft to joint)";
            if (math.distance(BalanceShaft, Shaft) < MinShaftGap) return "the two shafts must be at least 2 cm apart";
            float off = math.length((Joint - Shaft) - (BalanceJoint - BalanceShaft));
            if (off > ParallelTolerance)
                return "the balance arm must equal the main arm (joint - shaft = balanceJoint - balanceShaft): they differ by " +
                       (off * 1000f).ToString("0.0") + " mm, the limit is " + (ParallelTolerance * 1000f).ToString("0") + " mm";
            float degrees = math.degrees(math.abs(ShaftRadians));
            if (degrees < 5f || degrees > 170f) return "shaftDegrees must turn 5..170 degrees";
            if (DrivePart == BalanceDrivePart) return "the main and the balance drive parts must be two different parts";
            return null;
        }
    }

    /// <summary>
    /// The doors of one body section following the shift's door command: each opens and closes at its own rate, and one with a close
    /// delay starts to close only that long after the command (the order a door controller gives the doors: the rear door first).
    /// Values 0 shut .. 1 open, per door (x, y, z, w = doors 0..3). An explicit platform side stays latched until all doors close;
    /// absent channels stay shut. Bilateral profiles default to the right side until a caller selects a side.
    /// Legacy profiles retain their single-side behavior.
    /// </summary>
    internal sealed class DoorMotion
    {
        private float4 m_Open;
        private float4 m_SinceClose;
        private float m_ActiveSide;

        internal float3 Channels => m_Open.xyz;
        internal float Fourth => m_Open.w;
        internal float Max => math.cmax(m_Open);
        internal bool Moving => math.any(m_Open > .001f & m_Open < .999f);

        /// <summary>One frame: <paramref name="open"/> is the command, the timing the profile's (null: the defaults for every door).</summary>
        internal void Step(bool open, float dt, VehicleProfile profile, float platformSide = 0f)
        {
            int doors = profile?.Doors.Length ?? 3;
            float side = platformSide == 0f ? 1f : math.sign(platformSide);
            if (platformSide == 0f && profile?.DoorSides != null)
            {
                bool oneSide = true;
                for (int d = 1; d < doors; d++) oneSide &= profile.DoorSide(d) == profile.DoorSide(0);
                if (oneSide) side = profile.DoorSide(0);
            }
            if (Max <= .001f) m_ActiveSide = side;
            for (int d = 0; d < 4; d++)
            {
                if (d >= doors) { m_Open[d] = 0f; continue; }
                bool requested = open && (profile == null || profile.DoorSides == null || profile.DoorSide(d) == m_ActiveSide);
                m_SinceClose[d] = requested ? 0f : math.min(m_SinceClose[d] + dt, 1e4f);
                VehicleProfile.DoorTiming timing = profile?.Timing(d) ?? VehicleProfile.DoorTiming.Default;
                float v = m_Open[d];
                if (requested) v = math.min(1f, v + dt / timing.OpenSeconds);
                else if (m_SinceClose[d] > timing.CloseDelay) v = math.max(0f, v - math.min(dt, m_SinceClose[d] - timing.CloseDelay) / timing.CloseSeconds);
                m_Open[d] = v;
            }
        }
    }
}
