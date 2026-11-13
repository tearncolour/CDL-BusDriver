// Renders CDLC.BusDriver.mjs with stubbed game UI modules for every shift state, in English and Chinese, and checks for
// runtime errors, missing texts, the stylesheet and the C# <-> JS contract (binding names and shift states).
// Usage: node Tests/ui-smoke.mjs <repo root>
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const root = process.argv[2] || process.cwd();
const languages = {
    "en-US": JSON.parse(readFileSync(join(root, "lang", "en-US.json"), "utf8")),
    "zh-HANS": JSON.parse(readFileSync(join(root, "lang", "zh-HANS.json"), "utf8"))
};
let failures = 0;
const fail = message => {
    failures++;
    console.log("  FAIL " + message);
};

const element = (type, props, ...children) => {
    const flat = children.flat(Infinity);
    const childProp = flat.length === 0 ? {} : { children: flat.length === 1 ? flat[0] : flat };
    return { type, props: Object.assign({}, props || {}, childProp), children: flat };
};
const React = { createElement: element, Fragment: "Fragment", useState: v => [v, () => {}], useRef: v => ({ current: v }), useEffect: () => {}, useMemo: f => f() };
const state = { values: {}, locale: "en-US", missing: new Set() };
globalThis.window = {
    React,
    "cs2/api": {
        bindValue: (group, name, fallback) => ({ group, name, fallback }),
        useValue: binding => (binding.name in state.values ? state.values[binding.name] : binding.fallback),
        trigger: () => {}
    },
    "cs2/ui": { Tooltip: "Tooltip", Button: "Button", Panel: "Panel" },
    "cs2/l10n": {
        useLocalization: () => ({
            translate: (key, fallback) => {
                if (key === "Common.VALUE_MONEY") {
                    return "{SIGN}¢{VALUE}";                 // the game's own text
                }
                const value = languages[state.locale][key];
                if (value === undefined && key.startsWith("CDLC.BusDriver.")) {
                    state.missing.add(key);
                }
                return value ?? fallback ?? null;
            }
        })
    }
};

const module = await import(pathToFileURL(join(root, "CDLC.BusDriver.mjs")).href);
const appended = [];
module.default({ append: (slot, component) => appended.push([slot, component]), extend: () => {} });
if (appended.length !== 4) {
    fail(`expected 4 appended components, got ${appended.length}`);
}
if (module.hasCSS !== true) {
    fail("hasCSS must be true");
}

const css = readFileSync(join(root, "CDLC.BusDriver.css"), "utf8");
// The game's UI engine (Coherent Gameface) drops a declaration that uses var(--x, fallback): backgrounds vanished in game.
if (css.split("var(--").slice(1).some(rest => rest.indexOf(",") >= 0 && rest.indexOf(",") < rest.indexOf(")"))) {
    fail("CDLC.BusDriver.css uses var() with a fallback, which the game ignores");
}
const source = readFileSync(join(root, "CDLC.BusDriver.mjs"), "utf8");
// The script holds ids, not texts: a text call carries no second string literal (that would be an English copy of the language file).
const textCopies = [...source.matchAll(/(?<![\w.$])t\(\s*"[^"]*"\s*,\s*"[^"]*"/g)];
if (textCopies.length > 0) {
    fail(`${textCopies.length} text calls carry a text of their own, the language files hold the texts: ${textCopies[0][0]}`);
}
const classes = new Set((source.match(/cdlcbus-[a-z0-9-]+/g) || []).filter(name => !name.endsWith("-")));
const missingCss = [...classes].filter(name => !css.includes("." + name));
// Template classes built at runtime (cdlcbus-button-*, cdlcbus-prompt-*) are checked through their variants below.
const dynamic = ["primary", "normal", "stop"].map(k => `cdlcbus-button-${k}`).concat(["info", "near", "go", "open", "skip"].map(k => `cdlcbus-prompt-${k}`));
for (const name of dynamic) {
    if (!css.includes("." + name)) {
        missingCss.push(name);
    }
}
if (missingCss.length > 0) {
    fail(`classes without CSS: ${missingCss.join(", ")}`);
}

// The C# code holds no text either: an id given to the game's UI never carries an English text beside it.
for (const folder of ["Systems", "Interior", "Locale"]) {
    for (const name of readdirSync(join(root, folder), { recursive: true })) {
        if (String(name).endsWith(".cs") && readFileSync(join(root, folder, String(name)), "utf8").includes("IdWithFallback")) {
            fail(`${folder}/${name} gives the game an English text beside an id (LocalizedString.IdWithFallback): the language files hold the texts`);
        }
    }
}

// The C# side must bind every value the module reads and use the same state names.
const cs = readFileSync(join(root, "Systems", "BusUiSystem.cs"), "utf8");
for (const match of source.matchAll(/api\.bindValue\(group, "([A-Za-z]+)"/g)) {
    if (!cs.includes(`"${match[1]}"`)) {
        fail(`binding ${match[1]} is not created in BusUiSystem.cs`);
    }
}
for (const match of source.matchAll(/(?<![.\w])trigger\("([A-Za-z]+)"/g)) {
    if (!cs.includes(`"${match[1]}"`)) {
        fail(`trigger ${match[1]} has no TriggerBinding in BusUiSystem.cs`);
    }
}
const stateSource = readFileSync(join(root, "Systems", "BusState.cs"), "utf8");
for (const name of ["Idle", "Dispatching", "TakingOver", "Driving", "DoorsOpening", "DoorsOpen", "DoorsClosing"]) {
    if (!stateSource.includes(`        ${name},`) && !stateSource.includes(`        ${name}\n`) && !stateSource.includes(`        ${name}\r\n`)) {
        fail(`shift state ${name} missing in BusState.cs`);
    }
}
// Every message id the C# code sends exists in both languages.
const shiftSource = readFileSync(join(root, "Systems", "BusShiftSystem.cs"), "utf8");
for (const match of shiftSource.matchAll(/"(Msg\.[A-Za-z]+)"/g)) {
    for (const locale of Object.keys(languages)) {
        if (!(`CDLC.BusDriver.${match[1]}` in languages[locale])) {
            fail(`${locale} lacks CDLC.BusDriver.${match[1]}`);
        }
    }
}

function render(node, texts) {
    if (node === null || node === undefined || node === false || node === true) return;
    if (typeof node === "string" || typeof node === "number") {
        texts.push(String(node));
        return;
    }
    if (Array.isArray(node)) {
        node.forEach(child => render(child, texts));
        return;
    }
    if (typeof node.type === "function") {
        render(node.type(Object.assign({}, node.props, { children: node.children })), texts);
        return;
    }
    for (const prop of ["header", "tooltip"]) {
        if (node.props && node.props[prop] !== undefined) render(node.props[prop], texts);
    }
    node.children.forEach(child => render(child, texts));
}

const shift = (stateName, extra) => JSON.stringify(Object.assign({
    state: stateName, line: "Harbour Line", number: "12", color: "#e8574a", next: "People Square", nextIndex: 4, stopCount: 11,
    distance: 35, bearing: 20, stopped: false, inZone: false, zoneIsTarget: false, zoneName: "", canOpen: false, doorsOpen: false,
    passengers: 18, capacity: 70, waiting: 6, boarded: 32, alighted: 20, stopBoarded: 0, stopAlighted: 0, fares: 256, ticket: 8,
    served: 3, skipped: 1, message: "Msg.ShiftStarted", messageArg: "Harbour Line", messageAge: 1,
    target: 3, zone: -1, progress: 40, onRoute: true, approaching: false, terminus: "7 Pier Road"
}, extra || {}));
const transfers = JSON.stringify([[], [], [["7", "#3a7bd5", "Bus"]], [["7", "#3a7bd5", "Bus"], ["10", "#c0392b", "Bus"], ["M1", "#8e44ad", "Subway"]], [], [["T2", "#27ae60", "Tram"]], [], [], [], [], []]);
const stopNames = JSON.stringify(["12 Harbour Street", "", "3 Oak Avenue", "People Square", "8 Mill Road", "1 Station Road", "40 Elm Street", "2 Bay Road", "9 Hill Street", "5 Park Lane", "7 Pier Road"]);
const lines = JSON.stringify([{ index: 1, version: 1, name: "Harbour Line", number: 12, color: "#e8574a", stops: 11, vehicles: 4, ticket: 8, active: false }]);
const scenarios = [
    ["lines", { Available: true, PanelVisible: true, Lines: lines, Summary: JSON.stringify({ line: "Harbour Line", served: 9, boarded: 64, alighted: 51, fares: 512 }) }],
    ["no lines", { Available: true, PanelVisible: true, Lines: "[]" }],
    ["missing driving mod", { Available: false, PanelVisible: true }],
    ["dispatching", { Available: true, PanelVisible: true, Shift: shift("Dispatching") }],
    ["driving far", { Stops: stopNames, Transfers: transfers, Available: true, PanelVisible: true, DoorKey: "E", Shift: shift("Driving", { distance: 400 }) }],
    ["near zone", { Available: true, PanelVisible: true, DoorKey: "E", Shift: shift("Driving", { distance: 20, inZone: true }) }],
    ["can open", { Stops: stopNames, Available: true, PanelVisible: true, DoorKey: "E", Shift: shift("Driving", { inZone: true, canOpen: true, zoneIsTarget: false, zoneName: "Old Town" }) }],
    ["doors open", { Stops: stopNames, Available: true, PanelVisible: true, DoorKey: "E", Shift: shift("DoorsOpen", { doorsOpen: true, zone: 3, stopped: true, stopBoarded: 3, stopAlighted: 2, toBoard: 4, message: "Msg.Departing", messageArg: "3|2" }) }],
    ["doors open full", { Stops: stopNames, Available: true, PanelVisible: true, DoorKey: "E", Shift: shift("DoorsOpen", { doorsOpen: true, zone: 3, stopped: true, passengers: 70, toBoard: 0, leftBehind: 3, message: "", messageAge: 99 }) }],
    ["into zone", { Stops: stopNames, Available: true, PanelVisible: true, DoorKey: "E", Shift: shift("Driving", { distance: 30, zoneMove: 7, lateral: 4, message: "", messageAge: 99 }) }],
    ["past zone", { Stops: stopNames, Available: true, PanelVisible: true, DoorKey: "E", Shift: shift("Driving", { distance: 0, zoneMove: -4, zonePast: true, lateral: 4, message: "", messageAge: 99 }) }],
    ["reversed short of zone", { Stops: stopNames, Available: true, PanelVisible: true, DoorKey: "E", Shift: shift("Driving", { distance: 40, zoneMove: -3, zonePast: false, lateral: 2, message: "", messageAge: 99 }) }],
    ["skippable", { Stops: stopNames, Demand: "[[0,0,1],[2,1,0],[0,3,0],[0,0,1],[5,0,0],[0,0,1],[1,0,0],[0,0,0],[0,2,0],[0,0,1],[4,4,0]]", Available: true, PanelVisible: true, DoorKey: "E", Shift: shift("Driving", { distance: 180, skip: 1, waiting: 0, alighting: 0, message: "", messageAge: 99 }) }],
    ["skippable docking", { Stops: stopNames, Demand: "[[0,0,1],[2,1,0],[0,3,0],[0,0,1],[5,0,0],[0,0,1],[1,0,0],[0,0,0],[0,2,0],[0,0,1],[4,4,0]]", Available: true, PanelVisible: true, DoorKey: "E", Shift: shift("Driving", { distance: 20, zoneMove: 0, lateral: 9, kerbGap: 9, skip: 2, message: "", messageAge: 99 }) }],
    ["skipped message", { Stops: stopNames, Demand: "[[0,0,1],[2,1,0],[0,3,0],[0,0,1],[5,0,0],[0,0,1],[1,0,0],[0,0,0],[0,2,0],[0,0,1],[4,4,0]]", Available: true, PanelVisible: true, Shift: shift("Driving", { skipped: 2, missed: 1, message: "Msg.MissedStop", messageArg: "Old Town|3|1", messageAge: 1 }) }],
    ["off route", { Stops: stopNames, Available: true, PanelVisible: true, Shift: shift("Driving", { distance: 400, offRoute: true, offRouteDistance: 22, message: "", messageAge: 99 }) }],
    ["turn ahead", { Stops: stopNames, Available: true, PanelVisible: true, Shift: shift("Driving", { distance: 400, turn: -1, turnDistance: 80, message: "", messageAge: 99 }) }],
    ["closing", { Available: true, PanelVisible: true, DoorKey: "E", Shift: shift("DoorsClosing", { doorsOpen: true }) }],
    ["ended", { Available: true, PanelVisible: true, Lines: lines, Shift: shift("Idle", { message: "Msg.DispatchTimeout" }) }]
];

for (const locale of ["en-US", "zh-HANS"]) {
    for (const [label, values] of scenarios) {
        state.locale = locale;
        state.values = values;
        state.missing = new Set();
        const texts = [];
        try {
            for (const [, component] of appended) render(element(component, {}), texts);
        } catch (error) {
            fail(`${locale}/${label} threw ${error.stack}`);
            continue;
        }
        if (state.missing.size > 0) {
            fail(`${locale}/${label} missing keys: ${[...state.missing].join(", ")}`);
        }
        const joined = texts.join(" | ");
        if (locale === "zh-HANS" && !/[一-鿿]/.test(joined)) {
            fail(`${locale}/${label} rendered no Chinese text`);
        }
        if (label === "can open" && !joined.includes(locale === "en-US" ? "Press E to open the doors" : "按 E 开门")) {
            fail(`${locale} door prompt missing: ${joined}`);
        }
        if (label === "driving far" && !(joined.includes("People Square") && joined.includes("8 Mill Road"))) {
            fail(`${locale} line strip does not show the stop names`);
        }
        if (label === "driving far" && !(joined.includes("M1") && joined.includes("10") && joined.includes(locale === "en-US" ? "to 7 Pier Road" : "开往 7 Pier Road"))) {
            fail(`${locale} line strip lacks the transfer badges or the direction: ${joined}`);
        }
        if (label === "doors open" && !joined.includes(locale === "en-US" ? "Now at" : "到站")) {
            fail(`${locale} arrival announcement missing`);
        }
        if (label === "doors open" && !joined.includes(locale === "en-US" ? "4 still to board" : "待上车 4 人")) {
            fail(`${locale} sign lacks the riders still to board: ${joined}`);
        }
        if (label === "doors open full" && !joined.includes(locale === "en-US" ? "Nobody left to board · full, 3 left behind" : "无人待上车 · 满员，3 人留站")) {
            fail(`${locale} sign lacks the riders left behind: ${joined}`);
        }
        if ((label === "driving far" || label === "into zone") && (joined.includes("still to board") || joined.includes("待上车") || joined.includes("无人待上车"))) {
            fail(`${locale}/${label} shows the boarding count with the doors closed`);
        }
        if (label === "into zone" && !joined.includes(locale === "en-US" ? "Drive 7 m on into the boarding zone" : "再向前 7 米驶入上下客区")) {
            fail(`${locale} into-zone prompt missing: ${joined}`);
        }
        if (label === "past zone" && !joined.includes(locale === "en-US" ? "Past the boarding zone: back up 4 m" : "已驶过上下客区：倒车 4 米")) {
            fail(`${locale} past-zone prompt missing: ${joined}`);
        }
        if (label === "reversed short of zone" && !joined.includes(locale === "en-US" ? "Back up 3 m into the boarding zone" : "倒车 3 米驶入上下客区")) {
            fail(`${locale} reversing prompt missing: ${joined}`);
        }
        if (label === "driving far" && !joined.includes(locale === "en-US" ? "Drive to People Square" : "开往 People Square")) {
            fail(`${locale} far from the stop, the prompt is not "drive to": ${joined}`);
        }
        if (/stop line|停车线/.test(joined)) {
            fail(`${locale}/${label} still speaks of a stop line: ${joined}`);
        }
        if (label === "skippable" && !joined.includes(locale === "en-US" ? "drive past to skip it" : "可直接驶过甩站")) {
            fail(`${locale} skip prompt missing: ${joined}`);
        }
        if (label === "skippable" && !joined.includes(locale === "en-US" ? "may skip" : "可甩站")) {
            fail(`${locale} line strip does not tag skippable stops`);
        }
        if (label === "skippable docking" && !joined.includes(locale === "en-US" ? "Pull 0.9 m closer" : "再向站台靠近 0.9 米")) {
            fail(`${locale} kerb distance prompt missing: ${joined}`);
        }
        if (label === "skipped message" && !joined.includes(locale === "en-US" ? "Missed Old Town: 3 waiting, 1 wanted to get off." : "漏站 Old Town：3 人候车，1 人要下车。")) {
            fail(`${locale} missed-stop message missing: ${joined}`);
        }
        if (label === "off route" && !joined.includes(locale === "en-US" ? "Off the line" : "已偏离线路")) {
            fail(`${locale} off-route prompt missing`);
        }
        if (label === "turn ahead" && !joined.includes(locale === "en-US" ? "Turn left in 80 m" : "80 米后左转")) {
            fail(`${locale} turn prompt missing: ${joined}`);
        }
        if (label === "lines" && !joined.includes("¢8")) {
            fail(`${locale} fare not formatted with the game's money format`);
        }
        console.log(`ok   ${locale} ${label}: ${texts.length} texts`);
    }
}

console.log(failures === 0 ? "RESULT=PASS" : "RESULT=FAIL");
process.exit(failures === 0 ? 0 : 1);
