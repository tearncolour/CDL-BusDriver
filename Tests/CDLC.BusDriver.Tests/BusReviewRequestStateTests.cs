using System;
using CDLC.BusDriver;

// Exercise the production request gate directly. No game process, real clocks or copied state machine.
static class BusReviewRequestStateTests
{
    const string A = "0123456789abcdef0123456789abcdef";
    const string B = "1123456789abcdef0123456789abcdef";
    const string C = "2123456789abcdef0123456789abcdef";
    const string D = "3123456789abcdef0123456789abcdef";

    internal static void Run(Action<bool, string> check)
    {
        IdentityAndLifecycle(check);
        BusyAndOldResponses(check);
        UserTakeover(check);
        IdleTakeover(check);
        RestartRequired(check);
        ExplicitQuit(check);
    }

    static bool Begin(BusReviewRequestState state, string id, bool ready = true, bool resume = false, bool close = false)
        => state.TryBegin(id, ready, resume, close, out _);

    static bool ThrowsOldResponse(BusReviewRequestState state, string id)
    {
        try { state.Complete(id, true); }
        catch (InvalidOperationException) { return true; }
        return false;
    }

    static void IdentityAndLifecycle(Action<bool, string> check)
    {
        var state = new BusReviewRequestState();
        check(!state.Busy && !state.HasAcceptedRequest && !state.Interrupted && !state.Suspended &&
            !state.RequiresRestart && !state.Stopping, "review session: fresh gate owns no request");
        foreach (string id in new[] { null, "", "0123", new string('a', 31), new string('b', 33),
            new string('g', 32), " " + A.Substring(1), A.Substring(0, 31) + "/" })
        {
            bool accepted = state.TryBegin(id, true, false, false, out string error);
            check(!accepted && !string.IsNullOrEmpty(error) && !state.Busy && !state.HasAcceptedRequest,
                "review session: malformed request identity cannot acquire or consume session (" + (id ?? "null") + ")");
        }

        state.ObserveMenu(false); // Starting the game is not user takeover of an as-yet unowned session.
        check(!state.Suspended && !Begin(state, A, false) && !state.HasAcceptedRequest,
            "review session: initial loading rejects work without poisoning an unowned session");
        check(Begin(state, A) && state.Busy && state.ActiveRequestId == A && state.HasAcceptedRequest,
            "review session: first ready-menu request acquires the gate");
        check(!state.Complete(A, false) && !state.Busy && !state.Stopping,
            "review session: normal completion releases work and keeps the process available");
        check(!Begin(state, A) && !state.Busy,
            "review session: a completed request identity cannot be replayed");
        check(Begin(state, B), "review session: a distinct follow-up runs in the same session");
        check(!state.Complete(B, false) && !state.Busy,
            "review session: repeated non-quit work leaves the session reusable");

        var upper = new BusReviewRequestState();
        check(Begin(upper, A.ToUpperInvariant()), "review session: uppercase hexadecimal identity is valid");
        check(!upper.Complete(A, false) && !Begin(upper, A),
            "review session: identity casing cannot evade completion matching or duplicate rejection");
    }

    static void BusyAndOldResponses(Action<bool, string> check)
    {
        var state = new BusReviewRequestState();
        Begin(state, A);
        check(!Begin(state, A) && !Begin(state, B) && !Begin(state, C, true, true, true) &&
            state.Busy && state.ActiveRequestId == A && !state.Stopping,
            "review session: duplicate, concurrent and close/resume requests cannot replace active work");
        check(ThrowsOldResponse(state, B) && state.Busy && state.ActiveRequestId == A && !state.Stopping,
            "review session: a foreign response cannot release or quit the active request");
        state.Complete(A, false);
        check(Begin(state, B), "review session: busy rejection did not consume the queued request identity");
        check(ThrowsOldResponse(state, A) && state.Busy && state.ActiveRequestId == B && !state.Stopping,
            "review session: delayed completion of old work cannot release the next request");
        state.Complete(B, false);
        check(ThrowsOldResponse(state, B) && !state.Busy && !state.Stopping,
            "review session: duplicate completion cannot turn an idle session into shutdown");
    }

    static void UserTakeover(Action<bool, string> check)
    {
        var state = new BusReviewRequestState();
        Begin(state, A);
        state.ObserveMenu(false); // Covers loading/game/manager-unavailable as normalized by the dispatcher.
        check(state.Busy && state.Interrupted && state.Suspended && state.ActiveRequestId == A,
            "review session: leaving ready menu during capture latches interruption without losing ownership");
        state.ObserveMenu(true);
        check(state.Interrupted && state.Suspended,
            "review session: returning to menu does not silently clear takeover");
        check(!Begin(state, B, true, true) && state.Busy && state.Interrupted,
            "review session: resume cannot clear interruption while cleanup is still active");
        check(!state.Complete(A, true) && !state.Busy && !state.Stopping && state.Suspended,
            "review session: interrupted request cleans up but cannot autoquit even after return to menu");
        check(!Begin(state, B) && !state.Busy && state.Suspended,
            "review session: fresh work requires explicit resume after user takeover");
        check(!Begin(state, B, false, true) && state.Suspended,
            "review session: resume while loading is rejected without clearing the latch");
        check(Begin(state, B, true, true) && state.Busy && !state.Interrupted && !state.Suspended,
            "review session: explicit ready-menu resume starts fresh work and clears takeover");
        check(ThrowsOldResponse(state, A) && state.ActiveRequestId == B && state.Busy,
            "review session: interrupted old response cannot complete resumed work");
        check(!state.Complete(B, false) && !state.Stopping,
            "review session: resumed request can complete without closing the process");
    }

    static void IdleTakeover(Action<bool, string> check)
    {
        var state = new BusReviewRequestState();
        Begin(state, A);
        state.Complete(A, false);
        state.ObserveMenu(false);
        state.ObserveMenu(true);
        check(state.Suspended && !state.Busy && !Begin(state, B),
            "review session: user takeover between requests also requires an explicit resume");
        check(Begin(state, B, true, true) && !state.Suspended,
            "review session: idle takeover can be resumed once ready");
        state.Complete(B, false);
    }

    static void RestartRequired(Action<bool, string> check)
    {
        var state = new BusReviewRequestState();
        Begin(state, A);
        state.Complete(A, false);
        state.MarkRequiresRestart();
        check(state.RequiresRestart && !Begin(state, B) && !Begin(state, B, true, true) && !state.Busy,
            "review session: poisoned capture rejects normal work and cannot be repaired by resume");
        check(!Begin(state, C, false, false, true) && state.RequiresRestart && !state.Busy,
            "review session: close still cannot seize an unready menu");
        check(Begin(state, C, true, false, true) && state.RequiresRestart && state.Busy,
            "review session: explicit close is admitted when restart is required");
        check(state.Complete(C, true) && state.Stopping && !state.Busy,
            "review session: admitted close allows shutdown without clearing restart requirement");
        check(!Begin(state, D, true, true, true),
            "review session: a stopping process admits no further close or resume request");

        var takenOver = new BusReviewRequestState();
        Begin(takenOver, A);
        takenOver.ObserveMenu(false);
        takenOver.MarkRequiresRestart();
        takenOver.Complete(A, false);
        takenOver.ObserveMenu(true);
        check(!Begin(takenOver, B, true, true) && !Begin(takenOver, B, true, false, true) &&
            takenOver.Suspended && takenOver.RequiresRestart,
            "review session: poison plus takeover requires both explicit resume and close");
        check(Begin(takenOver, B, true, true, true) && takenOver.RequiresRestart,
            "review session: authorized close may resume ownership without declaring poison repaired");
        takenOver.ObserveMenu(false);
        takenOver.ObserveMenu(true);
        check(!takenOver.Complete(B, true) && !takenOver.Stopping && takenOver.Suspended,
            "review session: takeover during explicit close still blocks autoquit");
    }

    static void ExplicitQuit(Action<bool, string> check)
    {
        var state = new BusReviewRequestState();
        check(ThrowsOldResponse(state, A) && !state.Busy && !state.HasAcceptedRequest && !state.Stopping,
            "review session: unsolicited completion cannot quit a fresh session");
        Begin(state, A);
        check(state.Complete(A, true) && !state.Busy && state.Stopping,
            "review session: uninterrupted current request may explicitly quit");
        check(!Begin(state, B) && !Begin(state, B, true, true) && !Begin(state, B, true, false, true),
            "review session: stopping is terminal for work, resume and close requests");
    }
}
