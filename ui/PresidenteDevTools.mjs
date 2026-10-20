/*!
 * Cities: Skylines II UI Module
 *
 * Id: PresidenteDevTools
 * Author: ZeeIce
 * Version: 0.1.0
 * Dependencies: HomelandSecurity
 */
// Hand-written (no bundler): uses the game's globals exactly as bundled mods do.
const React = window.React;
const api = window["cs2/api"];
const h = React.createElement;
const { useState, useEffect, useCallback } = React;
const G = "PresidenteDevTools";
const responseBinding = api.bindValue(G, "response", "{}");
const statusBinding = api.bindValue(G, "status", "{}");

// ---------- request queue: one outstanding request, matched by seq ----------
let seq = 0, waiting = null;
const queue = [];
function pump() {
  if (waiting || !queue.length) return;
  waiting = queue.shift();
  api.trigger(G, "request", JSON.stringify(Object.assign({ seq: waiting.seq, op: waiting.op }, waiting.args)));
  waiting.timer = setTimeout(() => { const w = waiting; waiting = null; w.reject(new Error("timeout")); pump(); }, 15000);
}
function onResponse(json) {
  let r; try { r = JSON.parse(json); } catch (e) { return; }
  if (!waiting || r.seq !== waiting.seq) return;
  const w = waiting; waiting = null; clearTimeout(w.timer);
  r.ok ? w.resolve(r.result) : w.reject(new Error(r.error));
  pump();
}
function call(op, args) {
  return new Promise((resolve, reject) => { queue.push({ seq: ++seq, op, args: args || {}, resolve, reject }); pump(); });
}

// ---------- tiny shared store for open state / explorer path ----------
const store = { open: false, tab: "quick", path: "", listeners: new Set() };
function setStore(p) { Object.assign(store, p); store.listeners.forEach(l => l()); }
function useStore() {
  const [, force] = useState(0);
  useEffect(() => { const l = () => force(x => x + 1); store.listeners.add(l); return () => store.listeners.delete(l); }, []);
  return store;
}

// ---------- styles ----------
const C = { bg: "rgba(18,22,28,0.97)", panel: "rgba(255,255,255,0.05)", line: "rgba(255,255,255,0.12)", text: "#e8ecf1", dim: "#9aa6b2", accent: "#4fb3ff", good: "#5fd38d", bad: "#ff6b6b", warn: "#f5c04a" };
const S = {
  win: { position: "absolute", top: "70rem", left: "60rem", width: "900rem", height: "720rem", background: C.bg, border: "1px solid " + C.line, borderRadius: "6rem", color: C.text, fontSize: "13rem", display: "flex", flexDirection: "column", pointerEvents: "auto", zIndex: 1000 },
  head: { display: "flex", alignItems: "center", padding: "8rem 12rem", borderBottom: "1px solid " + C.line, background: "rgba(79,179,255,0.10)" },
  tabs: { display: "flex", padding: "6rem 8rem", borderBottom: "1px solid " + C.line },
  body: { flex: 1, overflowY: "auto", padding: "10rem 12rem" },
  btn: { padding: "3rem 9rem", margin: "0 4rem 0 0", background: "rgba(255,255,255,0.10)", border: "1px solid " + C.line, borderRadius: "3rem", color: C.text, cursor: "pointer" },
  btnOn: { background: "rgba(79,179,255,0.35)", borderColor: C.accent },
  input: { padding: "3rem 6rem", background: "rgba(0,0,0,0.35)", border: "1px solid " + C.line, borderRadius: "3rem", color: C.text, fontSize: "13rem" },
  row: { display: "flex", alignItems: "center", padding: "3rem 0", borderBottom: "1px solid rgba(255,255,255,0.05)" },
  card: { background: C.panel, border: "1px solid " + C.line, borderRadius: "4rem", padding: "8rem 10rem", marginBottom: "10rem" },
  h: { fontSize: "14rem", fontWeight: "bold", marginBottom: "6rem", color: C.accent },
  mono: { fontFamily: "monospace" },
};
const Btn = ({ on, onClick, children, title, style }) => h("div", { style: Object.assign({}, S.btn, on ? S.btnOn : null, style), onClick, title }, children);
function Input({ value, onChange, onEnter, width, placeholder }) {
  return h("input", { type: "text", value: value == null ? "" : value, placeholder, style: Object.assign({}, S.input, { width: width || "120rem" }),
    onChange: e => onChange(e.target.value), onKeyDown: e => { if (e.key === "Enter" && onEnter) onEnter(e.target.value); } });
}
function Err({ error }) { return error ? h("div", { style: { color: C.bad, margin: "4rem 0" } }, String(error)) : null; }
function useAsync() {
  const [error, setError] = useState(null), [busy, setBusy] = useState(false);
  const run = useCallback(async (fn) => { setBusy(true); setError(null); try { return await fn(); } catch (e) { setError(e.message || String(e)); } finally { setBusy(false); } }, []);
  return { error, busy, run, setError };
}

// ---------- Quick tab ----------
function Quick() {
  const [data, setData] = useState(null), [vals, setVals] = useState({}), [amt, setAmt] = useState({}), [perm, setPerm] = useState(false);
  const a = useAsync();
  const load = () => a.run(async () => setData(await call("quick")));
  useEffect(() => { load(); const t = setInterval(load, 3000); return () => clearInterval(t); }, []);
  const setv = (k, v) => setVals(Object.assign({}, vals, { [k]: v }));
  const apply = (path, v, pin) => a.run(async () => { await call(pin ? "pin" : "set", { path, value: String(v) }); await load(); });
  if (!data) return h("div", null, h(Err, { error: a.error }), "Loading…");
  const stat = (label, key, path) => h("div", { style: S.row },
    h("div", { style: { width: "170rem" } }, label), h("div", { style: Object.assign({ width: "120rem" }, S.mono) }, data[key]),
    h(Input, { value: vals[key], onChange: v => setv(key, v), onEnter: v => apply(path, v), width: "90rem", placeholder: "3 – 97" }),
    h(Btn, { onClick: () => apply(path, vals[key]) }, "Set"), h(Btn, { onClick: () => apply(path, vals[key], true), title: "Re-applied every frame until unpinned" }, "Pin"),
    h(Btn, { onClick: () => a.run(async () => { await call("unpin", { path }); await load(); }) }, "Unpin"));
  return h("div", null,
    h(Err, { error: a.error }),
    h("div", { style: S.card }, h("div", { style: S.h }, "Approval"),
      stat("President approval", "approval", data.approvalPath),
      stat("Public approval", "publicApproval", data.publicApprovalPath),
      h("div", { style: { color: C.dim, marginTop: "4rem" } }, "Presidente recomputes approval from its survey, so a plain Set drifts back. Pin holds it; mood events below move it the game's own way."),
      h("div", { style: S.row }, h("div", { style: { width: "170rem" } }, "Party liking (left / right)"), h("div", { style: S.mono }, data.liking.join(" / ")))),
    h("div", { style: S.card }, h("div", { style: S.h }, "City money"),
      h("div", { style: S.row }, h("div", { style: { width: "170rem" } }, "Treasury"), h("div", { style: Object.assign({ width: "160rem" }, S.mono) }, data.money ? data.money.money.toLocaleString() + (data.money.unlimited ? " (unlimited)" : "") : "no city"),
        h(Input, { value: vals.money, onChange: v => setv("money", v), width: "130rem" }),
        h(Btn, { onClick: () => a.run(async () => { await call("money", { amount: Number(vals.money) }); await load(); }) }, "Set"))),
    h("div", { style: S.card }, h("div", { style: S.h }, "Moods (29 political actors, −60 … +60)"),
      h("div", { style: { display: "flex", alignItems: "center", marginBottom: "6rem" } },
        h(Btn, { on: perm, onClick: () => setPerm(!perm) }, perm ? "Permanent events" : "Fading events (12 months)"),
        h(Btn, { onClick: () => a.run(async () => { await call("clearMoods", { actor: -1 }); await load(); }) }, "Clear all dev events"),
        h("div", { style: { color: C.dim } }, "Month " + data.month)),
      data.moods.map(m => h("div", { key: m.actor, style: S.row },
        h("div", { style: { width: "34rem", color: C.dim } }, m.actor), h("div", { style: { width: "200rem" } }, m.name),
        h("div", { style: Object.assign({ width: "70rem", color: m.mood >= 0 ? C.good : C.bad }, S.mono) }, m.mood),
        h("div", { style: Object.assign({ width: "90rem", color: C.dim }, S.mono) }, m.injected ? "dev " + m.injected : ""),
        h(Input, { value: amt[m.actor], onChange: v => setAmt(Object.assign({}, amt, { [m.actor]: v })), width: "60rem", placeholder: "±" }),
        h(Btn, { onClick: () => a.run(async () => { await call("mood", { actor: m.actor, amount: Number(amt[m.actor] || 0), permanent: perm }); await load(); }) }, "Add"),
        [-10, -5, 5, 10].map(d => h(Btn, { key: d, onClick: () => a.run(async () => { await call("mood", { actor: m.actor, amount: d, permanent: perm }); await load(); }) }, (d > 0 ? "+" : "") + d)),
        h(Btn, { onClick: () => a.run(async () => { await call("clearMoods", { actor: m.actor }); await load(); }) }, "Clear")))));
}

// ---------- Laws tab ----------
function Laws() {
  const [filter, setFilter] = useState(""), [data, setData] = useState(null);
  const a = useAsync();
  const load = (f) => a.run(async () => setData(await call("laws", { filter: f == null ? filter : f })));
  useEffect(() => { load(""); }, []);
  return h("div", null,
    h("div", { style: { display: "flex", marginBottom: "8rem" } }, h(Input, { value: filter, onChange: setFilter, onEnter: load, width: "300rem", placeholder: "filter by key or name, Enter" }), h(Btn, { onClick: () => load() }, "Search")),
    h("div", { style: { color: C.dim, marginBottom: "6rem" } }, "Forcing writes the law straight into the code (Presidente's WriteLaw), with no Assembly vote and no approval effects."),
    h(Err, { error: a.error }),
    data && data.laws.map(l => h("div", { key: l.key, style: S.row },
      h("div", { style: { width: "60rem", color: l.on ? C.good : C.dim, fontWeight: "bold" } }, l.on ? "ON" : "off"),
      h("div", { style: { width: "320rem" } }, l.name), h("div", { style: Object.assign({ width: "190rem", color: C.dim }, S.mono) }, l.key),
      h("div", { style: { width: "170rem", color: C.dim } }, l.agency),
      h(Btn, { onClick: () => a.run(async () => { await call("forceLaw", { key: l.key, on: !l.on }); await load(); }) }, l.on ? "Force off" : "Force on"))));
}

// ---------- Explorer tab ----------
function Explorer() {
  const st = useStore();
  const [pathInput, setPathInput] = useState(st.path), [data, setData] = useState(null), [roots, setRoots] = useState(null), [rootFilter, setRootFilter] = useState("");
  const [edits, setEdits] = useState({}), [offset, setOffset] = useState(0), [props, setProps] = useState(false), [methods, setMethods] = useState(null), [mFilter, setMFilter] = useState(""), [margs, setMargs] = useState({}), [result, setResult] = useState(null);
  const a = useAsync();
  const go = (p, off) => { setStore({ path: p }); setPathInput(p); setOffset(off || 0); setMethods(null); setResult(null); };
  useEffect(() => {
    if (!st.path) { a.run(async () => setRoots(await call("roots", { filter: rootFilter }))); return; }
    a.run(async () => setData(await call("inspect", { path: st.path, offset, limit: 150, properties: props })));
  }, [st.path, offset, props]);
  const reload = () => a.run(async () => setData(await call("inspect", { path: st.path, offset, limit: 150, properties: props })));
  const parent = (p) => { const i = Math.max(p.lastIndexOf("."), p.lastIndexOf("[")); return i > 0 ? p.substring(0, i) : ""; };
  const crumbs = () => { const out = []; let p = st.path; while (p) { out.unshift(p); p = parent(p); } return out; };
  const nav = h("div", { style: { display: "flex", alignItems: "center", marginBottom: "8rem" } },
    h(Btn, { onClick: () => go("") }, "Roots"), h(Btn, { onClick: () => go(parent(st.path)) }, "Up"),
    h(Input, { value: pathInput, onChange: setPathInput, onEnter: go, width: "470rem", placeholder: "System.m_Field[0].x   or   @StaticType.field" }),
    h(Btn, { onClick: () => go(pathInput) }, "Go"), h(Btn, { on: props, onClick: () => setProps(!props), title: "Evaluate properties (runs getters)" }, "Props"));
  if (!st.path) return h("div", null, nav, h(Err, { error: a.error }),
    h("div", { style: { display: "flex", marginBottom: "6rem" } }, h(Input, { value: rootFilter, onChange: setRootFilter, onEnter: f => a.run(async () => setRoots(await call("roots", { filter: f }))), width: "300rem", placeholder: "filter systems / static types, Enter" })),
    roots && roots.map(r => h("div", { key: r.path, style: Object.assign({ cursor: "pointer" }, S.row), onClick: () => go(r.path) },
      h("div", { style: { width: "60rem", color: r.kind === "system" ? C.accent : C.warn } }, r.kind), h("div", { style: Object.assign({ width: "330rem" }, S.mono) }, r.path),
      h("div", { style: { color: C.dim } }, r.fields + " fields"))));
  return h("div", null, nav,
    h("div", { style: { color: C.dim, marginBottom: "6rem" } }, crumbs().map((c, i) => h("span", { key: c, style: { cursor: "pointer", color: i === crumbs().length - 1 ? C.text : C.dim }, onClick: () => go(c) }, (i ? " › " : "") + c.substring(parent(c).length).replace(/^\./, "")))),
    h(Err, { error: a.error }),
    data && h("div", null,
      h("div", { style: { display: "flex", alignItems: "center", marginBottom: "6rem" } },
        h("div", { style: Object.assign({ color: C.warn, marginRight: "10rem" }, S.mono) }, data.type),
        h("div", { style: { color: C.dim, marginRight: "10rem" } }, data.total + " entries"),
        offset > 0 && h(Btn, { onClick: () => setOffset(Math.max(0, offset - 150)) }, "‹ Prev"),
        offset + 150 < data.total && h(Btn, { onClick: () => setOffset(offset + 150) }, "Next ›"),
        h(Btn, { onClick: reload }, "Refresh"),
        h(Btn, { onClick: () => a.run(async () => setMethods(await call("methods", { path: st.path, filter: mFilter }))) }, "Methods")),
      methods && h("div", { style: S.card }, h("div", { style: S.h }, "Methods of " + methods.type),
        h("div", { style: { display: "flex", marginBottom: "6rem" } }, h(Input, { value: mFilter, onChange: setMFilter, onEnter: f => a.run(async () => setMethods(await call("methods", { path: st.path, filter: f }))), width: "260rem", placeholder: "filter, Enter" })),
        result && h("div", { style: Object.assign({ color: C.good, marginBottom: "6rem" }, S.mono) }, result),
        methods.methods.map(m => h("div", { key: m.id, style: S.row },
          h("div", { style: Object.assign({ width: "520rem", color: m.callable ? C.text : C.dim }, S.mono) }, m.signature),
          m.callable && m.params.filter(p => !p.out).map((p, i) => h(Input, { key: i, value: (margs[m.id] || [])[i], width: "70rem", placeholder: p.name, onChange: v => { const arr = (margs[m.id] || []).slice(); arr[i] = v; setMargs(Object.assign({}, margs, { [m.id]: arr })); } })),
          m.callable && h(Btn, { onClick: () => a.run(async () => {
            const given = margs[m.id] || []; let k = 0; const args = m.params.map(p => p.out ? "" : (given[k++] || ""));
            const r = await call("invoke", { path: st.path, id: m.id, args }); setResult(r.method + " → " + r.returned + (Object.keys(r.outs).length ? "  " + JSON.stringify(r.outs) : "")); await reload(); }) }, "Call")))),
      data.children.map(c => h("div", { key: c.path, style: S.row },
        h("div", { style: Object.assign({ width: "250rem", cursor: c.expandable ? "pointer" : "default", color: c.expandable ? C.accent : C.text }, S.mono), onClick: () => c.expandable && go(c.path) }, (c.static ? "◆ " : "") + c.name),
        h("div", { style: Object.assign({ width: "170rem", color: C.dim }, S.mono) }, c.type),
        h("div", { style: Object.assign({ width: "190rem" }, S.mono) }, c.value),
        c.editable && h(Input, { value: edits[c.path], width: "110rem", placeholder: c.options ? c.options.slice(0, 3).join("|") : "new value",
          onChange: v => setEdits(Object.assign({}, edits, { [c.path]: v })),
          onEnter: v => a.run(async () => { await call("set", { path: c.path, value: v }); await reload(); }) }),
        c.editable && h(Btn, { onClick: () => a.run(async () => { await call("set", { path: c.path, value: edits[c.path] }); await reload(); }) }, "Set"),
        c.editable && h(Btn, { onClick: () => a.run(async () => { await call("pin", edits[c.path] != null && edits[c.path] !== "" ? { path: c.path, value: edits[c.path] } : { path: c.path }); await reload(); }), title: "Hold this value every frame" }, "Pin"),
        c.kind === "bool" && c.editable && h(Btn, { onClick: () => a.run(async () => { await call("set", { path: c.path, value: c.value === "true" ? "false" : "true" }); await reload(); }) }, "Toggle")))));
}

// ---------- Search tab ----------
function Search() {
  const [q, setQ] = useState(""), [hits, setHits] = useState(null);
  const a = useAsync();
  const run = (v) => a.run(async () => setHits(await call("search", { query: v == null ? q : v, depth: 2, limit: 300 })));
  return h("div", null,
    h("div", { style: { display: "flex", marginBottom: "8rem" } }, h(Input, { value: q, onChange: setQ, onEnter: run, width: "320rem", placeholder: "field name, e.g. Approval, Isolation, Population" }), h(Btn, { onClick: () => run() }, "Search")),
    h(Err, { error: a.error }),
    hits && h("div", { style: { color: C.dim, marginBottom: "4rem" } }, hits.length + " fields (systems and nested Presidente objects, two levels deep; static fields of every type)"),
    hits && hits.map(c => h("div", { key: c.path, style: Object.assign({ cursor: "pointer" }, S.row), onClick: () => { const i = Math.max(c.path.lastIndexOf("."), c.path.lastIndexOf("[")); setStore({ tab: "explorer", path: c.expandable ? c.path : c.path.substring(0, i) }); } },
      h("div", { style: Object.assign({ width: "470rem" }, S.mono) }, c.path), h("div", { style: Object.assign({ width: "150rem", color: C.dim }, S.mono) }, c.type), h("div", { style: S.mono }, c.value))));
}

// ---------- Pins & history tab ----------
function Pins() {
  const status = api.useValue(statusBinding);
  const [hist, setHist] = useState(null);
  const a = useAsync();
  const st = (() => { try { return JSON.parse(status); } catch (e) { return {}; } })();
  const load = () => a.run(async () => setHist(await call("history")));
  useEffect(() => { load(); }, []);
  return h("div", null, h(Err, { error: a.error }),
    h("div", { style: S.card }, h("div", { style: S.h }, "Pinned values (re-applied every other frame)"),
      (st.pins || []).length === 0 ? h("div", { style: { color: C.dim } }, "Nothing pinned.") :
        (st.pins || []).map(p => h("div", { key: p.path, style: S.row }, h("div", { style: Object.assign({ width: "520rem" }, S.mono) }, p.path), h("div", { style: Object.assign({ width: "120rem" }, S.mono) }, p.value),
          p.failures ? h("div", { style: { color: C.bad, width: "90rem" } }, p.failures + " fails") : null,
          h(Btn, { onClick: () => a.run(async () => { await call("unpin", { path: p.path }); }) }, "Unpin"))),
      (st.pins || []).length > 0 && h(Btn, { style: { marginTop: "6rem" }, onClick: () => a.run(async () => { await call("unpinAll"); }) }, "Unpin all")),
    h("div", { style: S.card }, h("div", { style: Object.assign({ display: "flex", alignItems: "center" }, S.h) }, "History (this session)", h("div", { style: { marginLeft: "12rem" } }),
        h(Btn, { onClick: () => a.run(async () => { await call("undo"); await load(); }) }, "Undo last edit"), h(Btn, { onClick: load }, "Refresh")),
      hist && hist.map((e, i) => h("div", { key: i, style: S.row }, h("div", { style: { width: "70rem", color: C.dim } }, e.at),
        h("div", { style: Object.assign({ width: "420rem" }, S.mono) }, e.path), h("div", { style: S.mono }, e.call ? e.call + (e.returned ? " → " + e.returned : "") : e.old + " → " + e.new)))));
}

// ---------- window + toolbar button ----------
const TABS = [["quick", "Quick"], ["laws", "Laws"], ["explorer", "Explorer"], ["search", "Search"], ["pins", "Pins & history"]];
function Window() {
  const st = useStore();
  const resp = api.useValue(responseBinding);
  useEffect(() => { onResponse(resp); }, [resp]);
  if (!st.open) return null;
  const Body = { quick: Quick, laws: Laws, explorer: Explorer, search: Search, pins: Pins }[st.tab];
  return h("div", { style: S.win },
    h("div", { style: S.head }, h("div", { style: { fontSize: "16rem", fontWeight: "bold", flex: 1 } }, "Presidente Dev Tools"),
      h("div", { style: { color: C.warn, marginRight: "12rem" } }, "Edits live game state. Save a backup first."), h(Btn, { onClick: () => setStore({ open: false }) }, "Close")),
    h("div", { style: S.tabs }, TABS.map(([k, label]) => h(Btn, { key: k, on: st.tab === k, onClick: () => setStore({ tab: k }) }, label))),
    h("div", { style: S.body }, h(Body, { key: st.tab })));
}
function ToggleButton() {
  const st = useStore();
  return h("div", { style: Object.assign({}, S.btn, st.open ? S.btnOn : null, { pointerEvents: "auto", margin: "6rem", fontWeight: "bold" }), onClick: () => setStore({ open: !st.open }), title: "Presidente Dev Tools" }, "PDT");
}

const register = (moduleRegistry) => {
  moduleRegistry.append("GameTopLeft", ToggleButton);
  moduleRegistry.append("Game", Window);
  console.log("PresidenteDevTools UI registered.");
};
export default register;
export const hasCSS = false;
