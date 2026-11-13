#if CDLC_AUTHORING
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using CDLC.Asset.Articulation;
using CDLC.BusDriver.Interior.Models;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using ObjectTransform = Game.Objects.Transform;

namespace CDLC.BusDriver
{
    internal static partial class BusVehicleRoadProbe
    {
        static async Task<Entity> WaitStationaryBus(SystemBase host, Entity front, Entity frontPrefab, Entity rearPrefab,
            VehicleAssembly assembly, Camera camera, CameraUpdateSystem cameras, Dictionary<string, object> report, Action check, Action run)
        {
            EntityManager em = host.EntityManager;
            // No rear prefab: a single-section bus, whose native layout is its front alone (or absent).
            bool single = rearPrefab == Entity.Null;
            var setup = new Dictionary<string, object> { ["road_travel_tested"] = false, ["single_section"] = single,
                ["donor_route_usage"] = "Native spawn placement only; no travel, turning or lane-following PASS is inferred",
                ["expected_front_prefab"] = frontPrefab.ToString(), ["expected_rear_prefab"] = rearPrefab.ToString() };
            report["stationary_setup"] = setup;
            var simulation = host.World.GetExistingSystemManaged<SimulationSystem>();
            var trail = new List<object>(); setup["trail"] = trail;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int stable = 0, dropped = 0, cameraPlacedFrame = -1;
            double nextSiteCheck = 0;
            bool siteReady = false, frozen = false, completed = false;
            float speedBeforeFreeze = 0;
            float3 checkedPosition = new float3(float.PositiveInfinity);
            Exception failure = null;
            try
            {
                while (true)
                {
                    check(); if (!frozen) run(); em.CompleteAllTrackedJobs();
                    Entity rear = Entity.Null;
                    bool frontExists = em.Exists(front), layoutMatches = frontExists && single;
                    int layoutLength = 0;
                    if (frontExists && em.HasBuffer<LayoutElement>(front))
                    {
                        var layout = em.GetBuffer<LayoutElement>(front); layoutLength = layout.Length;
                        layoutMatches = single ? layoutLength == 0 || (layoutLength == 1 && layout[0].m_Vehicle == front)
                            : layoutLength == 2 && layout[0].m_Vehicle == front;
                        if (layoutMatches && !single) rear = layout[1].m_Vehicle;
                    }
                    bool rearExists = rear != front && rear != Entity.Null && em.Exists(rear);
                    Entity actualFrontPrefab = frontExists && em.HasComponent<PrefabRef>(front) ? em.GetComponentData<PrefabRef>(front).m_Prefab : Entity.Null;
                    Entity actualRearPrefab = rearExists && em.HasComponent<PrefabRef>(rear) ? em.GetComponentData<PrefabRef>(rear).m_Prefab : Entity.Null;
                    int frontFrames = frontExists && em.HasBuffer<TransformFrame>(front) ? em.GetBuffer<TransformFrame>(front).Length : 0;
                    int rearFrames = rearExists && em.HasBuffer<TransformFrame>(rear) ? em.GetBuffer<TransformFrame>(rear).Length : 0;
                    var predicates = new Dictionary<string, bool> {
                        [single ? "layout_single" : "layout_pair"] = layoutMatches, ["front_exists"] = frontExists,
                        ["front_not_deleted"] = frontExists && !em.HasComponent<Deleted>(front),
                        ["front_prefab"] = actualFrontPrefab == frontPrefab,
                        ["front_transform"] = frontExists && em.HasComponent<ObjectTransform>(front),
                        ["front_update_frame"] = frontExists && em.HasComponent<UpdateFrame>(front),
                        ["front_four_frames"] = frontFrames >= 4
                    };
                    if (!single)
                    {
                        predicates["rear_exists"] = rearExists;
                        predicates["rear_not_deleted"] = rearExists && !em.HasComponent<Deleted>(rear);
                        predicates["rear_prefab"] = actualRearPrefab == rearPrefab;
                        predicates["rear_controller"] = rearExists && em.HasComponent<Controller>(rear) && em.GetComponentData<Controller>(rear).m_Controller == front;
                        predicates["rear_transform"] = rearExists && em.HasComponent<ObjectTransform>(rear);
                        predicates["rear_update_frame"] = rearExists && em.HasComponent<UpdateFrame>(rear);
                        predicates["rear_four_frames"] = rearFrames >= 4;
                    }
                    bool structureReady = true;
                    foreach (bool value in predicates.Values) structureReady &= value;
                    if (structureReady && !frozen)
                    {
                        // The fixture is stationary; stop normal travel before waiting for renderer/camera history.
                        speedBeforeFreeze = simulation.selectedSpeed;
                        simulation.selectedSpeed = 0; frozen = true;
                        setup["freeze"] = new { unity_frame = Time.frameCount, simulation_frame = simulation.frameIndex, previous_speed = speedBeforeFreeze };
                    }
                    bool frontInterpolated = frontExists && em.HasComponent<InterpolatedTransform>(front);
                    bool rearInterpolated = single ? frontInterpolated : rearExists && em.HasComponent<InterpolatedTransform>(rear);
                    predicates["front_interpolated"] = frontInterpolated;
                    if (!single) predicates["rear_interpolated"] = rearInterpolated;
                    bool finite = false;
                    if (structureReady && frontInterpolated && rearInterpolated)
                    {
                        var f = em.GetComponentData<InterpolatedTransform>(front);
                        var r = single ? f : em.GetComponentData<InterpolatedTransform>(rear);
                        finite = ValidStationaryPose(f) && ValidStationaryPose(r);
                        if (finite)
                        {
                            Vector3 before = camera.transform.position; Quaternion rotationBefore = camera.transform.rotation;
                            PlaceCamera(camera, new ObjectTransform(f.m_Position, f.m_Rotation), new ObjectTransform(r.m_Position, r.m_Rotation));
                            if (cameraPlacedFrame < 0 || Vector3.Distance(before, camera.transform.position) > .01f || Quaternion.Angle(rotationBefore, camera.transform.rotation) > .01f)
                                cameraPlacedFrame = Time.frameCount;
                            float3 point = em.GetComponentData<ObjectTransform>(front).m_Position;
                            if (watch.Elapsed.TotalSeconds >= nextSiteCheck || math.distancesq(point, checkedPosition) > 100f)
                            {
                                siteReady = RoadGlassLights.CanPrepare(host, point, out object site);
                                setup["road_light_preflight"] = site; checkedPosition = point; nextSiteCheck = watch.Elapsed.TotalSeconds + 1;
                            }
                        }
                    }
                    predicates["finite_interpolated_poses"] = finite;
                    predicates["simulation_paused"] = frozen && simulation.selectedSpeed == 0;
                    predicates["owned_active_camera"] = cameras.activeCamera == camera && cameras.activeViewer?.camera == camera;
                    predicates["camera_render_update"] = cameraPlacedFrame >= 0 && Time.frameCount > cameraPlacedFrame;
                    predicates["camera_cache_settled"] = predicates["owned_active_camera"] &&
                        math.distance(cameras.position, (float3)camera.transform.position) <= .01f &&
                        math.distance(cameras.direction, (float3)camera.transform.forward) <= .001f;
                    if (!single) predicates["production_joint_visible"] = host.World.GetExistingSystemManaged<ArticulationSystem>()?.HasVisibleJoint(front) == true;
                    predicates["road_lighting_supported"] = siteReady;
                    var failed = new List<string>(); foreach (var predicate in predicates) if (!predicate.Value) failed.Add(predicate.Key);
                    stable = failed.Count == 0 ? stable + 1 : 0;
                    setup["predicates"] = predicates; setup["stable_checks"] = stable;
                    setup["actual_front_prefab"] = actualFrontPrefab.ToString(); setup["actual_rear_prefab"] = actualRearPrefab.ToString();
                    if (!single) setup["last_state"] = NativeJointProbeState(host, front, rear, assembly, cameras);
                    setup["elapsed_seconds"] = watch.Elapsed.TotalSeconds;
                    if (trail.Count == 64) { trail.RemoveAt(1); dropped++; }
                    trail.Add(new { seconds = watch.Elapsed.TotalSeconds, unity_frame = Time.frameCount, simulation_frame = simulation.frameIndex,
                        selected_speed = simulation.selectedSpeed, structure_ready = structureReady, frozen, stable, failed_predicates = failed.ToArray(),
                        layout_length = layoutLength, front_frames = frontFrames, rear_frames = rearFrames, camera_placed_frame = cameraPlacedFrame,
                        camera_cache_gap_m = math.distance(cameras.position, (float3)camera.transform.position),
                        camera_cache_forward_delta = math.distance(cameras.direction, (float3)camera.transform.forward) });
                    setup["dropped_trail_entries"] = dropped;
                    if (stable >= 2)
                    { completed = true; setup["ready"] = true; setup["pause_handoff"] = "Owned paused World handed to the following glass cycle"; return rear; }
                    if (watch.Elapsed.TotalSeconds > 30)
                    { setup["ready"] = false; throw new InvalidOperationException("Stationary bus readiness timed out: " + stable + "/2 consecutive checks; failed: " + string.Join(", ", failed)); }
                    await Task.Delay(100);
                }
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                if (frozen && !completed)
                    try
                    {
                        check();
                        if (simulation.selectedSpeed == 0)
                        { simulation.selectedSpeed = speedBeforeFreeze; setup["pause_cleanup"] = "Restored owned World's prior speed"; }
                        else setup["pause_cleanup"] = "Another owner changed speed; its value was retained";
                    }
                    catch (OperationCanceledException) { setup["pause_cleanup"] = "World ownership lost; no speed was restored"; }
                    catch (Exception cleanup)
                    {
                        setup["pause_cleanup_error"] = cleanup.ToString();
                        throw failure == null ? cleanup : new AggregateException("Stationary fixture and speed cleanup failed", failure, cleanup);
                    }
            }
        }

        static bool ValidStationaryPose(InterpolatedTransform value) => math.all(math.isfinite(value.m_Position)) &&
            math.all(math.isfinite(value.m_Rotation.value)) && math.abs(math.lengthsq(value.m_Rotation.value) - 1f) < .01f;

        static object NativeJointProbeState(SystemBase host, Entity front, Entity rear, VehicleAssembly assembly, CameraUpdateSystem cameras)
        {
            try
            {
                var rendering = host.World.GetExistingSystemManaged<RenderingSystem>();
                var joints = host.World.GetExistingSystemManaged<ArticulationSystem>();
                int vertices = 0;
                bool visible = joints != null && joints.HasVisibleJoint(front, out vertices);
                var em = host.EntityManager;
                var frontState = NativeProbeEntity(em, front, rendering, null, default, default, out Vector3 fp, out Quaternion fr, out bool fv);
                Hitch? hitch = em.HasComponent<PrefabRef>(front) && em.HasComponent<PrefabRef>(rear) &&
                    ArticulationSystem.TryNativeHitch(em, em.GetComponentData<PrefabRef>(front).m_Prefab, em.GetComponentData<PrefabRef>(rear).m_Prefab,
                        out Hitch h) ? h : (Hitch?)null;
                var rearState = NativeProbeEntity(em, rear, rendering, fv ? hitch : null, fp, fr, out Vector3 rp, out Quaternion rr, out bool rv);
                Camera active = cameras?.activeCamera;
                return new { unity_frame = Time.frameCount, simulation_frame = host.World.GetExistingSystemManaged<SimulationSystem>()?.frameIndex,
                    rendering_frame = rendering?.frameIndex, rendering_time = rendering?.frameTime,
                    camera = active == null ? null : new { name = active.name, id = active.GetInstanceID(), position = A((float3)active.transform.position),
                        rotation = A((float4)new float4(active.transform.rotation.x, active.transform.rotation.y, active.transform.rotation.z, active.transform.rotation.w)),
                        active.enabled, active.fieldOfView, active.pixelHeight, active.nearClipPlane, active.farClipPlane },
                    active_viewer = cameras?.activeViewer == null ? null : new { camera_id = cameras.activeViewer.camera.GetInstanceID(),
                        position = A(cameras.activeViewer.position), forward = A(cameras.activeViewer.forward) },
                    cached_viewer_position = cameras == null ? null : A(cameras.position),
                    cached_viewer_forward = cameras == null ? null : A(cameras.direction),
                    front = frontState, rear = rearState,
                    reconstructed_joint_anchor_gap_m = fv && rv ? (float?)Vector3.Distance(fp + fr * V(assembly.FrontAnchor), rp + rr * V(assembly.RearAnchor)) : null,
                    pose_scope = "Joint input as CDL Asset's ArticulationSystem: native TransformFrame, the rear hung on the front's free-trailer hitch, then Swaying; native body InterpolatedTransform recorded separately",
                    rear_hung_on_hitch = fv && hitch.HasValue,
                    maximum_anchor_gap_m = assembly.MaxSeparation,
                    joint = new { visible, vertices, entry = JointEntryEvidence(joints, front) } };
            }
            catch (Exception error) { return new { diagnostic_error = error.ToString(), front = front.ToString(), rear = rear.ToString() }; }
        }

        static object NativeProbeEntity(EntityManager em, Entity entity, RenderingSystem rendering, Hitch? towedBy,
            Vector3 frontPosition, Quaternion frontRotation, out Vector3 position, out Quaternion rotation, out bool valid)
        {
            position = default; rotation = Quaternion.identity; valid = false;
            if (entity == Entity.Null || !em.Exists(entity)) return new { entity = entity.ToString(), exists = false };
            var result = new Dictionary<string, object> { ["entity"] = entity.ToString(), ["exists"] = true,
                ["deleted"] = em.HasComponent<Deleted>(entity), ["prefab"] = em.HasComponent<PrefabRef>(entity) ? em.GetComponentData<PrefabRef>(entity).m_Prefab.ToString() : null,
                ["controller"] = em.HasComponent<Controller>(entity) ? em.GetComponentData<Controller>(entity).m_Controller.ToString() : null };
            if (em.HasComponent<ObjectTransform>(entity))
            {
                var value = em.GetComponentData<ObjectTransform>(entity);
                result["simulation_pose"] = new { position = A(value.m_Position), rotation = A(value.m_Rotation.value) };
            }
            if (em.HasComponent<InterpolatedTransform>(entity))
            {
                var value = em.GetComponentData<InterpolatedTransform>(entity);
                result["native_interpolated_pose"] = new { position = A(value.m_Position), rotation = A(value.m_Rotation.value) };
            }
            if (!em.HasBuffer<TransformFrame>(entity)) return result;
            var frames = em.GetBuffer<TransformFrame>(entity);
            result["transform_frames"] = frames.Length;
            if (frames.Length < 4 || !em.HasComponent<UpdateFrame>(entity) || rendering == null) return result;
            uint update = em.GetSharedComponent<UpdateFrame>(entity).m_Index;
            ObjectInterpolateSystem.CalculateUpdateFrames(rendering.frameIndex, rendering.frameTime, update, out uint a, out uint b, out float t);
            var pose = ObjectInterpolateSystem.CalculateTransform(frames[(int)a], frames[(int)b], t);
            if (towedBy.HasValue) towedBy.Value.Hang(frontPosition, frontRotation, ref pose.m_Position, ref pose.m_Rotation);
            position = pose.m_Position; rotation = pose.m_Rotation;
            if (em.HasComponent<Swaying>(entity))
            {
                var sway = em.GetComponentData<Swaying>(entity);
                float2 lean = sway.m_SwayPosition.xz; float angle = math.length(lean);
                if (angle > 1e-5f && math.isfinite(angle)) { lean /= angle; var q = quaternion.AxisAngle(new float3(-lean.y, 0, lean.x), angle); rotation *= new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w); }
                if (math.isfinite(sway.m_SwayPosition.y)) position += rotation * new Vector3(0, sway.m_SwayPosition.y, 0);
            }
            valid = math.all(math.isfinite((float3)position)) && math.all(math.isfinite(new float4(rotation.x, rotation.y, rotation.z, rotation.w))) && Mathf.Abs(Quaternion.Dot(rotation, rotation) - 1) < .01f;
            result["reconstructed_joint_input"] = new { update_frame = update, from = a, to = b, interpolation = t,
                position = A((float3)position), rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w }, valid };
            return result;
        }

        static object JointEntryEvidence(ArticulationSystem system, Entity front)
        {
            if (system == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var entries = (IDictionary)typeof(ArticulationSystem).GetField("m_Entries", flags).GetValue(system);
            var claims = (HashSet<Entity>)typeof(ArticulationSystem).GetField("m_Claims", flags).GetValue(system);
            object entry = entries[front];
            var result = new Dictionary<string, object> { ["present"] = entry != null, ["cab_claim"] = claims.Contains(front) };
            if (entry == null) return result;
            foreach (string field in new[] { "Active", "Lod", "Tier", "HiddenAt", "Rear" })
                result[field] = entry.GetType().GetField(field, flags).GetValue(entry).ToString();
            return result;
        }
    }
}
#endif
