using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    internal static class BusGlassContextDiagnostic
    {
        internal static async Task<List<object>> Run(Camera camera, RenderTexture target, string directory, string name,
            Vector3 origin, Transform model, List<HDAdditionalLightData> lights, List<object> shots)
        {
            var result = new List<object>(); var hd = camera.GetComponent<HDAdditionalCameraData>();
            var aa = hd.antialiasing; bool history = hd.hasPersistentHistory;
            var settings = hd.renderingPathCustomFrameSettings; var mask = hd.renderingPathCustomFrameSettingsOverrideMask;
            int culling = camera.cullingMask; const int layer = 30;
            var hierarchy = model.GetComponentsInChildren<Transform>(true);
            var layers = hierarchy.Select(transform => transform.gameObject.layer).ToArray();
            var positions = lights.Select(light => light.transform.position).ToArray();
            var intensities = lights.Select(light => light.intensity).ToArray(); var units = lights.Select(light => light.lightUnit).ToArray();
            var glazing = model.GetComponentsInChildren<MeshRenderer>(true).Where(renderer => renderer.name.StartsWith("Glass ", StringComparison.Ordinal)).ToArray();
            var visibility = glazing.Select(renderer => renderer.enabled).ToArray();
            var outsiders = UnityEngine.Object.FindObjectsOfType<Renderer>().Where(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy &&
                renderer.gameObject.layer == layer && !renderer.transform.IsChildOf(model)).ToArray();
            if (outsiders.Length != 0) throw new InvalidOperationException("Private diagnostic render layer is occupied");
            GameObject plane = null; Mesh planeMesh = null;
            try
            {
                foreach (var transform in hierarchy) transform.gameObject.layer = layer;
                camera.cullingMask = 1 << layer;
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing; hd.hasPersistentHistory = true;
                for (int i = 0; i < lights.Count; i++)
                    lights[i].SetIntensity(intensities[i], lights[i].GetComponent<Light>().type == LightType.Directional ? LightUnit.Lux : LightUnit.Lumen);
                foreach (string group in new[] { "baseline", "near-origin", "postprocess-off", "single-near-glass-plane" })
                {
                    Vector3 currentOrigin = group == "near-origin" ? new Vector3(0f, 30f, 0f) : origin;
                    model.position = currentOrigin;
                    for (int i = 0; i < lights.Count; i++) lights[i].transform.position = positions[i] + currentOrigin - origin;
                    var currentSettings = settings; var currentMask = mask;
                    currentSettings.SetEnabled(FrameSettingsField.Postprocess, group != "postprocess-off");
                    currentMask.mask[(uint)FrameSettingsField.Postprocess] = true;
                    currentSettings.SetEnabled(FrameSettingsField.ExposureControl, true);
                    currentMask.mask[(uint)FrameSettingsField.ExposureControl] = true;
                    hd.renderingPathCustomFrameSettings = currentSettings; hd.renderingPathCustomFrameSettingsOverrideMask = currentMask;
                    object planeEvidence = null;
                    if (group == "single-near-glass-plane")
                    {
                        planeEvidence = SelectPlane(model, layer, out plane, out planeMesh);
                        foreach (var renderer in glazing) renderer.enabled = false;
                    }
                    object immediate = null;
                    using (var exposure = new BusVehicleMaterialDiagnostics.PrivateExposure(camera, currentOrigin, 13f))
                        await BusVehicleProbe.Capture(camera, target, directory, name, 0, "context-" + group + "-handrail-material",
                            new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f, shots, 64, true, () =>
                            {
                                var hdcam = HDCamera.GetOrCreate(camera); var resolved = hdcam.volumeStack.GetComponent<Exposure>();
                                immediate = new { postprocess = hdcam.frameSettings.IsEnabled(FrameSettingsField.Postprocess),
                                    exposure_control = hdcam.frameSettings.IsEnabled(FrameSettingsField.ExposureControl),
                                    exposure_mode = resolved.mode.value.ToString(), exposure_ev = resolved.fixedExposure.value,
                                    environment = BusNativeVehicleProbe.EnvironmentEvidence() };
                            }, null, currentOrigin);
                    result.Add(new { group, scope = "Diagnostic only: single-glass and postprocess-disabled groups do not represent complete final appearance.",
                        origin = V(currentOrigin), private_render_layer = layer, unrelated_visible_renderers_on_layer = outsiders.Length,
                        model_relative_pose_unchanged = true, plane = planeEvidence, immediate });
                }
            }
            finally
            {
                if (plane != null) { plane.SetActive(false); UnityEngine.Object.Destroy(plane); }
                if (planeMesh != null) UnityEngine.Object.Destroy(planeMesh);
                for (int i = 0; i < glazing.Length; i++) glazing[i].enabled = visibility[i];
                model.position = origin;
                for (int i = 0; i < hierarchy.Length; i++) hierarchy[i].gameObject.layer = layers[i];
                camera.cullingMask = culling; hd.antialiasing = aa; hd.hasPersistentHistory = history;
                hd.renderingPathCustomFrameSettings = settings; hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                for (int i = 0; i < lights.Count; i++) { lights[i].transform.position = positions[i]; lights[i].SetIntensity(intensities[i], units[i]); }
            }
            return result;
        }

        internal static object SelectPlane(Transform model, int layer, out GameObject holder, out Mesh result)
        {
            var filter = model.GetComponentsInChildren<MeshFilter>().Single(item => item.name.StartsWith("Glass ", StringComparison.Ordinal) && !item.name.Contains(" on part "));
            Mesh source = filter.sharedMesh; Vector3[] vertices = source.vertices; int[] indices = source.GetTriangles(0);
            int best = -1; float area = 0; Vector3 eye = new Vector3(-.15f, 1.65f, 1.65f);
            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3 a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]], centre = (a + b + c) / 3f;
                Vector3 normal = Vector3.Cross(b - a, c - a);
                if (centre.x < 1.1f || centre.y < .9f || centre.y > 2.5f || centre.z < 0f || centre.z > 3.4f ||
                    normal.sqrMagnitude < area || Vector3.Dot(normal.normalized, (eye - centre).normalized) < .5f ||
                    Vector3.Dot((centre - eye).normalized, new Vector3(.6f, .5f, -.9f).normalized) < .3f) continue;
                best = i; area = normal.sqrMagnitude;
            }
            if (best < 0 || area < .01f) throw new InvalidOperationException("No usable near-side original glass plane");
            Vector3 p = vertices[indices[best]], n = Vector3.Cross(vertices[indices[best + 1]] - p, vertices[indices[best + 2]] - p).normalized;
            var bounds = new Bounds(p, Vector3.zero); bounds.Encapsulate(vertices[indices[best + 1]]); bounds.Encapsulate(vertices[indices[best + 2]]); bounds.Expand(.0002f);
            var selected = new List<int>();
            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3 a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
                if (!bounds.Contains(a) || !bounds.Contains(b) || !bounds.Contains(c) ||
                    Mathf.Abs(Vector3.Dot(a - p, n)) > .0001f || Mathf.Abs(Vector3.Dot(b - p, n)) > .0001f || Mathf.Abs(Vector3.Dot(c - p, n)) > .0001f ||
                    Vector3.Dot(Vector3.Cross(b - a, c - a).normalized, n) < .9999f) continue;
                selected.Add(indices[i]); selected.Add(indices[i + 1]); selected.Add(indices[i + 2]);
            }
            result = CopyIndices(source, selected); holder = new GameObject("CDLC diagnostic single original glass plane") { layer = layer, hideFlags = HideFlags.DontSave };
            holder.transform.SetParent(filter.transform, false); holder.AddComponent<MeshFilter>().sharedMesh = result;
            var original = filter.GetComponent<MeshRenderer>(); var renderer = holder.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = original.sharedMaterials; renderer.shadowCastingMode = original.shadowCastingMode;
            renderer.receiveShadows = original.receiveShadows; renderer.motionVectorGenerationMode = original.motionVectorGenerationMode;
            renderer.lightProbeUsage = original.lightProbeUsage; renderer.reflectionProbeUsage = original.reflectionProbeUsage;
            return new { source = source.name, triangles = selected.Count / 3, bounds_center = V(bounds.center), bounds_size = V(bounds.size), normal = V(n),
                packed_data = BusGlassBufferAudit.Compare(source, result, "single-plane-diagnostic", 0, 1, -1) };
        }

        static Mesh CopyIndices(Mesh source, List<int> triangles)
        {
            var copy = new Mesh { name = "single plane packed vertex copy", hideFlags = HideFlags.DontSave };
            try
            {
                using (var input = Mesh.AcquireReadOnlyMeshData(source))
                {
                    var output = Mesh.AllocateWritableMeshData(1); bool transferred = false;
                    try
                    {
                        var to = output[0]; to.SetVertexBufferParams(source.vertexCount, source.GetVertexAttributes());
                        for (int stream = 0; stream < input[0].vertexBufferCount; stream++) to.GetVertexData<byte>(stream).CopyFrom(input[0].GetVertexData<byte>(stream));
                        to.SetIndexBufferParams(triangles.Count, IndexFormat.UInt32); var indices = to.GetIndexData<uint>();
                        for (int i = 0; i < triangles.Count; i++) indices[i] = (uint)triangles[i];
                        to.subMeshCount = 1; to.SetSubMesh(0, new SubMeshDescriptor(0, triangles.Count) { bounds = source.bounds, firstVertex = 0, vertexCount = source.vertexCount }, MeshUpdateFlags.DontRecalculateBounds);
                        Mesh.ApplyAndDisposeWritableMeshData(output, copy, MeshUpdateFlags.DontRecalculateBounds); transferred = true;
                    }
                    finally { if (!transferred) output.Dispose(); }
                }
                copy.bounds = source.bounds; return copy;
            }
            catch { UnityEngine.Object.Destroy(copy); throw; }
        }
        static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
    }
}
