using System;
using System.Linq;
using CDLC.BusDriver.Interior;
using SceneRuntime.Tests;
using Unity.Mathematics;
using UnityEngine;
using LedPanel = CDLC.Asset.Led.LedPanel;

internal static class Program
{
    private static int s_Checks;

    private static void Main()
    {
        ConstructorFailures();
        RetainedConstructorFailure();
        DisposeRetries();
        HdSignFailure();
        CabMaterials.DestroyAll();
        Console.WriteLine($"LED panel lifetime: {s_Checks} checks passed (fake Unity; rendering unverified).");
    }

    private static void Check(bool condition, string message)
    {
        s_Checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { Check(true, "expected " + typeof(T).Name); return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Start()
    {
        CabMaterials.DestroyAll();
        Engine.Reset();
    }

    private static Material[] BorrowedInks() => Enumerable.Range(0, 4).Select(i => new Material(Shader.Find("borrowed")) { name = "external ink " + i }).ToArray();
    private static LedPanel Panel(Transform parent, Material[] inks) =>
        new LedPanel(parent, float3.zero, new float2(2, .5f), 128, 32, inks, quaternion.identity, null, CabMaterials.AcquireResources());
    private static Mesh PanelMesh() => Engine.Objects.OfType<Mesh>().Single(m => m.name == "CDL LED panel mesh");

    private static void Finish(GameObject parent, Material[] inks)
    {
        Engine.Callback = null; Engine.FailOnce = null;
        CabCleanup.RetryPending(); CabMaterials.DestroyAll();
        Check(!parent.DestroyScheduled && inks.All(m => !m.DestroyScheduled), "Panel cleanup released a borrowed parent or ink.");
        UnityEngine.Object.Destroy(parent);
        foreach (Material material in inks) UnityEngine.Object.Destroy(material);
    }

    private static void ConstructorFailures()
    {
        foreach (string failure in new[] { "Dynamic:CDL LED panel mesh", "Parent:LED panel", "Add:LED panel:MeshRenderer", "Materials:LED panel:4" })
        {
            Start();
            var parent = new GameObject("parent");
            Material[] inks = BorrowedInks();
            Material pooled = CabMaterials.Glow();
            try
            {
                Engine.FailOnce = failure;
                Throws<InvalidOperationException>(() => Panel(parent.transform, inks));
                Check(Engine.Objects.OfType<Mesh>().All(m => m.DestroyScheduled) && Engine.Objects.OfType<GameObject>().Where(g => g != parent).All(g => g.DestroyScheduled),
                    "Construction failure abandoned an owned mesh or hierarchy: " + failure);
                CabMaterials.DestroyAll();
                Check(pooled.DestroyScheduled, "Construction failure retained a material lease: " + failure);
            }
            finally { Finish(parent, inks); }
        }
    }

    private static void RetainedConstructorFailure()
    {
        Start();
        var parent = new GameObject("parent");
        Material[] inks = BorrowedInks();
        Material pooled = CabMaterials.Glow();
        try
        {
            Engine.FailOnce = "Materials:LED panel:4";
            var cleanupFailure = new InvalidOperationException("detachment unavailable");
            Engine.Callback = action => { if (action == "Active:LED panel:False") throw cleanupFailure; };
            AggregateException allocation = Throws<AggregateException>(() => Panel(parent.transform, inks));
            Check(allocation.Flatten().InnerExceptions.Contains(cleanupFailure), "Failed construction lost its cleanup failure.");
            Mesh mesh = PanelMesh();
            GameObject holder = Engine.Root("LED panel");
            Throws<AggregateException>(CabMaterials.DestroyAll);
            Check(!mesh.DestroyScheduled && !holder.DestroyScheduled && !pooled.DestroyScheduled && Engine.Filter("LED panel").sharedMesh == mesh,
                "Failed detachment released native ownership or the last material lease.");
            Engine.Callback = null;
            CabCleanup.RetryPending();
            Check(mesh.DestroyScheduled && holder.DestroyScheduled && pooled.DestroyScheduled && Engine.Filter("LED panel").sharedMesh == null,
                "The constructor's retained owner did not complete cleanup on retry.");
            int destroys = Engine.Events.Count(e => e.StartsWith("Destroy:"));
            CabCleanup.RetryPending();
            Check(Engine.Events.Count(e => e.StartsWith("Destroy:")) == destroys, "A completed retained owner was retried again.");
        }
        finally { Finish(parent, inks); }
    }

    private static void DisposeRetries()
    {
        foreach (string failure in new[] { "Active:LED panel:False", "Enabled:LED panel:False", "Materials:LED panel:0", "Mesh:LED panel:null",
            "Destroy:CDL LED panel mesh", "Destroy:LED panel", "pool" })
        {
            Start();
            var parent = new GameObject("parent");
            Material[] inks = BorrowedInks();
            Material pooled = CabMaterials.Glow();
            LedPanel panel = Panel(parent.transform, inks);
            Mesh mesh = PanelMesh();
            GameObject holder = Engine.Root("LED panel");
            try
            {
                CabMaterials.DestroyAll();
                Check(!pooled.DestroyScheduled, "A live panel did not hold its resource lease.");
                Engine.FailOnce = failure == "pool" ? "Destroy:" + pooled.name : failure;
                Throws<AggregateException>(panel.Dispose);
                Check(!panel.Alive && !pooled.DestroyScheduled, "Failed disposal revived a panel or lost its resource retry owner.");
                if (failure.StartsWith("Active:") || failure.StartsWith("Enabled:") || failure.StartsWith("Materials:") || failure.StartsWith("Mesh:"))
                    Check(!mesh.DestroyScheduled && !holder.DestroyScheduled, "Native objects were destroyed before complete detachment.");
                panel.Dispose();
                Check(mesh.DestroyScheduled && holder.DestroyScheduled && pooled.DestroyScheduled && Engine.Filter("LED panel").sharedMesh == null && Engine.Renderer("LED panel").sharedMaterials.Length == 0,
                    "Dispose retry failed to finish native and shared-resource cleanup: " + failure);
                int meshRelease = Engine.Events.IndexOf("Destroy:CDL LED panel mesh");
                int hierarchyRelease = Engine.Events.IndexOf("Destroy:LED panel");
                Check(Engine.Events.IndexOf("Mesh:LED panel:null") < meshRelease && Engine.Events.IndexOf("Materials:LED panel:0") < hierarchyRelease,
                    "Native release preceded mesh/material unbinding.");
                int destroys = Engine.Events.Count(e => e.StartsWith("Destroy:"));
                panel.Dispose();
                Check(Engine.Events.Count(e => e.StartsWith("Destroy:")) == destroys, "Successful Dispose was not idempotent.");
            }
            finally { Engine.Callback = null; Engine.FailOnce = null; panel.Dispose(); Finish(parent, inks); }
        }
    }

    private static void HdSignFailure()
    {
        Start();
        var parent = new GameObject("parent");
        Material[] inks = BorrowedInks();
        Material pooled = CabMaterials.Glow();
        try
        {
            Engine.FailOnce = "Add:LED panel:MeshRenderer";
            Throws<InvalidOperationException>(() => new HdSign(parent.transform, float3.zero, new float2(2, .5f), 128, inks, null,
                new DotFont(), new LedFont(), quaternion.identity, 20f));
            CabMaterials.DestroyAll();
            Check(PanelMesh().DestroyScheduled && Engine.Root("LED panel").DestroyScheduled && pooled.DestroyScheduled,
                "HdSign construction lost the failed panel's cleanup owner.");
        }
        finally { Finish(parent, inks); }
    }
}
