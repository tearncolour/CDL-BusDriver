using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>Native activity points from explicit section-local thresholds. Does not alter v1 buses.</summary>
    internal static class VehicleBoarding
    {
        internal readonly struct Point
        {
            internal readonly int Door;
            internal readonly bool Enter;
            internal readonly float3 Position;
            internal readonly quaternion Rotation;
            internal Point(int door, bool enter, float3 position, quaternion rotation)
            { Door = door; Enter = enter; Position = position; Rotation = rotation; }
        }

        internal static Point[] Points(VehicleProfile profile)
        {
            if (profile.NativeDoors == null) return Array.Empty<Point>();
            var result = new List<Point>();
            for (int i = 0; i < profile.NativeDoors.Length; i++)
            {
                var door = profile.NativeDoors[i];
                if (door.CanBoard) result.Add(new Point(i, true, door.Position, door.Rotation));
                if (door.CanAlight) result.Add(new Point(i, false, door.Position,
                    math.mul(quaternion.RotateY(math.PI), door.Rotation)));
            }
            return result.ToArray();
        }
    }
}
