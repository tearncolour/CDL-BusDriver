const api = window["cs2/api"];
const ui = window["cs2/ui"];
const l10n = window["cs2/l10n"];
const React = window.React;
const h = React.createElement;

const group = "cdlcbusdriver";
const keyPrefix = "CDLC.BusDriver.";
const busIcon = "Media/Game/Icons/Bus.svg";
let lastToggle = 0;

// ------------------------------------------------------------------ bindings

const available = api.bindValue(group, "Available", false);
const panelVisible = api.bindValue(group, "PanelVisible", false);
const showHud = api.bindValue(group, "ShowHud", true);
const linesJson = api.bindValue(group, "Lines", "[]");
const shiftJson = api.bindValue(group, "Shift", "");
const rideJson = api.bindValue(group, "Ride", "");
const rideStopsJson = api.bindValue(group, "RideStops", "[]");
const summaryJson = api.bindValue(group, "Summary", "");
const stopsJson = api.bindValue(group, "Stops", "[]");
const demandJson = api.bindValue(group, "Demand", "[]");
const transfersJson = api.bindValue(group, "Transfers", "[]");
const panelKey = api.bindValue(group, "PanelKey", "");
const doorKey = api.bindValue(group, "DoorKey", "");
const leftKey = api.bindValue(group, "IndicateLeftKey", "");
const rightKey = api.bindValue(group, "IndicateRightKey", "");
const wrapsJson = api.bindValue(group, "Wraps", "");
const immersiveOn = api.bindValue(group, "Immersive", false);

// ------------------------------------------------------------------ helpers

function trigger(name, ...args) {
    api.trigger(group, name, ...args);
}

function playSelectSound() {
    try {
        api.trigger("audio", "playSound", "select-item", 1);
    } catch (error) {
    }
}

function parse(json, fallback) {
    if (!json) {
        return fallback;
    }
    try {
        return JSON.parse(json);
    } catch (error) {
        return fallback;
    }
}

function format(text, args) {
    return `${text}`.replace(/\{(\d+)\}/g, (match, index) => {
        const value = args[Number(index)];
        return value === undefined || value === null ? match : `${value}`;
    });
}

// Every visible text comes from the game's localization system (lang/*.json or any language pack); this script holds ids and no text. An id no language has shows as the id.
function useText() {
    const { translate } = l10n.useLocalization();
    const t = (id, ...args) => format(translate(keyPrefix + id) || id, args);
    // Money the way the game writes it (Common.VALUE_MONEY, e.g. "¢1,234").
    const money = value => {
        const pattern = translate("Common.VALUE_MONEY") || "{SIGN}{VALUE}";
        const amount = Math.abs(Math.round(Number(value) || 0)).toString().replace(/\B(?=(\d{3})+(?!\d))/g, ",");
        return pattern.replace("{SIGN}", Number(value) < 0 ? "-" : "").replace("{VALUE}", amount);
    };
    return { t, money };
}

function KeyCombo({ combo }) {
    const parts = `${combo || ""}`.split("+").map(part => part.trim()).filter(part => part.length > 0);
    const children = [];
    parts.forEach((part, index) => {
        if (index > 0) {
            children.push(h("div", { key: `p${index}`, className: "cdlcbus-key-plus" }, "+"));
        }
        children.push(h("div", { key: `k${index}`, className: "cdlcbus-key" }, part));
    });
    return h("div", { className: "cdlcbus-keys" }, children.length > 0 ? children : h("div", { className: "cdlcbus-key" }, "–"));
}

// Text on a colour: white, or dark ink when white would be hard to read (WCAG contrast: the better of the two, white
// preferred unless the dark ink reads clearly better, as on yellow, light green or light blue).
const kDarkInk = "#16202a";
function luminance(color) {
    const m = /^#?([0-9a-f]{6})/i.exec(`${color || ""}`);
    if (!m) {
        return 0.3;
    }
    const n = parseInt(m[1], 16);
    const lin = c => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
    return 0.2126 * lin((n >> 16) & 255) + 0.7152 * lin((n >> 8) & 255) + 0.0722 * lin(n & 255);
}
function isLight(color) {
    const l = luminance(color);
    const white = 1.05 / (l + 0.05), dark = (l + 0.05) / (luminance(kDarkInk) + 0.05);
    return dark > white * 1.15;
}
function inkFor(color) {
    return isLight(color) ? kDarkInk : "#ffffff";
}

// The line's label in its colour (a transit map's pill): the whole label, prefix and number, in white or dark ink.
function LineBadge({ color, number, prefix, large }) {
    const bg = color || "#4bc3f1";
    return h("div", { className: `${large ? "cdlcbus-badge cdlcbus-badge-large" : "cdlcbus-badge"}${isLight(bg) ? " cdlcbus-on-light" : ""}`, style: { backgroundColor: bg, color: inkFor(bg) } },
        `${prefix || ""}${number || ""}`);
}

// The route on the HUD's line sign: the number big in the sign's ink, the prefix (K, BRT, 夜 ...) before it as a tag in its
// colour (the LED sign's prefix colour), "路" after a route number in Chinese (UI.RouteSuffix; not after a line's own name or "3线").
function RouteNumber({ prefix, prefixColor, number }) {
    const { t } = useText();
    const tag = prefixColor || "#ff3020";
    const suffix = /^[0-9]{1,4}[A-Za-z]?$/.test(number || "") ? t("UI.RouteSuffix") : "";
    return h("div", { className: "cdlcbus-row cdlcbus-route" },
        prefix ? h("div", { className: "cdlcbus-route-prefix", style: { backgroundColor: tag, color: inkFor(tag) } }, prefix) : null,
        number ? h("div", { className: "cdlcbus-route-number" }, number) : null,
        suffix ? h("div", { className: "cdlcbus-route-suffix" }, suffix) : null,
        !prefix && !number ? h("div", { className: "cdlcbus-route-number" }, "?") : null);
}

function Button({ label, onSelect, kind }) {
    const click = () => {
        playSelectSound();
        onSelect();
    };
    return h("div", { className: `cdlcbus-button cdlcbus-button-${kind || "normal"}`, onClick: click }, label);
}

// ------------------------------------------------------------------ panel

function LineRow({ line }) {
    const { t, money } = useText();
    return h("div", { className: line.active ? "cdlcbus-line cdlcbus-line-active" : "cdlcbus-line" },
        h("div", { className: "cdlcbus-row" },
            h(LineBadge, { color: line.color, number: line.number, prefix: line.prefix }),
            h("div", { className: "cdlcbus-col cdlcbus-grow", style: { marginLeft: "10rem" } },
                h("div", { className: "cdlcbus-line-name" }, line.name || t("UI.LineNumber", line.number)),
                h("div", { className: "cdlcbus-dim" }, t("UI.LineStats", line.stops, line.vehicles, money(line.ticket))))),
        h("div", { className: "cdlcbus-row cdlcbus-line-actions" },
            h(Button, { label: t("UI.Dispatch"), kind: "primary", onSelect: () => trigger("StartShift", line.index, line.version, true) }),
            h(Button, { label: t("UI.TakeOver"), onSelect: () => trigger("StartShift", line.index, line.version, false) }),
            h(Button, { label: t("UI.Ride"), onSelect: () => trigger("RideLine", line.index, line.version) })));
}

function Stat({ label, value }) {
    return h("div", { className: "cdlcbus-stat" },
        h("div", { className: "cdlcbus-stat-label" }, label),
        h("div", { className: "cdlcbus-stat-value" }, value));
}

function Progress({ index, count }) {
    const dots = [];
    const shown = Math.min(count, 24);
    for (let i = 1; i <= shown; i++) {
        const done = i < index;
        const current = i === index;
        dots.push(h("div", { key: `d${i}`, className: current ? "cdlcbus-dot cdlcbus-dot-current" : (done ? "cdlcbus-dot cdlcbus-dot-done" : "cdlcbus-dot") }));
    }
    return h("div", { className: "cdlcbus-row cdlcbus-progress" }, dots);
}

function ShiftView({ shift }) {
    const { t, money } = useText();
    const immersive = api.useValue(immersiveOn);
    const waitingForBus = shift.state === "Dispatching" || shift.state === "TakingOver";
    if (waitingForBus) {
        return h("div", { className: "cdlcbus-col cdlcbus-section" },
            h("div", { className: "cdlcbus-row" },
                h(LineBadge, { color: shift.color, number: shift.number, prefix: shift.prefix }),
                h("div", { className: "cdlcbus-line-name", style: { marginLeft: "10rem" } }, shift.line)),
            h("div", { className: "cdlcbus-wait" }, shift.state === "Dispatching" ? t("UI.Dispatching") : t("UI.TakingOver")),
            h(Button, { label: t("UI.Cancel"), onSelect: () => trigger("EndShift") }));
    }

    const doors = shift.doorsOpen ? t("UI.DoorsOpenState") : t("UI.DoorsClosed");
    return h("div", { className: "cdlcbus-col cdlcbus-section" },
        h("div", { className: "cdlcbus-row" },
            h(LineBadge, { color: shift.color, number: shift.number, prefix: shift.prefix, large: true }),
            h("div", { className: "cdlcbus-col cdlcbus-grow", style: { marginLeft: "12rem" } },
                h("div", { className: "cdlcbus-line-name" }, shift.line),
                h("div", { className: "cdlcbus-dim" }, t("UI.StopProgress", shift.nextIndex, shift.stopCount)))),
        h("div", { className: "cdlcbus-next" },
            h("div", { className: "cdlcbus-next-label" }, t("UI.NextStop")),
            h("div", { className: "cdlcbus-row" },
                h("div", { className: "cdlcbus-next-name cdlcbus-grow" }, shift.next || "–"),
                h("div", { className: "cdlcbus-next-distance" }, t("UI.Distance", shift.distance)))),
        h(Progress, { index: shift.nextIndex, count: shift.stopCount }),
        h("div", { className: "cdlcbus-stats" },
            h(Stat, { label: t("UI.Passengers"), value: `${shift.passengers} / ${shift.capacity || "–"}` }),
            h(Stat, { label: t("UI.WaitingAlighting"), value: `${shift.waiting} / ${shift.alighting || 0}` }),
            h(Stat, { label: t("UI.Served"), value: `${shift.served}` }),
            h(Stat, { label: t("UI.OnOff"), value: `${shift.boarded} / ${shift.alighted}` }),
            h(Stat, { label: t("UI.Fares"), value: money(shift.fares) }),
            h(Stat, { label: t("UI.Doors"), value: doors })),
        shift.skipped > 0 || shift.missed > 0 ? h("div", { className: "cdlcbus-dim", style: { marginTop: "6rem" } }, t("UI.SkippedMissed", shift.skipped || 0, shift.missed || 0)) : null,
        h("div", { className: "cdlcbus-note" }, t("UI.FareNote")),
        h(Button, { label: immersive ? t("UI.ImmersiveOn") : t("UI.ImmersiveOff"), onSelect: () => trigger("ToggleImmersive") }),
        h(Button, { label: t("UI.EndShift"), kind: "stop", onSelect: () => trigger("EndShift") }));
}

function RideView({ ride }) {
    const { t } = useText();
    const camera = ride.camera || t("UI.RideDriverSeat");
    return h("div", { className: "cdlcbus-col cdlcbus-section" },
        h("div", { className: "cdlcbus-row" },
            h(LineBadge, { color: ride.color, number: ride.number, prefix: ride.prefix, large: true }),
            h("div", { className: "cdlcbus-col cdlcbus-grow", style: { marginLeft: "12rem" } },
                h("div", { className: "cdlcbus-line-name" }, ride.line),
                h("div", { className: "cdlcbus-dim" }, t("UI.RideRiding")))),
        h("div", { className: "cdlcbus-stats" },
            h(Stat, { label: t("UI.Passengers"), value: `${ride.passengers} / ${ride.capacity || "–"}` }),
            h(Stat, { label: t("UI.Doors"), value: ride.doorsOpen ? t("UI.DoorsOpenState") : t("UI.DoorsClosed") }),
            h(Stat, { label: t("UI.RideCamera"), value: camera })),
        h("div", { className: "cdlcbus-note" }, t("UI.RideKeys")),
        h(Button, { label: t("UI.RideNext"), onSelect: () => trigger("RideNext") }),
        h(Button, { label: t("UI.RideLeave"), kind: "stop", onSelect: () => trigger("LeaveRide") }));
}

function LinesView() {
    const { t, money } = useText();
    const lines = parse(api.useValue(linesJson), []);
    const summary = parse(api.useValue(summaryJson), null);
    return h("div", { className: "cdlcbus-col" },
        h("div", { className: "cdlcbus-section-title" }, t("UI.Lines")),
        lines.length === 0
            ? h("div", { className: "cdlcbus-note", style: { margin: "4rem 12rem 10rem 12rem" } }, t("UI.NoLines"))
            : h("div", { className: "cdlcbus-col cdlcbus-lines" }, lines.map(line => h(LineRow, { key: `${line.index}:${line.version}`, line }))),
        h("div", { className: "cdlcbus-note", style: { margin: "6rem 12rem 4rem 12rem" } }, t("UI.DispatchHint")),
        h("div", { className: "cdlcbus-note", style: { margin: "0 12rem 4rem 12rem" } }, t("UI.RideHint")),
        summary ? h("div", { className: "cdlcbus-summary" },
            h("div", { className: "cdlcbus-section-title", style: { padding: "0 0 4rem 0" } }, t("UI.LastShift")),
            h("div", null, t("UI.Summary", summary.line, summary.served, summary.boarded, summary.alighted, money(summary.fares))),
            summary.skipped > 0 || summary.missed > 0 ? h("div", { className: "cdlcbus-dim" }, t("UI.SkippedMissed", summary.skipped || 0, summary.missed || 0)) : null) : null);
}

function BusDriverPanel() {
    const { t } = useText();
    const visible = api.useValue(panelVisible);
    const ok = api.useValue(available);
    const shift = parse(api.useValue(shiftJson), null);
    const ride = parse(api.useValue(rideJson), null);
    // every hook before the early return: React needs the same hooks in every render (a hook after it broke the whole UI)
    const [tab, setTab] = React.useState("lines");
    if (!visible) {
        return null;
    }

    const inShift = shift && shift.state && shift.state !== "Idle";
    let body;
    if (!ok) {
        body = h("div", { className: "cdlcbus-note", style: { margin: "12rem" } }, t("UI.NeedDrivingMod"));
    } else {
        const tabs = h("div", { className: "cdlcbus-row cdlcbus-tabs" },
            h(Tab, { label: inShift ? t("UI.TabShift") : ride ? t("UI.TabRide") : t("UI.TabLines"), selected: tab !== "wraps", onSelect: () => setTab("lines") }),
            h(Tab, { label: t("UI.TabWraps"), selected: tab === "wraps", onSelect: () => setTab("wraps") }));
        body = h("div", { className: "cdlcbus-col" }, tabs, tab === "wraps" ? h(WrapsView) : (inShift ? h(ShiftView, { shift }) : ride ? h(RideView, { ride }) : h(LinesView)));
    }

    return h(ui.Panel, {
        draggable: true,
        initialPosition: { x: 0.76, y: 0.14 },
        header: t("UI.Title"),
        onClose: () => trigger("SetPanelVisible", false),
        style: { width: "380rem", maxHeight: "760rem" }
    }, h("div", { className: "cdlcbus-col cdlcbus-panel" }, body, h(MessageLine, { shift })));
}

function Tab({ label, selected, onSelect }) {
    const click = () => {
        playSelectSound();
        onSelect();
    };
    return h("div", { className: selected ? "cdlcbus-tab cdlcbus-tab-selected" : "cdlcbus-tab", onClick: click }, label);
}

// ------------------------------------------------------------------ wraps manager

// The faces of a wrap (bit per face, the order of BusSkins.kFiles) as chips: lit when the wrap has a picture for it.
const wrapFaces = ["UI.WrapLeft", "UI.WrapRight", "UI.WrapFront", "UI.WrapRear", "UI.WrapRoof"];

// A wrap's problem: an id of this mod (its arguments after "|") is translated; any other text is the file's own error and shown as it is.
function wrapError(t, error) {
    if (!error.startsWith("Wrap.Err.")) {
        return error;
    }

    const [id, ...args] = error.split("|");
    return t(id, ...args);
}

function WrapRow({ item, current }) {
    const { t } = useText();
    const loaded = item.name === current;
    const name = item.name === "template" ? t("UI.WrapTemplate") : item.name;
    // a side without its picture takes the other side's
    const sideShared = (item.faces & 3) !== 0;
    return h("div", { className: loaded ? "cdlcbus-wrap cdlcbus-wrap-loaded" : (item.error ? "cdlcbus-wrap cdlcbus-wrap-bad" : "cdlcbus-wrap") },
        h("div", { className: "cdlcbus-row" },
            item.thumb ? h("img", { className: "cdlcbus-wrap-thumb", src: item.thumb }) : h("div", { className: "cdlcbus-wrap-thumb" }),
            h("div", { className: "cdlcbus-col cdlcbus-grow", style: { marginLeft: "8rem" } },
                h("div", { className: "cdlcbus-wrap-name" }, name),
                item.error ? h("div", { className: "cdlcbus-wrap-error", title: wrapError(t, item.error) }, wrapError(t, item.error)) : null,
                h("div", { className: "cdlcbus-row", style: { marginTop: "3rem" } },
                    wrapFaces.map((face, i) => {
                        const own = (item.faces & (1 << i)) !== 0;
                        const shared = !own && i < 2 && sideShared;
                        return h("div", { key: `f${i}`, className: own ? "cdlcbus-face cdlcbus-face-on" : (shared ? "cdlcbus-face cdlcbus-face-shared" : "cdlcbus-face"),
                            title: item.sizes && item.sizes[i] ? item.sizes[i] : "" }, t(face));
                    })))),
        h("div", { className: "cdlcbus-row cdlcbus-line-actions" },
            loaded
                ? h(Button, { label: t("UI.WrapUnload"), onSelect: () => trigger("UnloadWrap") })
                : (item.error ? null : h(Button, { label: t("UI.WrapLoad"), kind: "primary", onSelect: () => trigger("LoadWrap", item.name) })),
            h(Button, { label: t("UI.WrapFolder"), onSelect: () => trigger("OpenWraps") })));
}

function WrapsView() {
    const { t } = useText();
    const wraps = parse(api.useValue(wrapsJson), null);
    const items = wraps && Array.isArray(wraps.items) ? wraps.items : [];
    const current = wraps ? wraps.current || "" : "";
    const own = items.filter(item => !item.template);
    const templates = items.filter(item => item.template);
    const model = wraps && wraps.model ? wraps.model : "";
    return h("div", { className: "cdlcbus-col" },
        h("div", { className: "cdlcbus-wrap-current" }, t("UI.WrapModel", model)),
        h("div", { className: "cdlcbus-row cdlcbus-wrap-current" },
            h("div", { className: "cdlcbus-grow" }, current ? t("UI.WrapCurrent", current === "template" ? t("UI.WrapTemplate") : current) : t("UI.WrapNoneLoaded")),
            current ? h(Button, { label: t("UI.WrapUnload"), onSelect: () => trigger("UnloadWrap") }) : null),
        wraps && wraps.exterior === false ? h("div", { className: "cdlcbus-note", style: { margin: "0 12rem 6rem 12rem", color: "#ffd34d" } }, t("UI.WrapNeedsExterior")) : null,
        h("div", { className: "cdlcbus-col cdlcbus-lines" },
            own.length === 0 ? h("div", { className: "cdlcbus-note", style: { margin: "4rem 12rem" } }, t("UI.WrapNoWraps")) : null,
            own.map(item => h(WrapRow, { key: item.name, item, current })),
            templates.map(item => h(WrapRow, { key: item.name, item, current }))),
        h("div", { className: "cdlcbus-row cdlcbus-line-actions", style: { margin: "6rem 12rem 0 12rem" } },
            h(Button, { label: t("UI.WrapNew"), kind: "primary", onSelect: () => trigger("NewWrap") }),
            h(Button, { label: t("UI.WrapOpenRoot"), onSelect: () => trigger("OpenWraps") }),
            h(Button, { label: t("UI.WrapRefresh"), onSelect: () => trigger("RefreshWraps") })),
        h("div", { className: "cdlcbus-note", style: { margin: "6rem 12rem 4rem 12rem" } },
            t("UI.WrapHint")));
}

function MessageLine({ shift }) {
    const { t } = useText();
    if (!shift || !shift.message || shift.messageAge > 6) {
        return null;
    }
    const args = `${shift.messageArg || ""}`.split("|");
    return h("div", { className: "cdlcbus-message" }, t(shift.message, ...args));
}

function BusDriverButton() {
    const { t } = useText();
    const visible = api.useValue(panelVisible);
    const key = api.useValue(panelKey);
    const title = t("UI.Title");
    const toggle = () => {
        const now = Date.now();
        if (now - lastToggle < 180) {
            return;
        }
        lastToggle = now;
        trigger("TogglePanel");
    };
    return h(ui.Tooltip, {
        tooltip: h("div", null,
            h("div", { style: { fontWeight: "700" } }, key ? `${title} (${key})` : title),
            h("div", null, t("UI.ToolbarTooltip")))
    }, h(ui.Button, { src: busIcon, selected: !!visible, variant: "floating", onSelect: toggle, title }));
}

// ------------------------------------------------------------------ shift HUD (top centre)

// The turn the prompt shows (for its hysteresis).
const turnShown = { turn: 0 };

// What the driver should do now: open the doors, stop in the zone, or drive on.
function usePrompt(shift) {
    const { t } = useText();
    const key = api.useValue(doorKey);
    const left = api.useValue(leftKey), right = api.useValue(rightKey);
    if (shift.state === "DoorsOpening") {
        return { tone: "open", key: null, text: t("Prompt.Opening") };
    }
    if (shift.state === "DoorsClosing") {
        return { tone: "open", key: null, text: t("Prompt.Closing") };
    }
    if (shift.state === "DoorsOpen") {
        return { tone: "open", key, text: t("Prompt.DoorsOpen", shift.stopBoarded, shift.stopAlighted, key) };
    }
    if (shift.canOpen) {
        return { tone: "go", key, text: shift.zoneIsTarget ? t("Prompt.Open", key) : `${t("Prompt.Open", key)} · ${t("Prompt.OtherStop", shift.zoneName)}` };
    }
    if (shift.inZone) {
        return { tone: "near", key: null, text: t("Prompt.ComeToStop") };
    }
    // Nobody gets on or off at the next stop: it may be skipped (drive past), or served anyway.
    const skip = shift.skip > 0;
    const skipText = shift.skip === 2
        ? t("Prompt.SkipFull", shift.next || "–")
        : t("Prompt.Skip", shift.next || "–");
    const docking = text => ({ tone: skip ? "skip" : "near", key: null, text: skip ? `${t("Prompt.SkipShort")} · ${text}` : text });
    // Docking at the next stop: into the zone lengthwise (the bus may stand either way round: zoneMove is in its own terms,
    // + drive on, - reverse; there is no stop line), then closer to the kerb.
    const move = Number.isFinite(shift.zoneMove) ? shift.zoneMove : 0;
    const besideZone = Number.isFinite(shift.zoneMove) && Math.abs(move) < 25 && (Number(shift.lateral) || 0) < 80;
    if (shift.target >= 0 && (shift.distance < 60 || besideZone)) {
        if (shift.zonePast && skip) {
            return { tone: "skip", key: null, text: skipText };
        }
        if (move >= 1) {
            return docking(t("Prompt.IntoZone", move));
        }
        if (move <= -1) {
            return docking(shift.zonePast
                ? t("Prompt.PastZone", -move)
                : t("Prompt.BackIntoZone", -move));
        }
        const gap = (Number(shift.kerbGap) || 0) / 10;
        if (gap > 0.3) {
            return docking(t("Prompt.CloserToKerbBy", gap.toFixed(1)));
        }
        if (gap < -1.8) {
            return docking(t("Prompt.AwayFromKerb", (-gap).toFixed(1)));
        }
        return docking(t("Prompt.CloserToKerb"));
    }
    if (shift.offRoute) {
        return { tone: "near", key: null, text: t("Prompt.OffRoute", shift.offRouteDistance) };
    }
    if (skip && shift.distance < 250) {
        return { tone: "skip", key: null, text: skipText };
    }
    const drive = t("Prompt.Drive", shift.next || "–");
    if (shift.turn && shift.turnDistance <= (turnShown.turn === shift.turn ? 230 : 200)) {
        turnShown.turn = shift.turn;
        const metres = Math.max(10, shift.turnDistance);
        const turn = shift.turn === 2
            ? t("Prompt.UTurn", metres)
            : (shift.turn < 0 ? t("Prompt.TurnLeft", metres) : t("Prompt.TurnRight", metres));
        // the indicator key for the turn while it is not on yet (the bus never indicates on its own)
        const want = shift.turn === 2 ? 0 : (shift.turn < 0 ? -1 : 1);
        const hint = want !== 0 && shift.indicator !== want ? ` · ${t("Prompt.Indicate", want < 0 ? left : right)}` : "";
        return { tone: "info", key: null, text: `${turn}${hint} · ${drive}` };
    }
    turnShown.turn = 0;
    return { tone: "info", key: null, text: drive };
}

function Arrow({ bearing, color }) {
    return h("div", { className: "cdlcbus-arrow", style: { transform: `rotate(${bearing}deg)` } },
        h("svg", { width: 22, height: 22, viewBox: "0 0 20 20" }, h("path", { d: "M10 1 L17 18 L10 13 L3 18 Z", fill: color || "#ffd34d" })));
}

// Which announcement applies: at the stop, arriving soon, or the next stop.
function announcement(shift) {
    const atStop = shift.zone >= 0 && shift.zone === shift.target && (shift.stopped || shift.doorsOpen);
    if (atStop) {
        return { kind: "arrived", id: "UI.AnnArrived", color: "#8bdb46" };
    }
    if (shift.skip > 0) {
        return { kind: "skip", id: "UI.AnnSkippable", color: "#c7d6e0" };
    }
    if (shift.approaching) {
        return { kind: "arriving", id: "UI.AnnArriving", color: "#ffd34d" };
    }
    return { kind: "next", id: "UI.AnnNext", color: "#9ee2fc" };
}

// Transfer badges: the other lines calling at a stop (number on the line colour; the transport type as a small icon).
function TransferBadges({ lines, compact }) {
    if (!Array.isArray(lines) || lines.length === 0) {
        return null;
    }
    const shown = lines.slice(0, compact ? 3 : 4);
    return h("div", { className: compact ? "cdlcbus-transfers cdlcbus-transfers-compact" : "cdlcbus-transfers" },
        shown.map((line, i) => h("div", { key: `x${i}`, className: "cdlcbus-transfer", style: { backgroundColor: line[1] || "#4bc3f1", color: inkFor(line[1] || "#4bc3f1"), borderColor: isLight(line[1] || "#4bc3f1") ? "rgba(22, 32, 42, 0.5)" : null } },
            line[2] && line[2] !== "Bus" ? h("img", { className: "cdlcbus-transfer-icon", src: `Media/Game/Icons/${line[2]}.svg` }) : null,
            h("div", { className: "cdlcbus-transfer-number" }, line[0] || "?"))),
        lines.length > shown.length ? h("div", { className: "cdlcbus-transfer cdlcbus-transfer-more" }, `+${lines.length - shown.length}`) : null);
}

// Line strip after a real bus's passenger display: the previous stop, the next stop as a card (with its transfers) and the
// stops after it on a track with direction marks; the bus marker runs between the previous and the next stop.
function LineStrip({ shift, names, demand, transfers, atStop }) {
    const { t } = useText();
    const count = Math.max(names.length, shift.stopCount || 0);
    if (count < 2 || shift.target < 0) {
        return null;
    }

    const slots = Math.min(5, count);
    const before = count > slots ? 1 : Math.min(1, shift.target);
    const start = count > slots ? shift.target - before : 0;
    const items = [];
    for (let k = 0; k < slots; k++) {
        const index = ((start + k) % count + count) % count;
        items.push({ index, x: slots === 1 ? 50 : 8 + (k / (slots - 1)) * 84 });
    }

    const targetSlot = items.findIndex(item => item.index === shift.target);
    const targetX = targetSlot >= 0 ? items[targetSlot].x : 50;
    const prevX = targetSlot > 0 ? items[targetSlot - 1].x : 0;
    const progress = Math.max(0, Math.min(1, (shift.progress || 0) / 100));
    const busX = atStop ? targetX : prevX + (targetX - prevX) * progress;
    const color = shift.color || "#4bc3f1";
    const name = index => names[index] || t("UI.StopN", index + 1);
    const header = atStop ? t("UI.AnnArrived") : (shift.approaching ? t("UI.AnnArriving") : t("UI.AnnNext"));

    const marks = [];
    for (let k = 0; k < items.length - 1; k++) {
        for (let m = 1; m <= 2; m++) {
            const x = items[k].x + (items[k + 1].x - items[k].x) * (m / 3);
            marks.push(h("div", { key: `m${k}_${m}`, className: k < targetSlot ? "cdlcbus-mark cdlcbus-mark-passed" : "cdlcbus-mark", style: { left: `${x}%` } }));
        }
    }

    return h("div", { className: "cdlcbus-strip" },
        h("div", { className: "cdlcbus-strip-header", style: { color: atStop ? "#8bdb46" : "#ffffff" } }, header),
        h("div", { className: "cdlcbus-strip-inner" },
            h("div", { className: "cdlcbus-track" }),
            h("div", { className: "cdlcbus-track-done", style: { width: `${busX}%`, backgroundColor: color } }),
            marks,
            items.map((item, k) => {
                const passed = targetSlot >= 0 && k < targetSlot;
                const next = item.index === shift.target;
                const d = Array.isArray(demand[item.index]) ? demand[item.index] : null;
                const skip = !passed && !!d && d[2] > 0;
                const lines = Array.isArray(transfers[item.index]) ? transfers[item.index] : [];
                if (next) {
                    return h("div", { key: `s${item.index}`, className: "cdlcbus-stop cdlcbus-stop-is-next", style: { left: `${item.x}%` } },
                        h("div", { className: skip ? "cdlcbus-stop-dot cdlcbus-stop-dot-next cdlcbus-stop-next-skip" : "cdlcbus-stop-dot cdlcbus-stop-dot-next", style: { borderColor: color } }),
                        h("div", { className: "cdlcbus-row cdlcbus-next-card-row" },
                            h("div", { className: skip ? "cdlcbus-next-card cdlcbus-next-card-skip" : "cdlcbus-next-card", style: skip ? null : { backgroundColor: color, color: inkFor(color) } },
                                h("div", { className: "cdlcbus-next-card-name" }, name(item.index)),
                                d ? h("div", { className: "cdlcbus-next-card-sub" }, skip ? t("UI.SkipTag") : t("UI.Demand", d[0], d[1])) : null),
                            h(TransferBadges, { lines, compact: false })));
                }
                return h("div", { key: `s${item.index}`, className: "cdlcbus-stop", style: { left: `${item.x}%` } },
                    h("div", { className: passed ? "cdlcbus-stop-dot cdlcbus-stop-passed" : (skip ? "cdlcbus-stop-dot cdlcbus-stop-skip" : "cdlcbus-stop-dot"), style: passed || skip ? null : { borderColor: color } }),
                    h("div", { className: passed ? "cdlcbus-stop-name cdlcbus-stop-name-passed" : "cdlcbus-stop-name" }, name(item.index)),
                    passed ? null : h(TransferBadges, { lines, compact: true }),
                    d && !passed ? h("div", { className: skip ? "cdlcbus-stop-demand cdlcbus-stop-demand-skip" : "cdlcbus-stop-demand" },
                        skip ? t("UI.SkipTag") : t("UI.Demand", d[0], d[1])) : null);
            }),
            h("div", { className: "cdlcbus-bus", style: { left: `${busX}%`, borderColor: color } },
                h("img", { src: busIcon }))));
}

// The line sign and the line strip of the HUD. The shift's at the top while driving (BusDriverHud), a ride's at the foot (BusRideHud): the same
// parts; a ride has no arrow (the bus is not the player's to steer) and none of the shift's boarding counts.
function HudBody({ shift, names, demand, transfers, arrow }) {
    const { t } = useText();
    const ann = announcement(shift);
    const atStop = ann.kind === "arrived";
    const nextName = shift.next || t("UI.StopN", shift.nextIndex);
    const towards = shift.terminus ? t("UI.Towards", shift.terminus) : "";
    const signColor = shift.color || "#4bc3f1";
    const ink = inkFor(signColor);
    // Doors open: the sign says how many riders are still to board here (walking to the doors, or waiting with room for
    // them), and how many the full bus leaves behind.
    const doors = shift.state === "DoorsOpening" || shift.state === "DoorsOpen" || shift.state === "DoorsClosing";
    const toBoard = Number(shift.toBoard) || 0, leftBehind = Number(shift.leftBehind) || 0;
    const boardText = doors
        ? (toBoard > 0 ? t("UI.ToBoard", toBoard) : t("UI.NoneToBoard")) +
            (leftBehind > 0 ? ` · ${t("UI.LeftBehind", leftBehind)}` : "")
        : null;
    return h("div", { className: "cdlcbus-row cdlcbus-hud-body" },
        h("div", { className: isLight(signColor) ? "cdlcbus-sign cdlcbus-on-light" : "cdlcbus-sign", style: { backgroundColor: signColor, color: ink } },
            h("div", { className: "cdlcbus-row cdlcbus-sign-top" },
                h(RouteNumber, { prefix: shift.prefix, prefixColor: shift.prefixColor, number: shift.number }),
                h("div", { className: "cdlcbus-sign-towards" }, towards)),
            h("div", { className: "cdlcbus-row cdlcbus-sign-mid" },
                h("div", { className: "cdlcbus-sign-label" }, t(ann.id))),
            h("div", { className: "cdlcbus-sign-next" }, nextName),
            h("div", { className: "cdlcbus-row cdlcbus-sign-foot" },
                arrow && !atStop && !doors ? h(Arrow, { bearing: shift.bearing, color: ink }) : null,
                atStop || doors ? null : h("div", { className: "cdlcbus-sign-distance" }, t("UI.Distance", shift.distance)),
                doors ? h("div", { className: "cdlcbus-sign-board" }, boardText) : null,
                h("div", { className: "cdlcbus-grow" }),
                h("img", { className: "cdlcbus-sign-icon", src: busIcon }),
                h("div", { className: "cdlcbus-sign-pax" }, `${shift.passengers}/${shift.capacity || "–"}`))),
        h("div", { className: "cdlcbus-col cdlcbus-grow" },
            h(LineStrip, { shift, names, demand, transfers, atStop })));
}

// The ride's route at the foot of the screen, above the game's toolbar: the line, the stop the bus heads for, how far it is, the way done, and
// the camera position the player sits at. Only while the bus heads for one of its line's stops.
function BusRideHud() {
    const { t } = useText();
    const enabled = api.useValue(showHud);
    const ride = parse(api.useValue(rideJson), null);
    const names = parse(api.useValue(rideStopsJson), []);
    if (!enabled || !ride || !ride.hasRoute) {
        return null;
    }

    // what the shift's sign reads: the bus has arrived while its doors are open at the stop it heads for
    const shift = Object.assign({ state: "Driving", skip: 0, bearing: 0, stopped: ride.doorsOpen, zone: ride.doorsOpen ? ride.target : -1 }, ride);
    const camera = ride.camera || t("UI.RideDriverSeat");
    return h("div", { className: "cdlcbus-hud cdlcbus-hud-foot" },
        h(HudBody, { shift, names, demand: [], transfers: [], arrow: false }),
        h("div", { className: "cdlcbus-row cdlcbus-prompt cdlcbus-prompt-info" },
            h("div", { className: "cdlcbus-prompt-text" }, `${t("UI.RideRiding")} · ${t("UI.RideCamera")}: ${camera}`)));
}

function BusDriverHud() {
    const { t } = useText();
    const enabled = api.useValue(showHud);
    const shift = parse(api.useValue(shiftJson), null);
    const names = parse(api.useValue(stopsJson), []);
    const demand = parse(api.useValue(demandJson), []);
    const transfers = parse(api.useValue(transfersJson), []);
    const active = shift && ["Driving", "DoorsOpening", "DoorsOpen", "DoorsClosing"].indexOf(shift.state) >= 0;
    const prompt = usePrompt(active ? shift : { state: "Idle" });
    if (!enabled || !active) {
        return null;
    }

    const showMessage = shift.message && shift.messageAge <= 4 && shift.message !== "Msg.ShiftStarted";
    const messageArgs = `${shift.messageArg || ""}`.split("|");
    return h("div", { className: "cdlcbus-hud" },
        h(HudBody, { shift, names, demand, transfers, arrow: true }),
        h("div", { className: `cdlcbus-row cdlcbus-prompt cdlcbus-prompt-${prompt.tone}` },
            prompt.key ? h(KeyCombo, { combo: prompt.key }) : null,
            h("div", { className: "cdlcbus-prompt-text" }, showMessage ? t(shift.message, ...messageArgs) : prompt.text)));
}

function extension(registry) {
    registry.append("GameTopLeft", BusDriverButton);
    registry.append("Game", BusDriverPanel);
    registry.append("Game", BusDriverHud);
    registry.append("Game", BusRideHud);
}

const hasCSS = true;

export { hasCSS, extension as default };
