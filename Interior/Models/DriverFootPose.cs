using System;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    // Optional vehicle-frame sole contact frames. No vehicle name or dimension is special-cased.
    internal readonly struct DriverFootFrame
    {
        internal readonly float3 Origin, KneePole;
        internal readonly quaternion Rotation;
        internal DriverFootFrame(float3 origin, quaternion rotation, float3 kneePole)
        { Origin = origin; Rotation = rotation; KneePole = kneePole; }
        internal DriverFootFrame InSeat(BusInterior.RiderSlot seat)
        {
            quaternion inv = quaternion.RotateY(-seat.Yaw);
            return new DriverFootFrame(math.mul(inv, Origin-seat.Position), math.mul(inv, Rotation), math.mul(inv, KneePole));
        }
    }

    internal readonly struct DriverFeetData
    {
        internal readonly DriverFootFrame Left, Right;
        internal DriverFeetData(DriverFootFrame left, DriverFootFrame right) { Left=left; Right=right; }
    }

    internal readonly struct DriverFootPose
    {
        internal readonly DriverFootFrame Left, Right;
        internal readonly float3 LeftKnee, LeftAnkle, RightKnee, RightAnkle;
        internal readonly float LeftResidual, RightResidual;
        internal DriverFootPose(DriverFeetData feet, BusInterior.RiderSlot seat)
        {
            Left=feet.Left.InSeat(seat); Right=feet.Right.InSeat(seat);
            Solve(new float3(-.095f,.08f,-.05f),new float3(-.12f,.08f,.40f),new float3(-.12f,-.44f,.55f),Left,
                out LeftKnee,out LeftAnkle,out LeftResidual);
            Solve(new float3(.095f,.08f,-.05f),new float3(.12f,.08f,.40f),new float3(.13f,-.44f,.55f),Right,
                out RightKnee,out RightAnkle,out RightResidual);
        }
        private static void Solve(float3 hip,float3 knee,float3 ankle,DriverFootFrame foot,out float3 solvedKnee,out float3 solvedAnkle,out float residual)
        {
            // BusRiders.Legs: sole bottom is ankle+(0,-.091,.06).
            float3 target=foot.Origin+math.mul(foot.Rotation,new float3(0f,.091f,-.06f));
            TwoBoneIk.Solve(hip,knee,ankle,quaternion.identity,quaternion.identity,target,foot.KneePole,
                out _,out _,out solvedKnee,out solvedAnkle);
            residual=math.distance(solvedAnkle,target);
            if (!math.all(math.isfinite(solvedKnee)) || !math.all(math.isfinite(solvedAnkle)) || residual>.001f)
                throw new ArgumentException("Driver foot anchor is unreachable (residual "+residual.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" m)");
        }
    }

    // A single shared procedural driver, using the existing body/material/LOD generator.
    // It is deliberately separate from the legacy 37-part passenger model and its cache.
    internal sealed class DriverFootFigure : KitSource
    {
        private readonly DriverFootPose m_Pose;
        internal DriverFootFigure(DriverFootPose pose) { m_Pose=pose; }
        public override KitModelKey Key => new KitModelKey("bus-driver-feet")
            .With(unchecked((int)math.hash(m_Pose.Left.Origin))).With(unchecked((int)math.hash(m_Pose.Right.Origin)))
            .With(unchecked((int)math.hash(m_Pose.Left.Rotation.value))).With(unchecked((int)math.hash(m_Pose.Right.Rotation.value)))
            .With(unchecked((int)math.hash(m_Pose.LeftKnee))).With(unchecked((int)math.hash(m_Pose.RightKnee)));
        public override void Parts(KitPartList parts) { parts.Add(0,"Driver"); }
        public override void Define(KitModelBuilder builder,int lod) { BusRiders.DrawAnchoredDriver(builder.Part(0),lod,m_Pose); }
    }
}
