using System;
using System.Collections.Generic;

namespace CDLC.BusDriver
{
    /// <summary>Pure lifecycle guard for sequential, explicitly requested main-menu inspections.</summary>
    internal sealed class BusReviewRequestState
    {
        readonly HashSet<string> m_Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool Busy { get; private set; }
        internal bool Interrupted { get; private set; }
        internal bool Suspended { get; private set; }
        internal bool RequiresRestart { get; private set; }
        internal bool Stopping { get; private set; }
        internal bool HasAcceptedRequest { get; private set; }
        internal string ActiveRequestId { get; private set; }

        internal bool TryBegin(string requestId, bool menuReady, bool resume, bool close, out string error)
        {
            error = null;
            if (requestId == null || requestId.Length != 32 || !Guid.TryParseExact(requestId, "N", out _)) error = "invalid request_id";
            else if (m_Seen.Contains(requestId)) error = "request_id already consumed";
            else if (Busy) error = "another request is running";
            else if (Stopping) error = "process exit already requested";
            else if (!menuReady) error = "game is not in the ready main menu";
            else if (RequiresRestart && !close) error = "session requires a cold restart";
            else if (Suspended && !resume) error = "user took over; explicit resume required";
            if (error != null) return false;
            m_Seen.Add(requestId);
            if (resume) Suspended = false;
            Busy = true; Interrupted = false; HasAcceptedRequest = true; ActiveRequestId = requestId;
            return true;
        }

        internal void ObserveMenu(bool ready)
        {
            if (ready || !HasAcceptedRequest) return;
            Suspended = true;
            if (Busy) Interrupted = true;
        }

        internal void MarkRequiresRestart() => RequiresRestart = true;

        internal bool Complete(string requestId, bool quitRequested)
        {
            if (!Busy || !string.Equals(requestId, ActiveRequestId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the active request may complete");
            bool quit = quitRequested && !Interrupted && !Suspended;
            Busy = false; ActiveRequestId = null;
            if (quit) Stopping = true;
            return quit;
        }
    }
}
