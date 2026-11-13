using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior.Models;
using Game.Prefabs;
using Game.SceneFlow;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CDLC.BusDriver
{
    public partial class BusVehicleAssetSystem
    {
        // The old startup-only pack protocol remains available for real cold-load tests.
        // This protocol never imports, hot-reloads assets, or exits after an ordinary probe.
        static readonly BusReviewRequestState s_Review = new BusReviewRequestState();
        static readonly string s_ReviewInstance = Guid.NewGuid().ToString("N");
        static bool s_LegacyBusy, s_LegacyInterrupted;
        static string s_ReviewAssemblyHash, s_ReviewAssemblyPath, s_ReviewMvid, s_ReviewStartUtc;
        static int s_ReviewPid;
        static List<ReviewPackage> s_ReviewPackages;
        static double s_ReviewNextPoll, s_ReviewNextHeartbeat;

        sealed class ReviewRequest
        {
            public int protocol { get; set; }
            public string instance_id { get; set; }
            public string request_id { get; set; }
            public string command { get; set; }
            public int? mirror_lod { get; set; }
            public string source_sha256 { get; set; }
            public string expected_assembly_sha256 { get; set; }
            public string expected_assembly_mvid { get; set; }
            public string expected_package_sha256 { get; set; }
            public string expected_resource_sha256 { get; set; }
            public string expected_assets_mvid { get; set; }
            public string expected_observer_mvid { get; set; }
            public string[] names { get; set; }
            public bool resume { get; set; }
        }
        sealed class ReviewPackage
        {
            public string name, path, sha256, resource_sha256, load_verification;
        }

        internal static void RequireTestMenu()
        {
            bool ready = TestMenuReady(GameManager.instance);
            s_Review.ObserveMenu(ready);
            if (s_LegacyBusy && !ready) s_LegacyInterrupted = true;
            if (!ready || (s_Review.Busy && s_Review.Interrupted) || (s_LegacyBusy && s_LegacyInterrupted))
                throw new InvalidOperationException("Inspection cancelled: user left the ready main menu");
        }

        // The protocol is a developer tool: its heartbeat file (every second) and the hashing and verifying of every pack at the
        // main menu are not for players. It starts once Tools/VehicleReview/review_session.ps1 has put this marker next to the
        // DLL (looked for every 2 s) and stays on for the rest of the process; the client removes the marker after its Close.
        const string kReviewMarker = "review-enabled";
        static bool s_ReviewEnabled;
        static double s_ReviewMarkerCheck;

        static bool ReviewEnabled(double now)
        {
            if (s_ReviewEnabled) return true;
            if (now < s_ReviewMarkerCheck) return false;
            s_ReviewMarkerCheck = now + 2;
            try { s_ReviewEnabled = File.Exists(Path.Combine(Mod.ModDirectory, kReviewMarker)); }
            catch (Exception) { return false; }
            if (s_ReviewEnabled) Mod.log.Info("Review protocol enabled (developer marker " + kReviewMarker + ").");
            return s_ReviewEnabled;
        }

        void PumpReviewSession(GameManager game)
        {
            if (s_RoadOwnsTransition)
            {
                // The explicit request owns its NewGame/Cleanup, but its client still needs a live identity heartbeat.
                double heartbeat = UnityEngine.Time.realtimeSinceStartupAsDouble;
                if (s_Review.Busy && heartbeat >= s_ReviewNextHeartbeat)
                {
                    try { PublishReviewStatus(); s_ReviewNextHeartbeat = heartbeat + 1; }
                    catch (Exception error) { s_Review.MarkRequiresRestart(); Mod.log.Warn("Owned-map heartbeat failed: " + error.Message); }
                }
                return;
            }
            bool ready = TestMenuReady(game);
            s_Review.ObserveMenu(ready);
            if (s_LegacyBusy && !ready) s_LegacyInterrupted = true;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now < s_ReviewNextPoll) return;
            s_ReviewNextPoll = now + .25;
            if (!ReviewEnabled(now)) return;
            try
            {
                if (s_ReviewAssemblyHash == null)
                {
                    var process = System.Diagnostics.Process.GetCurrentProcess();
                    s_ReviewPid = process.Id; s_ReviewStartUtc = process.StartTime.ToUniversalTime().ToString("o");
                    s_ReviewAssemblyPath = Path.Combine(Mod.ModDirectory, "CDLC.BusDriver.dll");
                    s_ReviewAssemblyHash = ReviewHash(s_ReviewAssemblyPath);
                    s_ReviewMvid = typeof(BusVehicleAssetSystem).Assembly.ManifestModule.ModuleVersionId.ToString("D");
                }
                // the packages' load verification checks our materials: not before HDRP exists (the client waits for ready)
                if (ready && s_ReviewPackages == null && !s_LegacyBusy && PipelineReadyForChecks(now))
                {
                    s_ReviewPackages = new List<ReviewPackage>();
                    // the mod's own bus in its Asset folder, and every bus the game loaded from a vehicle pack (the package and
                    // the runtime mesh its profile names, by their files' hashes)
                    string own = Path.Combine(Mod.ModDirectory, "Asset", kName + ".cok");
                    if (File.Exists(own))
                        using (Stream mesh = GenericBusReview.Open(GenericBusReview.Profile(BusInteriorSpec.Default)))
                            s_ReviewPackages.Add(new ReviewPackage { name = kName, path = own, sha256 = ReviewHash(own),
                                resource_sha256 = GenericBusReview.Hash(mesh), load_verification = Verify(ModelNamed(kName)) });
                    foreach ((PrefabBase prefab, VehicleProfile profile) in LoadedPacks())
                    {
                        string path = prefab.asset.path;
                        string mesh = profile.MeshPath;
                        string verification;
                        try { verification = Verify(ModelNamed(prefab.name)); }
                        catch (Exception e) { verification = "failed " + e.Message; }
                        s_ReviewPackages.Add(new ReviewPackage { name = prefab.name, path = path, sha256 = ReviewHash(path),
                            resource_sha256 = mesh != null && File.Exists(mesh) ? ReviewHash(mesh) : null, load_verification = verification });
                    }
                }
                if (now >= s_ReviewNextHeartbeat)
                {
                    PublishReviewStatus(); s_ReviewNextHeartbeat = now + 1;
                }
                if (!ready || s_ReviewPackages == null || s_Review.Busy || s_Review.Stopping || s_LegacyBusy) return;
                // Do not race a legacy startup pack request; its launcher owns the shared tool lock.
                if (File.Exists(Path.Combine(Mod.ModDirectory, kRequestFile))) return;
                string requestPath = Path.Combine(Mod.ModDirectory, "review-request.json");
                if (!File.Exists(requestPath)) return;
                string inbox = Path.Combine(Mod.ModDirectory, "review-results", "inbox-" + Guid.NewGuid().ToString("N") + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(inbox));
                File.Move(requestPath, inbox); // claim exactly the atomically published request
                ReviewRequest request = null;
                try
                {
                    if (new FileInfo(inbox).Length > 16384) throw new InvalidOperationException("Review request exceeds 16 KiB");
                    request = JsonConvert.DeserializeObject<ReviewRequest>(File.ReadAllText(inbox));
                    if (request == null || request.request_id == null || request.request_id.Length != 32 || !Guid.TryParseExact(request.request_id, "N", out _))
                        throw new InvalidOperationException("Invalid review request_id");
                    string output = Path.Combine(Mod.ModDirectory, "review-results", request.request_id);
                    if (Directory.Exists(output)) throw new InvalidOperationException("Request id already has archived evidence");
                    Directory.CreateDirectory(output);
                    File.Move(inbox, Path.Combine(output, "request.json"));
                    string error = ValidateReviewRequest(request);
                    if (error == null && !s_Review.TryBegin(request.request_id, ready, request.resume, request.command == "close", out error)) { }
                    if (error != null)
                    {
                        WriteReviewResponse(request, output, "REJECTED", "failed " + error, DateTime.UtcNow.ToString("o"),
                            new { passed = true, errors = new string[0], frames_waited = 0, remaining_objects = new string[0] }, false);
                        PublishReviewStatus();
                        return;
                    }
                    RunReviewRequest(request, output);
                    PublishReviewStatus();
                }
                catch (Exception error)
                {
                    // Keep malformed/stale envelopes for diagnosis. Never replace a previous response.
                    Mod.log.Warn("Review request rejected: " + error.Message + "; " + inbox);
                }
            }
            catch (Exception error)
            {
                s_Review.MarkRequiresRestart();
                Mod.log.Warn("Review session unavailable: " + error.Message);
                s_ReviewNextPoll = now + 5;
            }
        }

        static string ValidateReviewRequest(ReviewRequest request)
        {
            if (request.mirror_lod.HasValue && (request.command != "vehicle-mirrors" || request.mirror_lod < 0 || request.mirror_lod > 2))
                return "mirror_lod requires vehicle-mirrors and a value from 0 to 2";
            if (request.protocol != 1 || request.instance_id != s_ReviewInstance) return "protocol or process instance mismatch";
            if (request.expected_assembly_sha256 != s_ReviewAssemblyHash || request.expected_assembly_mvid != s_ReviewMvid)
                return "requested build differs from the running session";
            if (request.command == "close") return null;
            if (ReviewHash(s_ReviewAssemblyPath) != s_ReviewAssemblyHash)
            { s_Review.MarkRequiresRestart(); return "deployed DLL changed; hot replacement is not supported"; }
            if (request.command == "resume") return null;
            if (!new[] { "verify", "glass", "vehicle", "vehicle-mirrors", "vehicle-review-sample", "vehicle-refraction", "vehicle-glass-atlas", "assembly", "prepared-cancel", "bindings-snapshot" }.Contains(request.command))
                return "unsupported session command (packing requires a separate cold-load workflow)";
            if ((request.command == "prepared-cancel" || request.command == "bindings-snapshot") &&
                (!Guid.TryParse(request.expected_assets_mvid, out Guid assets) || assets == Guid.Empty ||
                 !Guid.TryParse(request.expected_observer_mvid, out Guid observer) || observer == Guid.Empty))
                return "native lifecycle stage requires exact Assets and lifecycle-observer MVIDs";
            if (request.names == null || request.names.Length != 1) return "exactly one model is required per request";
            var package = s_ReviewPackages.SingleOrDefault(p => p.name == request.names[0]);
            if (package == null || !package.load_verification.StartsWith("ok ", StringComparison.Ordinal)) return "model was not verified at session start";
            if (request.expected_package_sha256 != package.sha256 || request.expected_resource_sha256 != package.resource_sha256)
                return "requested model resource/package differs from the running session";
            if (!File.Exists(package.path) || ReviewHash(package.path) != package.sha256)
            { s_Review.MarkRequiresRestart(); return "deployed package changed; restart required"; }
            return null;
        }

        async void RunReviewRequest(ReviewRequest request, string directory)
        {
            try { await RunReviewRequestAsync(request, directory); }
            catch (Exception error)
            {
                s_Review.MarkRequiresRestart();
                if (s_Review.Busy && s_Review.ActiveRequestId == request.request_id) s_Review.Complete(request.request_id, false);
                Mod.log.Warn("Review session failed closed: " + error);
                try
                {
                    if (!File.Exists(Path.Combine(directory, "response.json")))
                        WriteReviewResponse(request, directory, "FAIL", "failed session lifecycle: " + error, DateTime.UtcNow.ToString("o"),
                            new { passed = false, errors = new[] { error.ToString() }, frames_waited = 0, remaining_objects = new string[0] }, false);
                    Mod.log.Info("CDLC_REVIEW_END " + request.request_id + " FAIL");
                    PublishReviewStatus();
                }
                catch (Exception publishError) { Mod.log.Warn("Review failure evidence could not be written: " + publishError.Message); }
            }
        }

        async Task RunReviewRequestAsync(ReviewRequest request, string directory)
        {
            string started = DateTime.UtcNow.ToString("o"), result = "failed request did not complete", status = "FAIL";
            string output = directory;
            var cleanupErrors = new List<string>();
            var previousObjects = new HashSet<int>(Resources.FindObjectsOfTypeAll<GameObject>().Where(IsReviewObject).Select(o => o.GetInstanceID()));
            bool close = request.command == "close";
            Mod.log.Info("CDLC_REVIEW_BEGIN " + request.request_id + " " + request.command);
            try
            {
                RequireTestMenu();
                string name = request.names?.FirstOrDefault();
                switch (request.command)
                {
                    case "verify": result = Verify(ModelNamed(name)); break;
                    case "glass": result = await BusGlassProbe.Run(FindOurBus, request.names, directory); break;
                    case "assembly":
                        result = await BusVehicleAssemblyProbe.Run(FindAssemblySection, name, n => Verify(ModelNamed(n)), directory, request.source_sha256);
                        break;
                    case "prepared-cancel":
                    case "bindings-snapshot":
                        output = Path.Combine(directory, request.command);
                        try
                        {
                            s_RoadOwnsTransition = true;
                            result = await BusVehicleRoadProbe.Run(this, m_PrefabSystem, name, FindAssemblySection,
                                n => Verify(ModelNamed(n)), directory: output, preparedCancel: request.command == "prepared-cancel",
                                expectedAssetsMvid: request.expected_assets_mvid, expectedObserverMvid: request.expected_observer_mvid,
                                bindingsSnapshot: request.command == "bindings-snapshot");
                        }
                        finally { s_RoadOwnsTransition = false; }
                        var cancellation = JObject.Parse(File.ReadAllText(Path.Combine(output, "road-report.json")));
                        if (cancellation["user_interrupted"]?.Value<bool>() == true) s_Review.ObserveMenu(false);
                        break;
                    case "vehicle":
                    case "vehicle-mirrors":
                    case "vehicle-review-sample":
                    case "vehicle-refraction":
                    case "vehicle-glass-atlas":
                        bool mirror = request.command == "vehicle-mirrors", sample = request.command == "vehicle-review-sample";
                        result = await BusVehicleProbe.Run(FindOurBus, request.names, materialDiagnostic: mirror || sample,
                            reviewSample: sample, mirrorProbe: mirror, sourceSha256: request.source_sha256, verifyLoaded: n => Verify(ModelNamed(n)),
                            refractionCycle: request.command == "vehicle-refraction", glassAtlas: request.command == "vehicle-glass-atlas", mirrorLod: request.mirror_lod);
                        int separator = result.LastIndexOf("; ", StringComparison.Ordinal);
                        if (separator >= 0)
                        {
                            string candidate = result.Substring(separator + 2);
                            if (Directory.Exists(candidate)) output = candidate;
                        }
                        break;
                    case "resume": result = "ok inspection session explicitly resumed"; break;
                    case "close": result = "ok explicit inspection-session exit requested"; break;
                }
                RequireTestMenu();
                status = result.StartsWith("ok ", StringComparison.Ordinal) || result == "ok" ? "PASS" : "FAIL";
            }
            catch (Exception error) { result = "failed " + error; }
            // Unity Destroy is deferred. Wait for subsequent actual game frames before accepting another request.
            int cleanupFrame = UnityEngine.Time.frameCount;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (UnityEngine.Time.frameCount - cleanupFrame < 3 && timer.Elapsed.TotalSeconds < 10) await Task.Delay(20);
            int framesWaited = UnityEngine.Time.frameCount - cleanupFrame;
            if (framesWaited < 3) cleanupErrors.Add("Deferred destruction did not reach three subsequent game frames");
            string[] remaining = Resources.FindObjectsOfTypeAll<GameObject>().Where(IsReviewObject)
                .Where(o => !previousObjects.Contains(o.GetInstanceID())).Select(o => o.name).OrderBy(n => n).ToArray();
            if (remaining.Length != 0) cleanupErrors.Add("Owned probe objects remain after cleanup: " + string.Join(", ", remaining));
            // Cancellation must never mask a probe's independently reported cleanup failure.
            if (result.IndexOf("cleanup", StringComparison.OrdinalIgnoreCase) >= 0 && result.StartsWith("failed", StringComparison.OrdinalIgnoreCase))
                cleanupErrors.Add("Probe reported cleanup failure: " + result);
            string probeReport = Path.Combine(output, "report.json");
            if (File.Exists(probeReport))
            {
                var evidence = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(probeReport));
                if (evidence["cleanup"]?["passed"]?.Value<bool>() == false ||
                    evidence["mirror_checks"]?["cleanup"]?["passed"]?.Value<bool>() == false ||
                    (evidence["mirror_checks"]?["cleanup_errors"] is JArray mirrorCleanup && mirrorCleanup.Count > 0))
                    cleanupErrors.Add("Probe's structured cleanup evidence is FAIL");
            }
            string roadReport = Path.Combine(output, "road-report.json");
            if ((request.command == "prepared-cancel" || request.command == "bindings-snapshot") && File.Exists(roadReport) &&
                JObject.Parse(File.ReadAllText(roadReport))["cleanup_errors"] is JArray roadCleanup && roadCleanup.Count > 0)
                cleanupErrors.Add("Native lifecycle stage's structured cleanup evidence is FAIL");
            try
            {
                string changed = ValidateReviewRequest(request);
                if (changed != null) cleanupErrors.Add(changed);
            }
            catch (Exception error) { cleanupErrors.Add("Final identity check: " + error.Message); }
            s_Review.ObserveMenu(TestMenuReady(GameManager.instance));
            if (s_Review.Interrupted && cleanupErrors.Count == 0) status = "CANCELLED";
            if (cleanupErrors.Count > 0) status = "FAIL";
            // A failed probe can have incompletely restored render state. Do not quietly reuse it.
            if (status == "FAIL") s_Review.MarkRequiresRestart();
            try
            {
                bool quit = s_Review.Complete(request.request_id, close && status == "PASS");
                var cleanup = new { passed = cleanupErrors.Count == 0, errors = cleanupErrors, frames_waited = framesWaited, remaining_objects = remaining };
                WriteReviewResponse(request, directory, status, result, started, cleanup, quit, output);
                Mod.log.Info("CDLC_REVIEW_END " + request.request_id + " " + status);
                PublishReviewStatus();
                if (quit && TestMenuReady(GameManager.instance)) GameManager.QuitGame();
            }
            catch (Exception error)
            {
                s_Review.MarkRequiresRestart();
                Mod.log.Warn("Review result could not be published: " + error);
                throw;
            }
        }

        static bool IsReviewObject(GameObject o) => o != null &&
            (o.name.StartsWith("CDLC ", StringComparison.Ordinal) || o.name.StartsWith("Bus Driver mirror camera", StringComparison.Ordinal));

        static void PublishReviewStatus()
        {
            if (s_ReviewAssemblyHash == null) return;
            AtomicReviewJson(Path.Combine(Mod.ModDirectory, "review-session.json"), new
            {
                protocol = 1, instance_id = s_ReviewInstance, pid = s_ReviewPid, process_start_utc = s_ReviewStartUtc,
                heartbeat_utc = DateTime.UtcNow.ToString("o"), ready = TestMenuReady(GameManager.instance) && s_ReviewPackages != null,
                busy = s_Review.Busy || s_LegacyBusy, suspended = s_Review.Suspended, needs_restart = s_Review.RequiresRestart,
                stopping = s_Review.Stopping, active_request_id = s_Review.ActiveRequestId,
                assembly_sha256 = s_ReviewAssemblyHash, assembly_mvid = s_ReviewMvid, packages = s_ReviewPackages,
                capabilities = new[] { "verify", "glass", "vehicle", "vehicle-mirrors", "mirror-lod-selection", "vehicle-review-sample", "vehicle-refraction", "vehicle-glass-atlas", "assembly", "prepared-cancel", "bindings-snapshot", "close", "resume" },
                evidence_scope = "Frozen process build and boot-verified package identities. Warm requests do not establish another cold start; a running process has no exit result yet."
            }, true, false);
        }

        static void WriteReviewResponse(ReviewRequest request, string directory, string status, string result, string started,
            object cleanup, bool quitting, string output = null)
        {
            AtomicReviewJson(Path.Combine(directory, "response.json"), new
            {
                protocol = 1, instance_id = s_ReviewInstance, request_id = request.request_id, pid = s_ReviewPid,
                command = request.command, status, result, started_utc = started, finished_utc = DateTime.UtcNow.ToString("o"),
                assembly_sha256 = s_ReviewAssemblyHash, assembly_mvid = s_ReviewMvid, packages = s_ReviewPackages,
                output_directory = output ?? directory, cleanup, process_state = quitting ? "EXIT_REQUESTED" : "RUNNING", exit_code = (int?)null
            }, false);
        }

        // durable: flushed to the disk before the rename (responses, which are evidence). The heartbeat is rewritten every second
        // and a lost one is replaced a second later, so it skips the flush (a disk wait on the main thread).
        static void AtomicReviewJson(string path, object data, bool replace, bool durable = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data, Formatting.Indented));
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(durable);
                }
                if (replace && File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        static string ReviewHash(string path)
        { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return ReviewHex(sha.ComputeHash(stream)); }
        static string ReviewHex(byte[] hash) => BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
