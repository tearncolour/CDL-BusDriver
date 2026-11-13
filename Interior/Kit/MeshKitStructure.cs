using System;

namespace CDLC.BusDriver.Kit
{
    public sealed partial class MeshKit
    {
        // Vehicle LODs remove named details explicitly; a size cutoff must not remove
        // a pedal, mirror joint or seat support while retaining the object it supports.
        internal StructureScope KeepSmallStructure() => new StructureScope(this);

        internal readonly struct StructureScope : IDisposable
        {
            readonly MeshKit m_Kit;
            readonly float m_Previous;
            internal StructureScope(MeshKit kit)
            { m_Kit = kit; m_Previous = kit.MinFeature; kit.MinFeature = 0f; }
            public void Dispose() { m_Kit.MinFeature = m_Previous; }
        }
    }
}
