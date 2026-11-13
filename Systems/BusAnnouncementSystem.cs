using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.SceneFlow;
using Game.UI.Localization;
using Game.UI.Menu;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The stop announcements of a shift, from the shift's state alone, and of a ride (the player a passenger of a bus the game drives), from what the ride
    /// system reads of the bus: "Next stop X" when the bus leaves a stop (the target moved on and the doors are shut), "X, please exit through the rear
    /// door" (or "not stopping at X": a shift only) 150 m before it, "X" when the doors open (AnnounceRules says when). When a shift or a ride starts,
    /// every sentence of the line is handed to the synthesiser in the background (the first stops first), so the voices are on disk before they are
    /// needed. Rendering phase: runs every frame, paused game included, and does nothing but compare a few numbers until something is due.
    /// </summary>
    public sealed partial class BusAnnouncementSystem : GameSystemBase
    {
        private const float kSettleSeconds = 2f;   // after a shift or a ride starts: no announcement for the first moments

        private readonly TtsCache m_Tts = new TtsCache();
        private BusAnnouncer m_Announcer;
        private AnnounceSource m_Source;
        private AnnounceState m_Rules;
        private float m_ShiftStart;
        private string m_Terminus = "";
        private string m_TtsKey = "";
        private float m_NextSettings;
        private int m_LogCooldown;

        internal static bool ClearCacheRequested;
        internal static bool InstallPiperRequested;
        private const string kPiperNotice = "CDLC.BusDriver.Piper";

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Announcer = new BusAnnouncer(m_Tts);
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Announcer?.Clear();
            m_Source = default;
        }

        protected override void OnUpdate()
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                if (m_LogCooldown-- <= 0)
                {
                    Mod.log.Warn($"Announcements update failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    m_LogCooldown = 300;
                }
            }
        }

        private void Run()
        {
            BusSettings settings = Mod.Settings;
            float now = UnityEngine.Time.unscaledTime;
            if (ClearCacheRequested)
            {
                ClearCacheRequested = false;
                m_Announcer.Clear();
                m_Tts.ClearAll();
            }

            if (InstallPiperRequested)
            {
                InstallPiperRequested = false;
                if (settings != null && !PiperInstall.Busy)
                {
                    string locale = ActiveLocale();
                    var voices = new List<string> { TtsText.PiperVoice(locale) };
                    if (!voices.Contains("en_US-lessac-medium"))
                        voices.Add("en_US-lessac-medium");
                    Mod.log.Info($"Installing Piper and the voices {string.Join(", ", voices)} into {PiperInstall.Root}.");
                    PiperInstall.Install(voices, settings.NeuralProxy);
                    Notice("CDLC.BusDriver.Piper.Installing");
                }
            }

            string finished = PiperInstall.Finished;
            if (finished != null)
            {
                PiperInstall.Finished = null;
                Notice(finished.StartsWith("Piper voice installed") ? "CDLC.BusDriver.Piper.Done" : "CDLC.BusDriver.Piper.Failed");
                Mod.log.Info(finished);
                m_Tts.Forget();
            }

            if (settings == null || !settings.Announcements || !Core.Available)
            {
                if (m_Announcer.Pending > 0)
                    m_Announcer.Clear();
                m_Source = default;
                return;
            }

            if (now >= m_NextSettings || now < m_NextSettings - 2f)
            {
                m_NextSettings = now + 1f;
                ApplySettings(settings);
            }

            Source source = ReadSource();
            var key = new AnnounceSource(source.Active, source.Ride, Key(source.Line), Key(source.Bus));
            if (AnnounceRules.Switched(key, m_Source))
            {
                if (m_Source.Active)
                    m_Announcer.Clear();
                m_Source = key;
                AnnounceRules.Begin(ref m_Rules, source.Facts);
                m_ShiftStart = now;
                m_Terminus = source.Terminus;
                if (source.Active)
                    Prewarm(settings, source);
            }
            else if (source.Terminus != m_Terminus)
            {
                // The terminus changed during the shift (the timetable's was moved): the sentences naming it, again.
                m_Terminus = source.Terminus;
                Prewarm(settings, source);
            }

            if (source.Active)
                Follow(settings, now, source);
            m_Announcer.Update(now);
        }

        /// <summary>What the announcements follow this frame: the shift of the player on a bus, else the ride of the player in one, else nothing.</summary>
        private struct Source
        {
            internal bool Active, Ride, Skip, DepotLeg;
            internal Unity.Entities.Entity Line, Bus;
            internal string Prefix, Number, Terminus;
            /// <summary>The stop the bus heads for, and the one its doors open at.</summary>
            internal string StopName, AtStopName;
            internal AnnounceFacts Facts;
        }

        private static Source ReadSource()
        {
            if (BusState.InShift && BusState.Bus != Unity.Entities.Entity.Null)
            {
                ShiftState state = BusState.State;
                StopInfo stop = BusState.Target;
                float distance = BusState.DistanceToTarget;
                return new Source
                {
                    Active = true, Skip = BusState.TargetSkip != SkipReason.None, DepotLeg = BusState.DepotLeg, Line = BusState.Line, Bus = BusState.Bus,
                    Prefix = BusState.LinePrefix, Number = BusState.LineNumber, Terminus = BusState.Terminus,
                    StopName = stop.Name,
                    AtStopName = BusState.ZoneIndex >= 0 && BusState.ZoneIndex < BusState.Stops.Count ? BusState.Stops[BusState.ZoneIndex].Name : stop.Name,
                    Facts = AnnounceRules.ShiftFacts(BusState.TargetIndex, stop.Valid, state == ShiftState.Driving, state == ShiftState.DoorsOpening,
                        state == ShiftState.DoorsOpening || state == ShiftState.DoorsOpen, distance),
                };
            }

            if (BusRide.Riding)
            {
                // The game does not tell a ridden bus which stop it passes without stopping, so a ride has no "not stopping at"; the bus holds the stop it boards
                // at as its target until the boarding is over (TransportCarAISystem), so the stop it heads for is the one its doors open at. Whether the
                // bus came out of the depot is not known: the first stop is announced as an ordinary one.
                return new Source
                {
                    Active = true, Ride = true, Line = BusRide.Line, Bus = BusRide.Bus, Prefix = BusRide.LinePrefix, Number = BusRide.LineNumber, Terminus = BusRide.Terminus,
                    StopName = BusRide.Next, AtStopName = BusRide.Next,
                    Facts = AnnounceRules.RideFacts(BusRide.Target, BusRide.HasRoute, BusRide.Next, BusRide.DoorsOpen, BusRide.Approaching, BusRide.Distance),
                };
            }

            return default;
        }

        private static long Key(Unity.Entities.Entity e) => ((long)e.Index << 32) | (uint)e.Version;

        private void ApplySettings(BusSettings settings)
        {
            m_Announcer.Volume = Mathf.Clamp01(settings.AnnounceVolume / 100f);
            m_Announcer.Chime = true;
            m_Announcer.Voice = settings.AnnounceVoice != AnnounceVoice.ChimeOnly;
            string locale = ActiveLocale();
            string key = settings.AnnounceVoice + "|" + locale + "|" + settings.AzureKey + "|" + settings.AzureRegion + "|" + settings.NeuralProxy;
            if (key != m_TtsKey)
            {
                m_TtsKey = key;
                m_Tts.Mode = settings.AnnounceVoice;
                m_Tts.Locale = locale;
                m_Tts.AzureKey = settings.AzureKey ?? "";
                m_Tts.AzureRegion = settings.AzureRegion ?? "";
                m_Tts.Proxy = settings.NeuralProxy ?? "";
                m_Tts.Forget();
            }
        }

        /// <summary>The language the sentences are in: the game's, when this mod has texts for it, else English.</summary>
        private static string ActiveLocale()
        {
            string id = GameManager.instance?.localizationManager?.activeLocaleId ?? "en-US";
            return BusLocale.HasLocale(id) ? id : "en-US";
        }

        private void Follow(BusSettings settings, float now, in Source source)
        {
            AnnounceDue due = AnnounceRules.Step(ref m_Rules, source.Facts, now - m_ShiftStart > kSettleSeconds);
            if ((due & AnnounceDue.Next) != 0)
                Say(AnnounceKind.Next, source.StopName, settings, now, source);
            if ((due & AnnounceDue.Arriving) != 0)
                Say(source.Skip ? AnnounceKind.Skip : AnnounceKind.Arriving, source.StopName, settings, now, source);
            if ((due & AnnounceDue.AtStop) != 0)
                Say(AnnounceKind.AtStop, source.AtStopName, settings, now, source);
        }

        private void Say(AnnounceKind kind, string stopName, BusSettings settings, float now, in Source source)
        {
            string sentence = TtsText.Sentence(kind, stopName, TerminusFor(stopName, source), settings.AnnounceTerminus, BusLocale.Text, LineLabel.Text(source.Prefix, source.Number),
                LineNamed(source));
            m_Announcer.Enqueue(kind, sentence, now);
        }

        /// <summary>The line's label is its own name or words ("高港专线", "快3线"), not a route number with the locale's word.</summary>
        private static bool LineNamed(in Source source) => !string.IsNullOrEmpty(source.Number) && !LineLabel.IsRouteNumber(source.Number);

        /// <summary>
        /// The terminus the sentences for a stop use. Out of the depot, a first stop that is the terminus begins the trip: no
        /// terminus there, so it is announced as an ordinary stop (not "the final stop, everybody off", nor "to X, next stop X").
        /// </summary>
        private static string TerminusFor(string stopName, in Source source)
        {
            return source.DepotLeg && TtsText.IsTerminus(stopName, source.Terminus) ? "" : source.Terminus;
        }

        /// <summary>Every sentence of the line, the next stops first, to the synthesiser's background queue; then the cache trim.</summary>
        private void Prewarm(BusSettings settings, in Source source)
        {
            if (settings.AnnounceVoice == AnnounceVoice.ChimeOnly)
                return;

            string line = LineLabel.Text(source.Prefix, source.Number);
            bool named = LineNamed(source);
            foreach (string name in StopNamesFrom(source))
            {
                string terminus = TerminusFor(name, source);
                m_Tts.Request(TtsText.Sentence(AnnounceKind.Next, name, terminus, settings.AnnounceTerminus, BusLocale.Text, line, named), false);
                m_Tts.Request(TtsText.Sentence(AnnounceKind.Arriving, name, terminus, settings.AnnounceTerminus, BusLocale.Text, line, named), false);
                m_Tts.Request(TtsText.Sentence(AnnounceKind.AtStop, name, terminus, settings.AnnounceTerminus, BusLocale.Text, line, named), false);
                if (terminus != source.Terminus)
                {
                    // ...and as the final stop, for when the bus comes round to it again.
                    m_Tts.Request(TtsText.Sentence(AnnounceKind.Next, name, source.Terminus, settings.AnnounceTerminus, BusLocale.Text, line, named), false);
                    m_Tts.Request(TtsText.Sentence(AnnounceKind.Arriving, name, source.Terminus, settings.AnnounceTerminus, BusLocale.Text, line, named), false);
                    m_Tts.Request(TtsText.Sentence(AnnounceKind.AtStop, name, source.Terminus, settings.AnnounceTerminus, BusLocale.Text, line, named), false);
                }
            }

            m_Tts.Trim(Mathf.Clamp(settings.AnnounceCacheMb, 50, 2000) * 1048576L);
        }

        /// <summary>The names of the line's stops from the one the bus heads for, round the line: the order their voices are made in.</summary>
        private static List<string> StopNamesFrom(in Source source)
        {
            var names = new List<string>();
            if (source.Ride)
            {
                string[] stops = BusRide.StopNames;
                int start = Mathf.Max(0, BusRide.Target);
                for (int n = 0; n < stops.Length; n++)
                {
                    string name = stops[(start + n) % stops.Length];
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }

                return names;
            }

            List<StopInfo> shiftStops = BusState.Stops;
            int first = Mathf.Max(0, BusState.TargetIndex);
            for (int n = 0; n < shiftStops.Count; n++)
            {
                StopInfo stop = shiftStops[(first + n) % shiftStops.Count];
                if (stop.IsStop && !string.IsNullOrEmpty(stop.Name))
                    names.Add(stop.Name);
            }

            return names;
        }

        /// <summary>A notice in the game's notification list (top right); its text is the language files'.</summary>
        private void Notice(string textKey)
        {
            try
            {
                NotificationUISystem notifications = World.GetExistingSystemManaged<NotificationUISystem>();
                notifications?.AddOrUpdateNotification(kPiperNotice,
                    LocalizedString.Id("CDLC.BusDriver.Piper.Title"),
                    LocalizedString.Id(textKey));
            }
            catch (Exception ex)
            {
                Mod.log.Info($"{textKey} ({ex.GetType().Name} while showing the notice)");
            }
        }

        protected override void OnDestroy()
        {
            m_Announcer?.Destroy();
            m_Tts.Shutdown();
            base.OnDestroy();
        }
    }
}
