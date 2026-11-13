#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Colossal.Mathematics;
using Game.Common;
using Game.Effects;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using ObjectTransform = Game.Objects.Transform;
using RoadFlags = Game.Net.RoadFlags;
using SubObject = Game.Objects.SubObject;

namespace CDLC.BusDriver
{
    internal static partial class BusVehicleRoadProbe
    {
        struct OwnedRoadGlassLight : IComponentData { public int Token; }

        sealed class RoadGlassLights
        {
            readonly SystemBase host;
            readonly Action check;
            readonly Func<bool> ownsWorld;
            readonly List<Entity> lights = new List<Entity>();
            readonly HashSet<Entity> initializing = new HashSet<Entity>();
            readonly List<object> observations = new List<object>();
            readonly Dictionary<string, object> report;
            readonly int token = Environment.TickCount;
            Entity road;
            RoadFlags addedFlags;
            bool flagsApplied;

            sealed class Site
            {
                internal Entity Road;
                internal float Distance;
                internal ObjectTransform[] Placements;
                internal object Evidence;
            }

            internal RoadGlassLights(SystemBase host, Action check, Func<bool> ownsWorld, Dictionary<string, object> report)
            { this.host = host; this.check = check; this.ownsWorld = ownsWorld; this.report = report; }

            internal void Create(float3 point)
            {
                check();
                EntityManager em = host.EntityManager;
                em.CompleteAllTrackedJobs();
                var prefabs = host.World.GetExistingSystemManaged<PrefabSystem>();
                bool found = TryFindSite(host, point, out Site site, out object placementEvidence);
                report["placement_search"] = placementEvidence;
                Require(found, "No ground-supported roadside light pair is available within 80 m at the bus's road elevation");

                using var prefabQuery = em.CreateEntityQuery(ComponentType.ReadOnly<StreetLightData>(), ComponentType.ReadOnly<ObjectData>(),
                    ComponentType.ReadOnly<PrefabData>(), ComponentType.Exclude<Deleted>());
                using var candidates = prefabQuery.ToEntityArray(Allocator.Temp);
                Entity lightPrefab = Entity.Null;
                PrefabBase lightAsset = null;
                foreach (Entity candidate in candidates)
                {
                    if (em.GetComponentData<StreetLightData>(candidate).m_Layer != StreetLightLayer.Default ||
                        !em.GetComponentData<ObjectData>(candidate).m_Archetype.Valid || !em.HasBuffer<Effect>(candidate) ||
                        !prefabs.TryGetPrefab(candidate, out PrefabBase asset) || !(asset is StaticObjectPrefab) || !asset.isBuiltin) continue;
                    bool hasLight = false;
                    foreach (Effect effect in em.GetBuffer<Effect>(candidate))
                        if (effect.m_Intensity > 0 && em.HasComponent<LightEffectData>(effect.m_Effect) &&
                            em.GetComponentData<LightEffectData>(effect.m_Effect).m_Range > 0) hasLight = true;
                    if (!hasLight || (lightAsset != null && string.CompareOrdinal(lightAsset.name, asset.name) <= 0)) continue;
                    lightPrefab = candidate; lightAsset = asset;
                }
                Require(lightPrefab != Entity.Null, "No built-in road StreetLight prefab with a native light effect is available");
                check();
                road = site.Road;
                ObjectTransform[] placements = site.Placements;
                Road before = em.GetComponentData<Road>(road);
                addedFlags = RoadFlags.IsLit & ~before.m_Flags;
                report["scope"] = "Test-owned built-in roadside poles on an existing road; native StreetLightSystem and effect buffers; no Unity Light or prefab mutation.";
                report["road"] = road.ToString();
                report["road_prefab"] = em.GetComponentData<PrefabRef>(road).m_Prefab.ToString();
                report["road_flags_before"] = before.m_Flags.ToString();
                report["added_road_flags"] = addedFlags.ToString();
                report["prefab"] = new { entity = lightPrefab.ToString(), name = lightAsset.name, asset = lightAsset.asset?.path,
                    id = lightAsset.GetPrefabID().ToString() };
                report["observations"] = observations;
                Road withLights = before; withLights.m_Flags |= addedFlags;
                if (addedFlags != 0)
                {
                    em.SetComponentData(road, withLights);
                    flagsApplied = true;
                }
                using var created = new NativeArray<Entity>(1, Allocator.Temp);
                foreach (ObjectTransform placement in placements)
                {
                    em.CreateEntity(em.GetComponentData<ObjectData>(lightPrefab).m_Archetype, created);
                    Entity light = created[0];
                    lights.Add(light);
                    initializing.Add(light);
                    em.AddComponentData(light, new OwnedRoadGlassLight { Token = token });
                    initializing.Remove(light);
                    em.SetComponentData(light, new PrefabRef(lightPrefab));
                    em.SetComponentData(light, placement);
                    em.AddComponentData(light, new Owner(road));
                    em.SetComponentData(light, new PseudoRandomSeed((ushort)(token ^ light.Index)));
                    if (em.HasBuffer<CustomMeshColor>(light)) em.SetComponentEnabled<CustomMeshColor>(light, false);
                    StreetLight state = default;
                    StreetLightSystem.UpdateStreetLightState(ref state, withLights);
                    em.SetComponentData(light, state);
                }
                report["entities"] = lights.Select((e, i) => new { entity = e.ToString(), position = A(placements[i].m_Position),
                    rotation = new[] { placements[i].m_Rotation.value.x, placements[i].m_Rotation.value.y,
                        placements[i].m_Rotation.value.z, placements[i].m_Rotation.value.w } }).ToArray();
            }

            internal static bool CanPrepare(SystemBase host, float3 point, out object evidence)
            {
                EntityManager em = host.EntityManager;
                using (var query = em.CreateEntityQuery(new EntityQueryDesc {
                    All = new[] { ComponentType.ReadOnly<StreetLight>(), ComponentType.ReadOnly<ObjectTransform>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
                }))
                using (var existing = query.ToEntityArray(Allocator.Temp))
                    foreach (Entity entity in existing)
                        if (math.distancesq(em.GetComponentData<ObjectTransform>(entity).m_Position, point) <= 100f * 100f)
                        { evidence = new { existing_light = entity.ToString(), fixture_needed = false }; return true; }
                return TryFindSite(host, point, out _, out evidence);
            }

            static bool TryFindSite(SystemBase host, float3 point, out Site site, out object evidence)
            {
                EntityManager em = host.EntityManager;
                var terrain = host.World.GetExistingSystemManaged<TerrainSystem>();
                Require(terrain != null, "Road fixture needs the actual terrain height field");
                var height = terrain.GetHeightData(true);
                Func<float3, float> sampleHeight = p => TerrainUtils.SampleHeight(ref height, p);
                using var query = em.CreateEntityQuery(new EntityQueryDesc {
                    All = new[] { ComponentType.ReadOnly<Road>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<Composition>(),
                        ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<SubObject>(), ComponentType.ReadOnly<UpdateFrame>(),
                        ComponentType.ReadOnly<PseudoRandomSeed>() },
                    None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
                });
                using var roads = query.ToEntityArray(Allocator.Temp);
                site = null;
                int nearby = 0, tested = 0;
                var rejected = new List<object>();
                foreach (Entity candidate in roads)
                {
                    Curve curve = em.GetComponentData<Curve>(candidate);
                    float distance = MathUtils.DistanceSquared(curve.m_Bezier, point, out float nearestT);
                    if (!math.isfinite(distance) || distance > 80f * 80f) continue;
                    nearby++;
                    Entity composition = em.GetComponentData<Composition>(candidate).m_Edge;
                    if (!em.HasComponent<NetCompositionData>(composition)) continue;
                    float width = em.GetComponentData<NetCompositionData>(composition).m_Width;
                    if (!math.isfinite(width) || width < 4 || width > 60 || curve.m_Length < 12) continue;
                    foreach (float shift in new[] { 0f, -12f, 12f, -24f, 24f })
                    foreach (float side in new[] { 1f, -1f })
                    {
                        tested++;
                        float t = math.clamp(nearestT + shift / curve.m_Length, .03f, .97f);
                        if (!TryLightPair(curve, width, t, side, point, sampleHeight, out ObjectTransform[] positions, out string reason))
                        {
                            if (rejected.Count < 24) rejected.Add(new { road = candidate.ToString(), distance_m = math.sqrt(distance), t, side, reason });
                            continue;
                        }
                        float score = math.max(math.distancesq(positions[0].m_Position, point), math.distancesq(positions[1].m_Position, point));
                        if (site != null && score >= site.Distance) continue;
                        site = new Site { Road = candidate, Distance = score, Placements = positions,
                            Evidence = new { road = candidate.ToString(), width, t, side, farthest_light_m = math.sqrt(score),
                                positions = positions.Select(p => A(p.m_Position)).ToArray() } };
                    }
                }
                evidence = new { nearby_roads = nearby, tested_pairs = tested, selected = site?.Evidence, rejected,
                    elevation_tolerance_m = 2.5f, pole_spacing_min_m = 5f, side_support_radius_m = .35f,
                    scope = "Read-only actual road/terrain search; no entities, flags or prefabs changed" };
                return site != null;
            }

            internal static bool TryLightPair(Curve curve, float width, float t, float side, float3 point,
                Func<float3, float> height, out ObjectTransform[] positions, out string reason)
            {
                positions = new ObjectTransform[2]; reason = null;
                for (int i = 0; i < 2; i++)
                {
                    float at = math.clamp(t + (i == 0 ? -12f : 12f) / curve.m_Length, .03f, .97f);
                    float3 center = MathUtils.Position(curve.m_Bezier, at);
                    float3 tangent = MathUtils.Tangent(curve.m_Bezier, at); tangent.y = 0;
                    if (math.lengthsq(tangent) <= .001f) { reason = "degenerate road tangent"; return false; }
                    tangent = math.normalize(tangent);
                    float3 position = center + math.cross(math.up(), tangent) * ((width * .5f + .75f) * side);
                    float ground = height(position);
                    if (!math.isfinite(ground) || math.abs(center.y - ground) > 2.5f || math.abs(center.y - point.y) > 2.5f)
                    { reason = $"unsupported elevation: road={center.y}, ground={ground}, bus={point.y}"; return false; }
                    foreach (float3 offset in new[] { new float3(.35f, 0, 0), new float3(-.35f, 0, 0), new float3(0, 0, .35f), new float3(0, 0, -.35f) })
                    {
                        float support = height(position + offset);
                        if (!math.isfinite(support) || math.abs(support - ground) > .25f)
                        { reason = "pole base crosses a steep terrain edge"; return false; }
                    }
                    position.y = ground;
                    if (math.distancesq(position, point) > 80f * 80f) { reason = "pole exceeds 80 m fixture radius"; return false; }
                    positions[i] = new ObjectTransform(position, quaternion.LookRotationSafe(tangent, math.up()));
                }
                if (math.distance(positions[0].m_Position, positions[1].m_Position) < 5f)
                { reason = "insufficient pole separation"; return false; }
                return true;
            }

            internal bool Observe(int hour, string view)
            {
                check();
                EntityManager em = host.EntityManager; em.CompleteAllTrackedJobs();
                var effects = host.World.GetExistingSystemManaged<EffectControlSystem>();
                Require(effects != null, "Native effect control is unavailable for the road fixture");
                var enabled = effects.GetEnabledData(true, out var dependencies); dependencies.Complete();
                bool complete = true;
                var rows = new List<object>();
                foreach (Entity light in lights)
                {
                    Require(IsOwned(em, light), "A test-owned native road light disappeared or changed ownership");
                    var subObjects = em.GetBuffer<SubObject>(road);
                    bool referenced = false;
                    for (int i = 0; i < subObjects.Length; i++)
                        if (subObjects[i].m_SubObject == light) { referenced = true; break; }
                    StreetLightState state = em.GetComponentData<StreetLight>(light).m_State;
                    var active = new List<object>();
                    for (int i = 0; i < enabled.Length; i++)
                    {
                        EnabledEffectData effect = enabled[i];
                        if (effect.m_Owner != light || (effect.m_Flags & (EnabledEffectFlags.IsEnabled | EnabledEffectFlags.IsLight)) !=
                            (EnabledEffectFlags.IsEnabled | EnabledEffectFlags.IsLight) || effect.m_Intensity <= 0) continue;
                        active.Add(new { index = i, prefab = effect.m_Prefab.ToString(), flags = effect.m_Flags.ToString(),
                            effect.m_Intensity, position = A(effect.m_Position), color_scale = A(effect.m_Scale) });
                    }
                    bool off = (state & StreetLightState.TurnedOff) != 0;
                    complete &= referenced && (hour == 12 ? off && active.Count == 0 : !off && active.Count > 0);
                    rows.Add(new { entity = light.ToString(), owner = road.ToString(), referenced_by_road = referenced,
                        state = state.ToString(), enabled_native_light_effects = active });
                }
                observations.Add(new { hour, view, complete, lights = rows });
                return complete;
            }

            internal async Task Cleanup()
            {
                if (!ownsWorld()) { report["cleanup"] = "Original world no longer owned; no writes to replacement world"; return; }
                EntityManager em = host.EntityManager; em.CompleteAllTrackedJobs();
                var failures = new List<Exception>();
                foreach (Entity light in lights)
                    try
                    {
                        if ((IsOwned(em, light) || (initializing.Contains(light) && em.Exists(light))) && !em.HasComponent<Deleted>(light))
                            em.AddComponent<Deleted>(light);
                    }
                    catch (Exception error) { failures.Add(error); }
                var simulation = host.World.GetExistingSystemManaged<SimulationSystem>();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    while (lights.Any(e => em.Exists(e)))
                    {
                        check();
                        Require(watch.Elapsed.TotalSeconds < 10, "Native road-light entities did not finish deletion");
                        simulation.selectedSpeed = 1;
                        await Task.Delay(100);
                        check(); em.CompleteAllTrackedJobs();
                    }
                    check();
                    if (flagsApplied && road != Entity.Null && em.Exists(road) && em.HasComponent<Road>(road))
                    {
                        Road value = em.GetComponentData<Road>(road);
                        report["road_flags_before_cleanup"] = value.m_Flags.ToString();
                        value.m_Flags &= ~addedFlags;
                        em.SetComponentData(road, value);
                        flagsApplied = false;
                        report["road_flags_after_cleanup"] = value.m_Flags.ToString();
                    }
                }
                catch (Exception error) { failures.Add(error); }
                if (failures.Count > 0)
                {
                    report["cleanup_errors"] = failures.Select(e => e.ToString()).ToArray();
                    throw new AggregateException("Native road-light fixture cleanup failed", failures);
                }
                report["cleanup"] = "All test poles deleted through the native lifecycle; only fixture-added IsLit bits removed";
            }

            bool IsOwned(EntityManager em, Entity entity) => em.Exists(entity) && em.HasComponent<OwnedRoadGlassLight>(entity) &&
                em.GetComponentData<OwnedRoadGlassLight>(entity).Token == token;
        }
    }
}
#endif
