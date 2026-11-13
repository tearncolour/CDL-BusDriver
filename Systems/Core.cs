using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Every call into CSL Driving Life Core goes through here. The methods are never inlined, so if the driving mod is not
    /// installed only these calls fail (and are caught); nothing else in the bus mod touches its types.
    /// </summary>
    internal static class Core
    {
        internal const string kOwner = "CDLC.BusDriver";
        private static bool s_Available;
        private static float s_NextCheck;

        internal static bool Available
        {
            get
            {
                if (s_Available)
                    return true;

                float now = Time.realtimeSinceStartup;
                if (now < s_NextCheck)
                    return false;

                s_NextCheck = now + 3f;
                try
                {
                    s_Available = CheckVersion();
                }
                catch (Exception)
                {
                    s_Available = false;
                }

                return s_Available;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool CheckVersion() => DrivingApi.ApiVersion >= 1;

        private static int s_Api = -1;

        /// <summary>The driving mod has the driver's view and body pose API (2, CSL Driving Life Core 0.9.0).</summary>
        internal static bool HasCab
        {
            get
            {
                if (!Available)
                    return false;
                if (s_Api < 0)
                {
                    try { s_Api = ApiVersionNow(); }
                    catch (Exception) { s_Api = 0; }
                }

                return s_Api >= 2;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ApiVersionNow() => DrivingApi.ApiVersion;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool CabViewActive() => DrivingApi.CabViewActive;

        /// <summary>
        /// API 4 (CSL Driving Life Core 0.10.0): the bus driveline's readouts, payload, kneeling, hiding the game's body in every
        /// view, the cab's openness for the sound. Every call below needs it.
        /// </summary>
        internal static bool HasApi4 => HasCab && s_Api >= 4;

        /// <summary>API 6: pad driving; the hints and keys name the pad's buttons while the player uses a pad.</summary>
        internal static bool HasApi6 => HasCab && s_Api >= 6;

        /// <summary>API 7: the driver's indicators and hazard lamps on the game's own body (SetManualSignals).</summary>
        internal static bool HasApi7 => HasCab && s_Api >= 7;

        /// <summary>API 8 (CSL Driving Life Core 0.13.1): PadHapticsActive.</summary>
        internal static bool HasApi8 => HasCab && s_Api >= 8;

        /// <summary>API 9: a temporary owner-scoped cab view without editing the player's saved view preference.</summary>
        internal static bool HasApi9 => HasCab && s_Api >= 9;

        /// <summary>API 10 (CSL Driving Life Core 0.13.27): the driver's cab (eye, look-down, seat, insulation) with SetCab.</summary>
        internal static bool HasApi10 => HasCab && s_Api >= 10;

        /// <summary>API 11 (CSL Driving Life Core 0.13.36): the body a vehicle prefab collides with (SetVehicleBody), without its mirrors.</summary>
        internal static bool HasApi11 => HasCab && s_Api >= 11;

        /// <summary>The driving mod plays the sounds through the pad's actuators now (a DualSense on USB): the engine and road are felt without our motors.</summary>
        internal static bool PadHapticsActive()
        {
            if (!HasApi8)
                return false;
            try { return PadHapticsActiveNow(); }
            catch (Exception) { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PadHapticsActiveNow() => DrivingApi.PadHapticsActive;

        private static int s_SentSignals;

        /// <summary>
        /// The driver's indicators on the game's body of the driven vehicle: 1 left, 2 right, 3 both (hazard lamps), 0 none. Only
        /// sent when it changes, and only with API 7 (an older driving mod has no manual signals: the cluster still flashes).
        /// </summary>
        internal static void SetManualSignals(int signals, bool force = false)
        {
            // force: a vehicle was taken or let go (the driving mod forgets the signals then): send again whatever was sent last.
            if (!HasApi7 || signals == s_SentSignals && !force)
                return;
            try
            {
                SetManualSignalsNow(signals);
                s_SentSignals = signals;
            }
            catch (Exception)
            {
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetManualSignalsNow(int signals) => DrivingApi.SetManualSignals(kOwner, signals);

        /// <summary>The player last used a pad (the game's control scheme). False without the driving mod's API 6.</summary>
        internal static bool PadActive
        {
            get
            {
                if (!HasApi6)
                    return false;
                try { return PadActiveNow(); }
                catch (Exception) { return false; }
            }
        }

        /// <summary>What the pad in hand calls the control at a binding path (A or the cross); "" if unknown or without API 6.</summary>
        internal static string PadName(string controlPath)
        {
            if (!HasApi6)
                return "";
            try { return PadNameNow(controlPath); }
            catch (Exception) { return ""; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PadActiveNow() => DrivingApi.GamepadActive;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string PadNameNow(string controlPath) => DrivingApi.PadLabel(controlPath);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetPayloadMass(float kg) => DrivingApi.SetPayloadMass(kOwner, kg);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetKneel(bool kneel) => DrivingApi.SetKneel(kOwner, kneel);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetBodyHidden(bool hide, string owner = kOwner) => DrivingApi.SetBodyHidden(owner, hide);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetCabOpen(float open) => DrivingApi.SetCabOpen(kOwner, open);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void GetKneelTransform(out Vector3 offset, out Quaternion rotation) => DrivingApi.GetKneelTransform(out offset, out rotation);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Braking() => DrivingApi.Braking;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Reversing() => DrivingApi.Reversing;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool HasDriveline() => DrivingApi.HasDriveline;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Gear() => DrivingApi.Gear;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float RetarderLevel() => DrivingApi.RetarderLevel;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float AirPressureBar() => DrivingApi.AirPressureBar;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float KneelLevel() => DrivingApi.KneelLevel;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool ParkingBrake() => DrivingApi.ParkingBrake;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float SteeringRatio() => DrivingApi.SteeringRatio;

        private static bool s_CursorNeeded;

        /// <summary>The bus panel needs the mouse pointer while driving (CSL Driving Life Core 0.9.3 hides it otherwise).</summary>
        internal static void SetCursorNeeded(bool needed)
        {
            if (needed == s_CursorNeeded || !Available || s_Api < 3 && ApiVersionSafe() < 3)
                return;

            s_CursorNeeded = needed;
            try { CursorNeeded(needed); }
            catch (Exception) { }
        }

        private static int ApiVersionSafe()
        {
            try { return ApiVersionNow(); }
            catch (Exception) { return 0; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CursorNeeded(bool needed) => DrivingApi.SetCursorNeeded(kOwner, needed);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetCabView(bool on) => DrivingApi.CabViewEnabled = on;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool CabViewPreference() => DrivingApi.CabViewEnabled;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetCabViewRequested(string owner, bool on) => DrivingApi.SetCabViewRequested(owner, on);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryGetBodyPose(out Vector3 position, out Quaternion rotation) => DrivingApi.TryGetBodyPose(out position, out rotation);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryGetRenderPose(Entity entity, out Vector3 position, out Quaternion rotation) => DrivingApi.TryGetRenderPose(entity, out position, out rotation);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetCabEye(Vector3 eye, string owner = kOwner) => DrivingApi.SetCabEye(owner, eye);

        /// <summary>
        /// The driver's cab for the driving mod: with API 10 the whole cab (NaN values are left to the driving mod), with an older one
        /// only its eye.
        /// </summary>
        internal static void SetCab(Vector3 eye, float lookDownDeg, float seatSpring, float insulation, string owner = kOwner)
        {
            if (HasApi10)
                SetCabApi10(eye, lookDownDeg, seatSpring, insulation, owner);
            else
                SetCabEye(eye, owner);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetCabApi10(Vector3 eye, float lookDownDeg, float seatSpring, float insulation, string owner) =>
            DrivingApi.SetCab(owner, new DrivingCab { Eye = eye, LookDownDeg = lookDownDeg, SeatSpring = seatSpring, Insulation = insulation });

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ClearCabEye(string owner = kOwner) => DrivingApi.ClearCabEye(owner);

        /// <summary>API 14 (CSL Driving Life Core 0.13.43): several camera positions per vehicle (SetCameras, ActiveCamera).</summary>
        internal static bool HasApi14 => HasCab && s_Api >= 14;

        // The cameras last made for the driving mod: a DrivingCamera[], typed only in the API 14 wrapper (an older Core has no such type,
        // and a field of it would stop this class from loading there).
        private static object s_Cameras;
        private static int s_CamerasKey = -1;

        /// <summary>
        /// The bus's cameras (API 14): the driver's seat (the cab, as <see cref="SetCab"/>) and the profile's others, each on its section
        /// (0 the driven body, 1 the towed rear). The others are made again only when <paramref name="key"/> changes (another bus or
        /// profile); sent every frame like the cab, without allocating. With an older driving mod the cab alone.
        /// </summary>
        internal static void SetCameras(int key, Vector3 eye, float lookDownDeg, float seatSpring, float insulation,
            List<(Interior.Models.VehicleProfile.CameraData Camera, int Section)> others, string owner = kOwner)
        {
            if (!HasApi14)
            {
                SetCab(eye, lookDownDeg, seatSpring, insulation, owner);
                return;
            }

            try { SetCamerasApi14(key, eye, lookDownDeg, seatSpring, insulation, others, owner); }
            catch (Exception) { SetCab(eye, lookDownDeg, seatSpring, insulation, owner); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetCamerasApi14(int key, Vector3 eye, float lookDownDeg, float seatSpring, float insulation,
            List<(Interior.Models.VehicleProfile.CameraData Camera, int Section)> others, string owner)
        {
            var cameras = s_Cameras as DrivingCamera[];
            if (cameras == null || key != s_CamerasKey || cameras.Length != others.Count + 1)
            {
                cameras = new DrivingCamera[others.Count + 1];
                for (int i = 0; i < others.Count; i++)
                {
                    Interior.Models.VehicleProfile.CameraData c = others[i].Camera;
                    // NaN (not said) goes as it is: the driving mod reads a value that is not finite as not given
                    cameras[i + 1] = new DrivingCamera
                    {
                        Name = c.Name, Eye = new Vector3(c.Eye.x, c.Eye.y, c.Eye.z), YawDeg = c.YawDeg, LookDownDeg = c.LookDownDeg, FovDeg = c.FovDeg,
                        SeatSpring = c.SeatSpring, Insulation = c.Insulation, Kind = c.Mounted ? DrivingCameraKind.Mounted : DrivingCameraKind.Seat,
                        Section = others[i].Section,
                    };
                }

                s_Cameras = cameras;
                s_CamerasKey = key;
            }

            cameras[0] = new DrivingCamera
            {
                Name = "driver", Eye = eye, LookDownDeg = lookDownDeg, SeatSpring = seatSpring, Insulation = insulation, Kind = DrivingCameraKind.Seat,
            };
            DrivingApi.SetCameras(owner, cameras);
        }

        /// <summary>The camera the driver's view uses (API 14): 0 the driver's seat, and always 0 with an older driving mod.</summary>
        internal static int ActiveCamera()
        {
            if (!HasApi14)
                return 0;
            try { return ActiveCameraApi14(); }
            catch (Exception) { return 0; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ActiveCameraApi14() => DrivingApi.ActiveCamera;

        /// <summary>Back to the driver's seat (the review probe's driver's view); nothing with an older driving mod (it has only that one).</summary>
        internal static void SelectDriverSeat()
        {
            if (!HasApi14)
                return;
            try { SelectDriverSeatApi14(); }
            catch (Exception) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SelectDriverSeatApi14() => DrivingApi.ActiveCamera = 0;

        /// <summary>The driver's view uses this camera of the bus (API 14); nothing with an older driving mod.</summary>
        internal static void SelectCamera(int index)
        {
            if (!HasApi14)
                return;
            try { SelectCameraApi14(index); }
            catch (Exception) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SelectCameraApi14(int index) => DrivingApi.ActiveCamera = index;

        /// <summary>The camera in use as the player is told it (API 14); "" with an older driving mod or while the vehicle has only one.</summary>
        internal static string ActiveCameraName()
        {
            if (!HasApi14)
                return "";
            try { return ActiveCameraNameApi14(); }
            catch (Exception) { return ""; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ActiveCameraNameApi14() => DrivingApi.ActiveCameraName ?? "";

        /// <summary>
        /// API 17 (CSL Driving Life Core 0.13.54): the player rides a vehicle the game keeps driving (Ride, StopRiding, IsRiding, RiddenVehicle):
        /// the camera in it, its camera positions and body pose as when driving, no control of it. The passenger mode needs it.
        /// </summary>
        internal static bool HasApi17 => HasCab && s_Api >= 17;

        /// <summary>API 18 (CSL Driving Life Core 0.13.55): the world's time scale (TimeScale); SpeedMps and Acceleration are true.</summary>
        internal static bool HasApi18 => HasCab && s_Api >= 18;

        /// <summary>
        /// How many world seconds the driven or ridden vehicle's world runs a real second (API 18: 2 on roads, 1 on rails): its speed per real second is
        /// SpeedMps times this. 1 with an older Core (its SpeedMps is that speed already), or for a figure that is not a positive number.
        /// </summary>
        internal static float TimeScale()
        {
            if (!HasApi18)
                return 1f;
            try
            {
                float scale = TimeScaleApi18();
                return scale > 0f && !float.IsInfinity(scale) ? scale : 1f;
            }
            catch (Exception) { return 1f; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float TimeScaleApi18() => DrivingApi.TimeScale;

        /// <summary>The player rides a vehicle as a passenger (API 17); false with an older driving mod.</summary>
        internal static bool IsRiding()
        {
            if (!HasApi17)
                return false;
            try { return IsRidingApi17(); }
            catch (Exception) { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsRidingApi17() => DrivingApi.IsRiding;

        /// <summary>The vehicle the player rides (API 17); Entity.Null when not riding.</summary>
        internal static Entity RiddenVehicle()
        {
            if (!HasApi17)
                return Entity.Null;
            try { return RiddenVehicleApi17(); }
            catch (Exception) { return Entity.Null; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Entity RiddenVehicleApi17() => DrivingApi.RiddenVehicle;

        /// <summary>Rides this vehicle (API 17); false while driving, with an older driving mod, or when the call fails.</summary>
        internal static bool Ride(Entity vehicle)
        {
            if (!HasApi17)
                return false;
            try { return RideApi17(vehicle); }
            catch (Exception) { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RideApi17(Entity vehicle) => DrivingApi.Ride(vehicle);

        /// <summary>Leaves the vehicle ridden (API 17).</summary>
        internal static void StopRiding()
        {
            if (!HasApi17)
                return;
            try { StopRidingApi17(); }
            catch (Exception) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StopRidingApi17() => DrivingApi.StopRiding();

        /// <summary>
        /// The body a vehicle prefab collides with, for the driving mod (API 11): the pack's own body ends and half width (no mirrors), so
        /// the mirrors' arms do not make the collision box 0.5 m wider than the bus. Nothing with an older driving mod.
        /// </summary>
        internal static void SetVehicleBody(Entity prefab, float3 min, float3 max)
        {
            if (!HasApi11)
                return;
            try { SetVehicleBodyApi11(prefab, min, max); }
            catch (Exception) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SetVehicleBodyApi11(Entity prefab, float3 min, float3 max) =>
            DrivingApi.SetVehicleBody(kOwner, prefab, new Vector3(min.x, min.y, min.z), new Vector3(max.x, max.y, max.z));

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetCabBodyHidden(bool hide, string owner = kOwner) => DrivingApi.SetCabBodyHidden(owner, hide);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float SteeringWheelAngle() => DrivingApi.SteeringWheelAngle;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float EngineRpm() => DrivingApi.EngineRpm;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float ThrottleInput() => DrivingApi.ThrottleInput;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float BrakeInput() => DrivingApi.BrakeInput;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Vector3 Acceleration() => DrivingApi.Acceleration;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Shifting() => DrivingApi.Shifting;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IsDriving() => DrivingApi.IsDriving;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Entity Vehicle() => DrivingApi.Vehicle;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float SpeedMps() => DrivingApi.SpeedMps;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Drive(Entity vehicle) => DrivingApi.Drive(vehicle);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Release() => DrivingApi.Release();

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetParkingBrake(bool engaged) => DrivingApi.SetParkingBrake(kOwner, engaged);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetHoldStopFlags(bool hold) => DrivingApi.SetHoldStopFlags(kOwner, hold);

        /// <summary>
        /// Rows: (keys, pad control paths, label key, English fallback, accent). The pad control paths ("&lt;Gamepad&gt;/buttonWest",
        /// several separated by " / ", "" = not on a pad) are what the row shows while the player uses a pad; the driving mod names
        /// them for the pad in hand (API 6; older ones show the keys only). Null clears the bus hints.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetKeyHints(IList<(string keys, string padControls, string label, string fallback, bool accent)> rows)
        {
            if (rows == null)
            {
                DrivingApi.SetKeyHints(kOwner, null);
                return;
            }

            bool pad = HasApi6;
            var hints = new List<DrivingKeyHint>(rows.Count);
            foreach ((string keys, string padControls, string label, string fallback, bool accent) in rows)
            {
                var hint = new DrivingKeyHint
                {
                    Keys = keys,
                    Label = label,
                    Fallback = fallback,
                    Group = BusLocale.kPrefix + "Hint.Group",
                    GroupFallback = "Bus",
                    Accent = accent
                };
                hints.Add(pad ? WithPad(hint, padControls) : hint);
            }

            DrivingApi.SetKeyHints(kOwner, hints);
        }

        // The field exists from API 6 on: only this method touches it, and it only runs when the driving mod has it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static DrivingKeyHint WithPad(DrivingKeyHint hint, string padControls)
        {
            hint.PadControls = padControls;
            return hint;
        }

        /// <summary>Releases everything the bus mod holds in the driving mod (safe to call when it is missing).</summary>
        internal static void ClearAll()
        {
            if (!Available)
                return;

            try
            {
                SetParkingBrake(false);
                SetHoldStopFlags(false);
                SetKeyHints(null);
                SetManualSignals(0);
            }
            catch (Exception)
            {
            }
        }
    }
}
