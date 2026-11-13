using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    // Explicit inspection illumination, never a replacement for a road-scene check.
    internal sealed class BusVehicleInspectionLighting : IDisposable
    {
        readonly List<HDAdditionalLightData> original, added = new List<HDAdditionalLightData>();
        readonly Vector3 origin;
        bool disposed;
        internal BusVehicleInspectionLighting(Vector3 origin, List<HDAdditionalLightData> original)
        {
            this.origin = origin; this.original = original;
            // Base lights are initialized by their owner. This scope only owns
            // supplementary lights; it must not snapshot/restore an incompatible
            // default unit (HDRP logs that error without throwing an exception).
            foreach (var light in original)
                if (!HDAdditionalLightData.IsValidLightUnitForType(light.type, light.spotLightShape, light.lightUnit))
                    throw new InvalidOperationException("Invalid base inspection light unit: " + light.name);
            Add("front inspection key", LightType.Directional, new Vector3(0f, 10f, 10f), 10000f, LightUnit.Lux);
            foreach (float z in new[] { -4f, -2f, 0f, 2f, 4f })
                Add("interior inspection " + z, LightType.Point, new Vector3(0f, 1.65f, z), 16000f, LightUnit.Lumen);
        }
        void Add(string label, LightType type, Vector3 position, float intensity, LightUnit unit)
        {
            var obj = new GameObject("CDLC " + label) { hideFlags = HideFlags.DontSave };
            obj.transform.position = origin + position; obj.transform.rotation = Quaternion.Euler(48f, 205f, 0f);
            var light = obj.AddComponent<Light>(); light.type = type; light.range = 5.5f; light.shadows = LightShadows.None;
            var data = obj.AddComponent<HDAdditionalLightData>(); data.SetIntensity(intensity, unit); added.Add(data);
        }
        internal object Evidence() => new {
            scope = "Dedicated inspection illumination: exterior EV13, interior EV12. Not normal road-scene lighting.",
            lights = original.Concat(added).Select(light => new { light.name, intensity = light.intensity,
                unit = light.lightUnit.ToString(), type = light.type.ToString(),
                position = new[] { light.transform.position.x - origin.x, light.transform.position.y - origin.y, light.transform.position.z - origin.z },
                rotation = new[] { light.transform.eulerAngles.x, light.transform.eulerAngles.y, light.transform.eulerAngles.z },
                range = light.GetComponent<Light>().range, shadows = light.GetComponent<Light>().shadows.ToString() }).ToArray()
        };
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var light in added) { light.gameObject.SetActive(false); UnityEngine.Object.Destroy(light.gameObject); }
        }
    }
}
