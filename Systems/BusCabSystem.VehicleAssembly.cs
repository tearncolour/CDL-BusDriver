using System;
using System.Collections.Generic;
using CDLC.Asset.Articulation;
using CDLC.BusDriver.Interior.Models;
using Colossal.Entities;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CDLC.BusDriver
{
    public sealed partial class BusCabSystem
    {
        private VehicleAssembly m_Assembly;
        // the same vehicle as CDL Asset completes and draws it (its manifest checked against the profile's assembly): the cab's joint
        private ArticulatedVehicle m_Articulated;
        private bool m_AssemblyPending;
        private float m_AssemblyRetryAt;
        private string m_AssemblyBindFailure;

        private VehicleProfile AuthoredProfileOf(Entity vehicle)
        {
            if (!EntityManager.TryGetComponent(vehicle, out PrefabRef reference)) return null;
            PrefabSystem prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            return prefabs != null && prefabs.TryGetPrefab(reference.m_Prefab, out PrefabBase prefab)
                ? VehiclePacks.ForPrefab(prefab) : null;
        }

        private bool AssemblyBindingFailed(string reason)
        {
            m_AssemblyPending = true;
            m_AssemblyRetryAt = UnityEngine.Time.unscaledTime+.5f;
            if (m_AssemblyBindFailure != reason)
            {
                m_AssemblyBindFailure = reason;
                Mod.log.Warn("Authored vehicle assembly not replaced: "+reason+". Native bodies remain visible.");
            }
            return false;
        }

        private bool TryBindAuthoredAssembly(VehicleProfile leading, List<Entity> entities, out BusInteriorSpec[] specs)
        {
            specs = null;
            VehicleAssembly assembly = leading.Assembly;
            if (leading.Name != assembly.Front.Prefab || entities.Count != 2 || entities[0] == entities[1])
                return AssemblyBindingFailed("expected the declared front and exactly one distinct rear entity");
            VehicleProfile rear = AuthoredProfileOf(entities[1]);
            if (rear?.Assembly == null || rear.Name != assembly.Rear.Prefab || rear.SectionId != assembly.Rear.Id ||
                rear.Assembly.Identity != assembly.Identity ||
                !string.Equals(VehiclePacks.FolderOf(rear), VehiclePacks.FolderOf(leading), StringComparison.OrdinalIgnoreCase))
                return AssemblyBindingFailed("rear entity does not belong to this explicit assembly manifest");
            if (!EntityManager.TryGetComponent(entities[0], out PrefabRef fp) ||
                !EntityManager.TryGetComponent(fp.m_Prefab, out CarTractorData tractor) ||
                !EntityManager.TryGetComponent(entities[1], out PrefabRef rp) ||
                !EntityManager.TryGetComponent(rp.m_Prefab, out CarTrailerData trailer))
                return AssemblyBindingFailed("native tractor/trailer attachment data is missing");
            if (math.distance(tractor.m_AttachPosition, assembly.FrontAnchor) > .001f ||
                math.distance(trailer.m_AttachPosition, assembly.RearAnchor) > .001f)
                return AssemblyBindingFailed("native local attachment positions differ from the authored source anchors");
            if (!World.GetOrCreateSystemManaged<ArticulationSpawnSystem>().TryGetVehicle(fp.m_Prefab, out ArticulatedVehicle articulated))
                return AssemblyBindingFailed("CDL Asset has no articulation manifest for this front section");
            if (articulated.Rear.Prefab != assembly.Rear.Prefab || articulated.JointSha256 != assembly.Joint.Sha256 ||
                articulated.MaxBendDegrees != assembly.Joint.MaxBendDegrees || articulated.MaxSeparation != assembly.MaxSeparation ||
                math.distance(articulated.FrontAnchor, assembly.FrontAnchor) > .001f || math.distance(articulated.RearAnchor, assembly.RearAnchor) > .001f)
                return AssemblyBindingFailed("the pack's articulation manifest differs from its profile's assembly");
            try
            {
                // Validate both complete meshes before exposing either profile to the eye/build path.
                _ = assembly.Front.Profile.Mesh; _ = assembly.Rear.Profile.Mesh;
            }
            catch (Exception e) { return AssemblyBindingFailed("runtime section mesh rejected: "+e.Message); }
            m_Assembly = assembly; m_Articulated = articulated; m_AssemblyPending = false; m_AssemblyBindFailure = null;
            specs = new[] { assembly.Front.Profile.Spec, assembly.Rear.Profile.Spec };
            return true;
        }
    }
}
