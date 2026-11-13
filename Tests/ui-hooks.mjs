// UI check (node Tests/ui-hooks.mjs): every component of CDLC.BusDriver.mjs calls the same hooks in every render, whatever
// the bindings' values (React throws error #310 otherwise, and the game's whole UI goes with it: a hook after an early return).
// A stand-in for the game's cs2/api, cs2/ui and React renders each component with the panel hidden and shown, idle and in a
// shift, with and without wraps, and counts its hooks; children are rendered too.
import fs from "fs";
import path from "path";
import url from "url";

const here = path.dirname(url.fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.join(here, "..", "CDLC.BusDriver.mjs"), "utf8");

let values = {};
let hooks = 0;
const api = {
    bindValue: (group, name, fallback) => ({ name, fallback }),
    useValue: binding => { hooks++; return binding.name in values ? values[binding.name] : binding.fallback; },
    trigger: () => {},
};
const React = {
    useState: initial => { hooks++; return [initial, () => {}]; },
    useEffect: () => { hooks++; },
    useRef: initial => { hooks++; return { current: initial }; },
    createElement: (type, props, ...children) => ({ type, props: props || {}, children }),
};
globalThis.window = {
    "cs2/api": api,
    "cs2/ui": { Panel: "Panel", Button: "Button", Tooltip: "Tooltip" },
    "cs2/l10n": { useLocalization: () => { hooks++; return { translate: (id, fallback) => fallback }; } },
    React,
};

const module = await import("data:text/javascript;base64," + Buffer.from(source + "\nexport { BusDriverPanel, BusDriverHud, BusDriverButton, WrapsView };").toString("base64"));

// Renders a component and its function children; returns the hooks each component called, by name.
function render(type, props, counts) {
    hooks = 0;
    const tree = type(props || {});
    counts[type.name] = (counts[type.name] || []).concat(hooks);
    walk(tree, counts);
}
function walk(node, counts) {
    if (!node || typeof node !== "object") return;
    if (Array.isArray(node)) { node.forEach(n => walk(n, counts)); return; }
    if (typeof node.type === "function") render(node.type, { ...node.props, children: node.children }, counts);
    (node.children || []).forEach(c => walk(c, counts));
}

const shift = JSON.stringify({ state: "Driving", line: "K4", number: "4", prefix: "K", color: "#f2e86a", next: "Stop", nextIndex: 2, stopCount: 9, target: 1, distance: 85, turn: 1, turnDistance: 60, passengers: 3, capacity: 80 });
const wraps = JSON.stringify({ current: "wrap-1", exterior: true, items: [{ name: "wrap-1", faces: 31, sizes: ["1x1", "", "", "", ""], thumb: "", template: false }, { name: "template", faces: 31, sizes: [], thumb: "", template: true }] });
const states = [
    { Available: true, PanelVisible: false },
    { Available: true, PanelVisible: true },
    { Available: false, PanelVisible: true },
    { Available: true, PanelVisible: true, Shift: shift, Wraps: wraps, ShowHud: true },
    { Available: true, PanelVisible: false, Shift: shift, ShowHud: true },
    { Available: true, PanelVisible: true, Shift: "", Wraps: wraps, ShowHud: false },
];

let failed = 0;
for (const name of ["BusDriverPanel", "BusDriverHud", "BusDriverButton", "WrapsView"]) {
    const perState = [];
    for (const state of states) {
        values = state;
        hooks = 0;
        module[name]({});
        perState.push(hooks);
    }
    const same = perState.every(n => n === perState[0]);
    console.log(`${same ? "ok  " : "FAIL"} ${name}: hooks per render ${perState.join(", ")}`);
    if (!same) failed++;
}
// the children, rendered from the shown states
const counts = {};
for (const state of states.slice(1)) {
    values = state;
    try { render(module.BusDriverPanel, {}, counts); render(module.BusDriverHud, {}, counts); } catch (e) { console.log("FAIL render: " + e.message); failed++; }
}
for (const [name, list] of Object.entries(counts)) {
    const same = list.every(n => n === list[0]);
    if (!same) { console.log(`FAIL ${name}: hooks per render ${list.join(", ")}`); failed++; }
}
console.log(`${Object.keys(counts).length} components rendered; ${failed === 0 ? "RESULT=PASS" : "RESULT=FAIL"}`);
process.exit(failed === 0 ? 0 : 1);
