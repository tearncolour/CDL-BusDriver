#if CDLC_AUTHORING
using System;
using System.Collections.Generic;

namespace CDLC.BusDriver
{
    internal sealed class BusRoadGlassRequests : IDisposable
    {
        readonly string owner;
        bool parking, view;

        internal BusRoadGlassRequests(string owner) { this.owner = owner; }

        internal void SetParking()
        {
            parking = true;
            CDLC.DrivingApi.SetParkingBrake(owner, true);
        }

        internal void SetView()
        {
            view = true;
            Core.SetCabViewRequested(owner, true);
        }

        internal void ClearView()
        {
            if (!view) return;
            Core.SetCabViewRequested(owner, false);
            view = false;
        }

        public void Dispose()
        {
            var failures = new List<Exception>();
            try { ClearView(); } catch (Exception error) { failures.Add(error); }
            if (parking)
                try { CDLC.DrivingApi.SetParkingBrake(owner, false); parking = false; }
                catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException("Road glass request cleanup failed", failures);
        }
    }
}
#endif
