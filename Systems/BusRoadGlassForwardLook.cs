#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.Reflection;
using CDLC.BusDriver.Interior;
using Game.Input;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CDLC.BusDriver
{
    internal sealed class BusRoadGlassForwardLook : IDisposable
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly CDLC.DirectDriveCameraSystem system;
        readonly Entity vehicle;
        readonly Func<bool> ownsWorld;
        readonly IDictionary<string, object> report;
        readonly FieldInfo yaw, pitch, rotate, zoom, target;
        readonly float originalYaw, originalPitch;
        readonly object originalRotate, originalZoom;
        InputAction input;
        ProxyAction suppressed;
        bool installed, cleanupStarted, restoreOriginalAngles, anglesRestored, disposed;

        internal BusRoadGlassForwardLook(SystemBase host, Entity vehicle, Func<bool> ownsWorld, IDictionary<string, object> report)
        {
            this.vehicle = vehicle;
            this.ownsWorld = ownsWorld;
            this.report = report;
            system = host.World.GetExistingSystemManaged<CDLC.DirectDriveCameraSystem>();
            Require(system != null && system.GetType() == typeof(CDLC.DirectDriveCameraSystem), "Core camera system is unavailable");
            Type type = system.GetType();
            yaw = Field(type, "m_LookYaw", typeof(float)); pitch = Field(type, "m_LookPitch", typeof(float));
            rotate = Field(type, "m_RotateAction", typeof(ProxyAction)); zoom = Field(type, "m_ZoomAction", typeof(ProxyAction));
            target = Field(type, "m_Target", typeof(Entity));
            Require(OwnedVehicle(), "Core camera is not following the owned glass fixture");
            originalYaw = (float)yaw.GetValue(system); originalPitch = (float)pitch.GetValue(system);
            originalRotate = rotate.GetValue(system); originalZoom = zoom.GetValue(system);
            report["core_mvid"] = type.Assembly.ManifestModule.ModuleVersionId;
            report["look_before"] = new[] { originalYaw, originalPitch };
            report["scope"] = "Only this Core camera instance uses an unregistered disabled action copy; shared input actions are unchanged";
            try
            {
                ProxyAction source = (ProxyAction)originalRotate ?? InputManager.instance?.FindAction(InputManager.kCameraMap, "Rotate");
                Require(source != null && source.GetType() == typeof(ProxyAction), "Core Rotate action cannot be copied safely");
                FieldInfo sourceAction = Field(typeof(ProxyAction), "m_SourceAction", typeof(InputAction));
                InputAction shared = (InputAction)sourceAction.GetValue(source);
                Require(shared != null, "Core Rotate action has no source");
                bool sharedEnabled = shared.enabled;
                input = new InputAction("CDLC glass review forward look");
                suppressed = (ProxyAction)typeof(object).GetMethod("MemberwiseClone", Fields).Invoke(source, null);
                sourceAction.SetValue(suppressed, input);
                Require(ReferenceEquals(sourceAction.GetValue(source), shared) && shared.enabled == sharedEnabled,
                    "Shared Rotate source changed while making the private copy");
                Require(!input.enabled && !suppressed.enabled && suppressed.ReadValue<Vector2>() == Vector2.zero &&
                    suppressed.ReadValue<float>() == 0f, "Private camera input is not disabled and neutral");
                // Null would let UpdateFreeLook reacquire the shared game actions on its next update.
                installed = true;
                rotate.SetValue(system, suppressed); zoom.SetValue(system, suppressed);
                yaw.SetValue(system, 0f); pitch.SetValue(system, 0f);
                Verify();
            }
            catch (Exception error)
            {
                try { Dispose(); }
                catch (Exception cleanup)
                {
                    CabCleanup.Retain(this);
                    throw new AggregateException("Forward-look setup and cleanup failed", error, cleanup);
                }
                throw;
            }
        }

        internal void Verify()
        {
            Require(!disposed && OwnedVehicle(), "Forward-look fixture lost its World or vehicle");
            Require(ReferenceEquals(rotate.GetValue(system), suppressed) && ReferenceEquals(zoom.GetValue(system), suppressed),
                "Core camera input reference changed during glass capture");
            float currentYaw = (float)yaw.GetValue(system), currentPitch = (float)pitch.GetValue(system);
            report["look_current"] = new[] { currentYaw, currentPitch };
            Require(currentYaw == 0f && currentPitch == 0f && !suppressed.enabled && suppressed.ReadValue<Vector2>() == Vector2.zero,
                "Core forward-look request is no longer neutral");
        }

        bool OwnedVehicle() => ownsWorld() && Core.IsDriving() && Core.Vehicle() == vehicle && (Entity)target.GetValue(system) == vehicle;

        public void Dispose()
        {
            if (disposed) return;
            var failures = new List<Exception>();
            bool restoreAngles = false;
            if (installed)
            {
                CabCleanup.Try(() => {
                    if (!cleanupStarted)
                    {
                        restoreOriginalAngles = ReferenceEquals(rotate.GetValue(system), suppressed) && ReferenceEquals(zoom.GetValue(system), suppressed);
                        cleanupStarted = true;
                    }
                    restoreAngles = restoreOriginalAngles && OwnedVehicle();
                }, failures);
                CabCleanup.Try(() => { if (ReferenceEquals(rotate.GetValue(system), suppressed)) rotate.SetValue(system, originalRotate); }, failures);
                CabCleanup.Try(() => { if (ReferenceEquals(zoom.GetValue(system), suppressed)) zoom.SetValue(system, originalZoom); }, failures);
                if (!anglesRestored && restoreAngles)
                {
                    bool a = CabCleanup.Try(() => yaw.SetValue(system, originalYaw), failures);
                    bool b = CabCleanup.Try(() => pitch.SetValue(system, originalPitch), failures);
                    anglesRestored = a && b;
                }
                bool referenced = true;
                CabCleanup.Try(() => referenced = ReferenceEquals(rotate.GetValue(system), suppressed) || ReferenceEquals(zoom.GetValue(system), suppressed), failures);
                if (referenced) failures.Add(new InvalidOperationException("Core camera still references the private input; disposal retained"));
                else if (input != null) CabCleanup.Try(() => { input.Dispose(); input = null; }, failures);
            }
            else if (input != null) CabCleanup.Try(() => { input.Dispose(); input = null; }, failures);
            report["cleanup"] = new { original_angles_restored = anglesRestored, original_vehicle_still_owned = restoreAngles,
                errors = failures.ConvertAll(e => e.ToString()).ToArray() };
            if (failures.Count > 0) throw new AggregateException("Forward-look cleanup failed", failures);
            disposed = true;
        }

        static FieldInfo Field(Type type, string name, Type expected)
        {
            FieldInfo field = type.GetField(name, Fields);
            Require(field != null && field.DeclaringType == type && field.FieldType == expected, "Unexpected Core camera field: " + name);
            return field;
        }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
#endif
