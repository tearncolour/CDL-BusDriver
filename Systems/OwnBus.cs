using System;
using CDLC.BusDriver.Interior.Models;

namespace CDLC.BusDriver
{
    /// <summary>The mod's own bus (the procedural 12 m CDLCBus12, packed from Asset/CDLCBus12). Kept apart from the authoring tools
    /// (BusVehicleAssetSystem), which a player build leaves out, so the cab and the AI signs can still tell it from a pack's bus.</summary>
    internal static class OwnBus
    {
        internal const string Name = "CDLCBus12";

        internal static bool Is(string prefabName) => string.Equals(prefabName, Name, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The spec the own bus is drawn and signed for: the one its native model, its glass, its signs and its embedded runtime mesh are made from (BusInteriorSpec.Default),
        /// on the traffic side of the city. Not measured from the prefab like a bus of someone else's: the native mesh's bounds (nose, bumper, roof pods) are not
        /// the body's, and a spec that differs from the default builds another bus at run time under glass and signs made for this one.
        /// </summary>
        internal static BusInteriorSpec Spec(bool leftHandTraffic)
        {
            var spec = BusInteriorSpec.Default;
            spec.Section = BusSection.Solo;
            spec.DriverSide = leftHandTraffic ? 1f : -1f;
            return spec;
        }
    }
}
