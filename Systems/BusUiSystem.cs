using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Colossal.Entities;
using Colossal.UI.Binding;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.SceneFlow;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Bindings for the bus panel and the shift HUD (CDLC.BusDriver.mjs), the panel and door keys, and the triggers the panel
    /// sends (start a shift on a line, end it, doors). Everything is passed as small JSON strings so the UI stays a plain
    /// module.
    /// </summary>
    internal sealed partial class BusUiSystem : UISystemBase
    {
        private const string kGroup = "cdlcbusdriver";
        private const float kLinesRefreshSeconds = 1f;

        private ValueBinding<bool> m_Available;
        private ValueBinding<bool> m_PanelVisible;
        private ValueBinding<bool> m_ShowHud;
        private ValueBinding<string> m_Lines;
        private ValueBinding<string> m_Shift;
        private ValueBinding<string> m_Ride, m_RideStops;
        private ValueBinding<string> m_Summary;
        private ValueBinding<string> m_Stops;
        private ValueBinding<string> m_Demand;
        private ValueBinding<string> m_Transfers;
        private ValueBinding<string> m_PanelKey;
        private ValueBinding<string> m_DoorKey;
        private ValueBinding<string> m_LeftKey, m_RightKey;
        private ValueBinding<string> m_Wraps;
        private ValueBinding<bool> m_Immersive;
        private int m_WrapsVersion = -1;
        private float m_NextWraps;
        private string m_WrapsCurrent;
        private EntityQuery m_LineQuery;
        private BusShiftSystem m_ShiftSystem;
        private bool m_Visible;
        private float m_NextLines;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_ShiftSystem = World.GetOrCreateSystemManaged<BusShiftSystem>();
            m_LineQuery = GetEntityQuery(ComponentType.ReadOnly<TransportLine>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            AddBinding(m_Available = new ValueBinding<bool>(kGroup, "Available", false));
            AddBinding(m_PanelVisible = new ValueBinding<bool>(kGroup, "PanelVisible", false));
            AddBinding(m_ShowHud = new ValueBinding<bool>(kGroup, "ShowHud", true));
            AddBinding(m_Lines = new ValueBinding<string>(kGroup, "Lines", "[]"));
            AddBinding(m_Shift = new ValueBinding<string>(kGroup, "Shift", ""));
            AddBinding(m_Summary = new ValueBinding<string>(kGroup, "Summary", ""));
            AddBinding(m_Stops = new ValueBinding<string>(kGroup, "Stops", "[]"));
            AddBinding(m_Demand = new ValueBinding<string>(kGroup, "Demand", "[]"));
            AddBinding(m_Transfers = new ValueBinding<string>(kGroup, "Transfers", "[]"));
            AddBinding(m_PanelKey = new ValueBinding<string>(kGroup, "PanelKey", ""));
            AddBinding(m_DoorKey = new ValueBinding<string>(kGroup, "DoorKey", ""));
            AddBinding(m_LeftKey = new ValueBinding<string>(kGroup, "IndicateLeftKey", ""));
            AddBinding(m_RightKey = new ValueBinding<string>(kGroup, "IndicateRightKey", ""));
            AddBinding(new TriggerBinding(kGroup, "TogglePanel", () => m_Visible = !m_Visible));
            AddBinding(new TriggerBinding<bool>(kGroup, "SetPanelVisible", value => m_Visible = value));
            AddBinding(new TriggerBinding<int, int, bool>(kGroup, "StartShift", StartShift));
            AddBinding(new TriggerBinding(kGroup, "EndShift", () => BusState.Request = ShiftRequest.End));
            AddBinding(m_Ride = new ValueBinding<string>(kGroup, "Ride", ""));
            AddBinding(m_RideStops = new ValueBinding<string>(kGroup, "RideStops", "[]"));
            AddBinding(new TriggerBinding<int, int>(kGroup, "RideLine", RideLine));
            AddBinding(new TriggerBinding(kGroup, "RideNext", () => BusRide.Request = RideRequest.Next));
            AddBinding(new TriggerBinding(kGroup, "LeaveRide", () => BusRide.Request = RideRequest.Leave));
            AddBinding(new TriggerBinding(kGroup, "Doors", () => BusState.Request = ShiftRequest.ToggleDoors));
            AddBinding(new TriggerBinding(kGroup, "ForceClose", () => BusState.Request = ShiftRequest.ForceClose));
            AddBinding(m_Wraps = new ValueBinding<string>(kGroup, "Wraps", ""));
            AddBinding(m_Immersive = new ValueBinding<bool>(kGroup, "Immersive", false));
            AddBinding(new TriggerBinding(kGroup, "ToggleImmersive", () => ToggleImmersive("panel")));
            AddBinding(new TriggerBinding<string>(kGroup, "LoadWrap", name => SetWrap(name)));
            AddBinding(new TriggerBinding(kGroup, "UnloadWrap", () => SetWrap("")));
            AddBinding(new TriggerBinding(kGroup, "NewWrap", () => { string name = BusSkins.NewWrap(); if (name.Length > 0) SetWrap(name); }));
            AddBinding(new TriggerBinding(kGroup, "OpenWraps", () => BusSkins.OpenFolder()));
            AddBinding(new TriggerBinding(kGroup, "RefreshWraps", BusSkins.Changed));
        }

        // Immersive mode on or off (the key, or the panel's button), saved with the settings; the log says which and what it is now.
        private static void ToggleImmersive(string how)
        {
            BusSettings settings = Mod.Settings;
            if (settings == null)
                return;
            settings.Immersive = !settings.Immersive;
            settings.ApplyAndSave();
            BusState.SetMessage(settings.Immersive ? "Msg.ImmersiveOn" : "Msg.ImmersiveOff");
            Mod.log.Info($"Immersive mode {(settings.Immersive ? "on" : "off")} ({how}).");
        }

        // The wrap the bus wears (the option "Body wrap"), saved with the settings.
        private static void SetWrap(string name)
        {
            BusSettings settings = Mod.Settings;
            if (settings == null || settings.BodyWrap == name)
                return;
            settings.BodyWrap = name ?? "";
            settings.ApplyAndSave();
            BusSkins.Changed();
        }

        // The wraps manager's list while the panel is open: when something changed, and every three seconds (new folders,
        // pictures saved from a paint program).
        private void UpdateWraps(float now)
        {
            BusSettings settings = Mod.Settings;
            string current = settings != null ? settings.BodyWrap ?? "" : "";
            if (BusSkins.Version == m_WrapsVersion && current == m_WrapsCurrent && now < m_NextWraps && now > m_NextWraps - 4f)
                return;
            m_WrapsVersion = BusSkins.Version;
            m_WrapsCurrent = current;
            m_NextWraps = now + 3f;
            m_Wraps.Update(BusSkins.ManagerJson(current, settings == null || settings.ShowExterior));
        }

        private static void RideLine(int index, int version)
        {
            BusRide.RequestedLine = new Entity { Index = index, Version = version };
            BusRide.Request = RideRequest.Ride;
        }

        private void StartShift(int index, int version, bool dispatch)
        {
            Entity line = new Entity { Index = index, Version = version };
            BusState.RequestedLine = line;
            BusState.Request = dispatch ? ShiftRequest.Dispatch : ShiftRequest.TakeOver;
        }

        [Preserve]
        private float m_NextKeys;
        private float m_NextShift;
        private float m_NextSummary;
        private bool m_LastIdle = true;

        protected override void OnUpdate()
        {
            try
            {
                UpdatePanel();
            }
            catch (System.Exception ex)
            {
                // A fault here repeats every frame: logged with a count, at most every 30 s.
                m_Faults++;
                float now = UnityEngine.Time.unscaledTime;
                if (m_Faults == 1 || now >= m_NextFaultLog)
                {
                    m_NextFaultLog = now + 30f;
                    Mod.log.Warn($"Bus panel update failed ({m_Faults}x): {ex}");
                }
            }
        }

        private int m_Faults;
        private float m_NextFaultLog;

        private void UpdatePanel()
        {
            bool inGame = GameManager.instance != null && GameManager.instance.gameMode == GameMode.Game;
            bool available = inGame && Core.Available;
            bool onShift = available && BusState.InShift;
            BusInput.SetState(inGame, available && BusState.DrivingBus, onShift);
            BusInput.Poll(out bool togglePanel, out ShiftRequest doorRequest);
            if (togglePanel)
                m_Visible = !m_Visible;
            if (doorRequest != ShiftRequest.None)
                BusState.Request = doorRequest;
            if (BusInput.PollImmersive())
                ToggleImmersive("key");
            if (Mod.Settings != null)
                m_Immersive.Update(Mod.Settings.Immersive);
            if (!inGame)
                m_Visible = false;

            m_Available.Update(available);
            m_PanelVisible.Update(m_Visible);
            if (m_Visible)
                UpdateWraps(UnityEngine.Time.unscaledTime);
            // The core hides the pointer while driving; the open panel needs it.
            if (available)
                Core.SetCursorNeeded(m_Visible);
            m_ShowHud.Update(Mod.Settings == null || Mod.Settings.ShowHud);
            float now = UnityEngine.Time.unscaledTime;
            if (now >= m_NextKeys || now < m_NextKeys - 2f)
            {
                // Key names only change when the player rebinds them.
                m_NextKeys = now + 1f;
                m_PanelKey.Update(BusInput.Display(BusInput.kPanel));
                m_DoorKey.Update(BusInput.Display(BusInput.kDoors));
                m_LeftKey.Update(BusInput.Display(BusInput.kIndicateLeft));
                m_RightKey.Update(BusInput.Display(BusInput.kIndicateRight));
            }

            // The shift display (distances, line strip, prompts) 15 times a second and the summary twice a second: building
            // the JSON every frame allocated for nothing. A shift starting or ending updates at once.
            bool idle = BusState.State == ShiftState.Idle && string.IsNullOrEmpty(BusState.Message);
            if (idle != m_LastIdle || now >= m_NextShift || now < m_NextShift - 1f)
            {
                m_LastIdle = idle;
                m_NextShift = now + 1f / 15f;
                m_Shift.Update(idle ? "" : BuildShiftJson());
                m_Ride.Update(BusRide.Riding ? BuildRideJson() : "");
                m_RideStops.Update(BusRide.Riding ? BusRide.StopsJson : "[]");
            }

            if (now >= m_NextSummary || now < m_NextSummary - 2f)
            {
                m_NextSummary = now + 0.5f;
                m_Summary.Update(BuildSummaryJson());
            }

            m_Stops.Update(BusState.StopNamesJson ?? "[]");
            m_Demand.Update(BusState.InShift ? BusState.DemandJson ?? "[]" : "[]");
            m_Transfers.Update(BusState.TransfersJson ?? "[]");

            if (m_Visible && inGame && now >= m_NextLines)
            {
                m_NextLines = now + kLinesRefreshSeconds;
                m_Lines.Update(BuildLinesJson());
            }
        }

        // ------------------------------------------------------------------ JSON

        private string BuildLinesJson()
        {
            var lines = new List<(int number, string json)>();
            NativeArray<Entity> entities = m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity line = entities[i];
                    if (!EntityManager.TryGetComponent(line, out PrefabRef prefabRef) ||
                        !EntityManager.TryGetComponent(prefabRef.m_Prefab, out TransportLineData data) ||
                        data.m_TransportType != TransportType.Bus || !data.m_PassengerTransport)
                    {
                        continue;
                    }

                    int number = EntityManager.TryGetComponent(line, out RouteNumber routeNumber) ? routeNumber.m_Number : 0;
                    int stops = EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteWaypoint> waypoints) ? waypoints.Length : 0;
                    int vehicles = EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteVehicle> routeVehicles) ? routeVehicles.Length : 0;
                    int ticket = EntityManager.TryGetComponent(line, out TransportLine transportLine) ? transportLine.m_TicketPrice : 0;
                    string color = EntityManager.TryGetComponent(line, out Game.Routes.Color lineColor) ? Hex(lineColor.m_Color) : "#4bc3f1";
                    var json = new StringBuilder("{");
                    Field(json, "index", line.Index, true);
                    Field(json, "version", line.Version);
                    string name = m_ShiftSystem.SafeName(line);
                    LineLabel.Parse(name, number, m_ShiftSystem.PlayerNamed(line), out string prefix, out string label);
                    Field(json, "name", name);
                    Field(json, "number", label);
                    Field(json, "prefix", prefix);
                    Field(json, "prefixColor", LineLabel.Hex(LineLabel.ColorFor(prefix, Mod.Settings?.RoutePrefixColors)));
                    Field(json, "color", color);
                    Field(json, "stops", stops);
                    Field(json, "vehicles", vehicles);
                    Field(json, "ticket", ticket);
                    Field(json, "active", line == BusState.Line);
                    json.Append('}');
                    lines.Add((number, json.ToString()));
                }
            }
            finally
            {
                entities.Dispose();
            }

            lines.Sort((a, b) => a.number.CompareTo(b.number));
            var result = new StringBuilder("[");
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                    result.Append(',');
                result.Append(lines[i].json);
            }

            return result.Append(']').ToString();
        }

        private static string BuildRideJson()
        {
            var json = new StringBuilder("{");
            Field(json, "line", BusRide.LineName, true);
            Field(json, "number", BusRide.LineNumber);
            Field(json, "prefix", BusRide.LinePrefix);
            Field(json, "color", Hex(BusRide.LineColor));
            Field(json, "prefixColor", BusRide.PrefixColour);
            Field(json, "hasRoute", BusRide.HasRoute);
            Field(json, "next", BusRide.Next);
            Field(json, "terminus", BusRide.Terminus);
            Field(json, "nextIndex", BusRide.NextIndex);
            Field(json, "target", BusRide.Target);
            Field(json, "stopCount", BusRide.StopCount);
            Field(json, "progress", BusRide.Progress);
            Field(json, "distance", BusRide.Distance);
            Field(json, "approaching", BusRide.Approaching);
            Field(json, "passengers", BusRide.Passengers);
            Field(json, "capacity", BusRide.Capacity);
            Field(json, "doorsOpen", BusRide.DoorsOpen);
            Field(json, "camera", Core.ActiveCameraName());
            json.Append('}');
            return json.ToString();
        }

        private static string BuildShiftJson()
        {
            StopInfo target = BusState.Target;
            int zone = BusState.ZoneIndex >= 0 && BusState.ZoneIndex < BusState.Stops.Count ? BusState.Stops[BusState.ZoneIndex].Ordinal : -1;
            var json = new StringBuilder("{");
            Field(json, "state", BusState.State.ToString(), true);
            Field(json, "line", BusState.LineName);
            Field(json, "number", BusState.LineNumber);
            Field(json, "prefix", BusState.LinePrefix);
            Field(json, "indicator", BusInput.Indicator);
            Field(json, "prefixColor", LineLabel.Hex(LineLabel.ColorFor(BusState.LinePrefix, Mod.Settings?.RoutePrefixColors)));
            Field(json, "color", Hex(BusState.LineColor));
            Field(json, "next", target.Valid ? target.Name : "");
            Field(json, "terminus", BusState.Terminus);
            Field(json, "nextIndex", target.Ordinal + 1);
            Field(json, "target", BusState.TargetIndex >= 0 ? target.Ordinal : -1);
            Field(json, "zone", zone);
            Field(json, "progress", (int)Math.Round(BusState.Progress * 100f));
            Field(json, "onRoute", BusState.OnGuide);
            Field(json, "approaching", BusState.DistanceToTarget < 150f);
            Field(json, "zoneMove", (int)Math.Round(BusState.TargetZoneMove));
            Field(json, "zonePast", BusState.TargetZonePast);
            Field(json, "lateral", (int)Math.Round(Math.Abs(BusState.TargetLateral) * 10f));
            Field(json, "stopCount", BusState.RealStops);
            Field(json, "distance", (int)(Math.Round(BusState.DistanceToTarget / 5f) * 5f));
            Field(json, "bearing", (int)(Math.Round(BusState.GuideBearing / 5f) * 5f));
            Field(json, "offRoute", BusState.OffRoute);
            Field(json, "offRouteDistance", (int)Math.Round(BusState.OffRouteDistance));
            Field(json, "turn", BusState.TurnDirection);
            Field(json, "turnDistance", (int)(Math.Round(BusState.TurnDistance / 10f) * 10f));
            Field(json, "kerbGap", (int)Math.Round(BusState.KerbGap * 10f));
            Field(json, "stopped", BusState.Stopped);
            Field(json, "inZone", BusState.ZoneIndex >= 0);
            Field(json, "zoneIsTarget", BusState.ZoneIndex >= 0 && BusState.ZoneIndex == BusState.TargetIndex);
            Field(json, "zoneName", BusState.ZoneIndex >= 0 && BusState.ZoneIndex < BusState.Stops.Count ? BusState.Stops[BusState.ZoneIndex].Name : "");
            Field(json, "canOpen", BusState.CanOpen);
            Field(json, "doorsOpen", BusState.DoorsOpen);
            Field(json, "passengers", BusState.Passengers);
            Field(json, "capacity", BusState.Capacity);
            Field(json, "waiting", BusState.Waiting);
            Field(json, "alighting", BusState.Alighting);
            Field(json, "skip", (int)BusState.TargetSkip);
            Field(json, "boarded", BusState.Boarded);
            Field(json, "alighted", BusState.Alighted);
            Field(json, "stopBoarded", BusState.StopBoarded);
            Field(json, "stopAlighted", BusState.StopAlighted);
            Field(json, "toBoard", BusState.ToBoard);
            Field(json, "leftBehind", BusState.LeftBehind);
            Field(json, "fares", BusState.Fares);
            Field(json, "ticket", BusState.TicketPrice);
            Field(json, "served", BusState.Served);
            Field(json, "skipped", BusState.Skipped);
            Field(json, "missed", BusState.Missed);
            Field(json, "message", BusState.Message);
            Field(json, "messageArg", BusState.MessageArg);
            Field(json, "messageAge", (int)Math.Min(999, (UnityEngine.Time.frameCount - (int)BusState.MessageFrame) / 30));
            return json.Append('}').ToString();
        }

        private static string BuildSummaryJson()
        {
            if (!BusSummary.Has)
                return "";

            var json = new StringBuilder("{");
            Field(json, "line", BusSummary.LineName, true);
            Field(json, "served", BusSummary.Served);
            Field(json, "boarded", BusSummary.Boarded);
            Field(json, "alighted", BusSummary.Alighted);
            Field(json, "skipped", BusSummary.Skipped);
            Field(json, "missed", BusSummary.Missed);
            Field(json, "fares", BusSummary.Fares);
            return json.Append('}').ToString();
        }

        private static string Hex(UnityEngine.Color32 color) => $"#{color.r:x2}{color.g:x2}{color.b:x2}";

        // The typed fields write exactly what the object one writes for that type, without boxing the value (the shift's JSON is
        // built 15 times a second). Any other type still goes through the object one (its ToString, quoted).
        private static void Field(StringBuilder json, string name, bool value, bool first = false) =>
            Name(json, name, first).Append(value ? "true" : "false");

        private static void Field(StringBuilder json, string name, int value, bool first = false) =>
            Name(json, name, first).Append(value.ToString(CultureInfo.InvariantCulture));

        private static void Field(StringBuilder json, string name, long value, bool first = false) =>
            Name(json, name, first).Append(value.ToString(CultureInfo.InvariantCulture));

        private static void Field(StringBuilder json, string name, string value, bool first = false)
        {
            Name(json, name, first);
            if (value == null)
                json.Append("null");
            else
                Quoted(json, value);
        }

        private static void Field(StringBuilder json, string name, object value, bool first = false)
        {
            Name(json, name, first);
            switch (value)
            {
                case null:
                    json.Append("null");
                    break;
                case bool b:
                    json.Append(b ? "true" : "false");
                    break;
                case int i:
                    json.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    json.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    Quoted(json, value.ToString());
                    break;
            }
        }

        private static StringBuilder Name(StringBuilder json, string name, bool first)
        {
            if (!first)
                json.Append(',');
            return json.Append('"').Append(name).Append("\":");
        }

        private static void Quoted(StringBuilder json, string text)
        {
            json.Append('"');
            foreach (char c in text)
            {
                if (c == '"' || c == '\\')
                    json.Append('\\').Append(c);
                else if (c < ' ')
                    json.Append("\\u").Append(((int)c).ToString("x4"));
                else
                    json.Append(c);
            }

            json.Append('"');
        }
    }
}
