/*!
 * Cities: Skylines II UI Module
 *
 * Id: CS2DevTools
 * Author: ZeeIce
 * Version: 0.2.1
 * Dependencies:
 */
// Hand-written (no bundler): uses the game's globals exactly as bundled mods do.
const React = window.React;
const api = window["cs2/api"];
const h = React.createElement;
const { useState, useEffect, useCallback } = React;
const G = "CS2DevTools";
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
const store = { open: false, tab: "explorer", path: "", listeners: new Set() };
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
const go = (p) => setStore({ tab: "explorer", path: p });
function EntityLink({ c }) {
  return c.entity ? h("div", { style: { cursor: "pointer", color: C.accent, marginLeft: "6rem" }, onClick: () => go(c.entity), title: "Open entity" }, "→ " + (c.entityLabel || c.entity)) : null;
}
function Explorer() {
  const st = useStore();
  const [pathInput, setPathInput] = useState(st.path), [data, setData] = useState(null), [roots, setRoots] = useState(null);
  const [rootFilter, setRootFilter] = useState(""), [asm, setAsm] = useState(""), [statics, setStatics] = useState(false), [rootOffset, setRootOffset] = useState(0);
  const [edits, setEdits] = useState({}), [offset, setOffset] = useState(0), [props, setProps] = useState(false), [methods, setMethods] = useState(null), [mFilter, setMFilter] = useState(""), [margs, setMargs] = useState({}), [result, setResult] = useState(null);
  const a = useAsync();
  const nav = (p, off) => { setStore({ path: p }); setPathInput(p); setOffset(off || 0); setMethods(null); setResult(null); };
  const loadRoots = (o) => a.run(async () => setRoots(await call("roots", { filter: rootFilter, assembly: asm || null, statics, offset: o || 0, limit: 150 })));
  useEffect(() => { setPathInput(st.path); }, [st.path]);
  useEffect(() => {
    if (!st.path) { loadRoots(rootOffset); return; }
    a.run(async () => setData(await call("inspect", { path: st.path, offset, limit: 150, properties: props })));
  }, [st.path, offset, props, statics, rootOffset]);
  const reload = () => a.run(async () => setData(await call("inspect", { path: st.path, offset, limit: 150, properties: props })));
  const parent = (p) => { const i = Math.max(p.lastIndexOf("."), p.lastIndexOf("[")); return i > 0 ? p.substring(0, i) : ""; };
  const crumbs = () => { const out = []; let p = st.path; while (p) { out.unshift(p); p = parent(p); } return out; };
  const selected = () => a.run(async () => { const s = await call("selected"); nav(s.path); });
  const bar = h("div", { style: { display: "flex", alignItems: "center", marginBottom: "8rem" } },
    h(Btn, { onClick: () => nav("") }, "Roots"), h(Btn, { onClick: () => nav(parent(st.path)) }, "Up"),
    h(Input, { value: pathInput, onChange: setPathInput, onEnter: nav, width: "390rem", placeholder: "System.m_field[0]   @Type.static   #1234:1.Building" }),
    h(Btn, { onClick: () => nav(pathInput) }, "Go"), h(Btn, { onClick: selected, title: "Open whatever is selected in the game" }, "Inspect selected"),
    h(Btn, { on: props, onClick: () => setProps(!props), title: "Evaluate properties (runs getters)" }, "Props"));
  if (!st.path) return h("div", null, bar, h(Err, { error: a.error }),
    h("div", { style: { display: "flex", alignItems: "center", marginBottom: "6rem" } },
      h(Input, { value: rootFilter, onChange: setRootFilter, onEnter: () => { setRootOffset(0); loadRoots(0); }, width: "220rem", placeholder: "filter type name, Enter" }),
      h(Input, { value: asm, onChange: setAsm, onEnter: () => { setRootOffset(0); loadRoots(0); }, width: "160rem", placeholder: "assembly (blank = all)" }),
      h(Btn, { on: statics, onClick: () => setStatics(!statics), title: "Also list types with static fields" }, "Statics"),
      h(Btn, { onClick: () => { setRootOffset(0); loadRoots(0); } }, "Search")),
    roots && h("div", { style: { display: "flex", flexWrap: "wrap", marginBottom: "6rem" } }, roots.assemblies.map(n => h(Btn, { key: n, on: asm === n, style: { marginBottom: "3rem", fontSize: "11rem" }, onClick: () => { const v = asm === n ? "" : n; setAsm(v); setRootOffset(0); a.run(async () => setRoots(await call("roots", { filter: rootFilter, assembly: v || null, statics, offset: 0, limit: 150 }))); } }, n))),
    roots && h("div", { style: { color: C.dim, marginBottom: "4rem", display: "flex", alignItems: "center" } }, roots.total + " roots",
      rootOffset > 0 && h(Btn, { style: { marginLeft: "8rem" }, onClick: () => setRootOffset(Math.max(0, rootOffset - 150)) }, "‹ Prev"),
      rootOffset + 150 < roots.total && h(Btn, { style: { marginLeft: "8rem" }, onClick: () => setRootOffset(rootOffset + 150) }, "Next ›")),
    roots && roots.roots.map(r => h("div", { key: r.path, style: Object.assign({ cursor: "pointer" }, S.row), onClick: () => nav(r.path) },
      h("div", { style: { width: "56rem", color: r.kind === "system" ? C.accent : C.warn } }, r.kind), h("div", { style: Object.assign({ width: "420rem" }, S.mono) }, r.path),
      h("div", { style: { width: "160rem", color: C.dim } }, r.assembly), h("div", { style: { color: C.dim } }, r.fields + " fields"))));
  return h("div", null, bar,
    h("div", { style: { color: C.dim, marginBottom: "6rem" } }, crumbs().map((c, i) => h("span", { key: c, style: { cursor: "pointer", color: i === crumbs().length - 1 ? C.text : C.dim }, onClick: () => nav(c) }, (i ? " › " : "") + (i ? c.substring(parent(c).length).replace(/^\./, "") : c)))),
    h(Err, { error: a.error }),
    data && h("div", null,
      h("div", { style: { display: "flex", alignItems: "center", marginBottom: "6rem" } },
        h("div", { style: Object.assign({ color: C.warn, marginRight: "10rem" }, S.mono) }, data.type),
        data.entityLabel && h("div", { style: { color: C.text, marginRight: "10rem" } }, data.entityLabel),
        h("div", { style: { color: C.dim, marginRight: "10rem" } }, data.total + " entries"),
        offset > 0 && h(Btn, { onClick: () => setOffset(Math.max(0, offset - 150)) }, "‹ Prev"),
        offset + 150 < data.total && h(Btn, { onClick: () => setOffset(offset + 150) }, "Next ›"),
        h(Btn, { onClick: reload }, "Refresh"),
        !st.path.startsWith("#") || st.path.indexOf(".") > 0 ? h(Btn, { onClick: () => a.run(async () => setMethods(await call("methods", { path: st.path, filter: mFilter }))) }, "Methods") : null),
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
        h("div", { style: Object.assign({ width: "240rem", cursor: c.expandable ? "pointer" : "default", color: c.expandable ? C.accent : C.text }, S.mono), onClick: () => c.expandable && nav(c.path) }, (c.static ? "◆ " : "") + c.name),
        h("div", { style: Object.assign({ width: "160rem", color: c.kind === "buffer" ? C.warn : C.dim }, S.mono) }, c.kind === "component" || c.kind === "buffer" || c.kind === "tag" ? c.kind : c.type),
        h("div", { style: Object.assign({ width: "180rem" }, S.mono) }, c.value),
        h(EntityLink, { c }),
        c.editable && h(Input, { value: edits[c.path], width: "110rem", placeholder: c.options ? (c.flags ? "A|B" : c.options.slice(0, 3).join("|")) : "new value",
          onChange: v => setEdits(Object.assign({}, edits, { [c.path]: v })),
          onEnter: v => a.run(async () => { await call("set", { path: c.path, value: v }); await reload(); }) }),
        c.editable && h(Btn, { onClick: () => a.run(async () => { await call("set", { path: c.path, value: edits[c.path] }); await reload(); }) }, "Set"),
        c.editable && h(Btn, { onClick: () => a.run(async () => { await call("pin", edits[c.path] != null && edits[c.path] !== "" ? { path: c.path, value: edits[c.path] } : { path: c.path }); await reload(); }), title: "Hold this value every frame (saved per city unless it is on an entity)" }, "Pin"),
        c.kind === "bool" && c.editable && h(Btn, { onClick: () => a.run(async () => { await call("set", { path: c.path, value: c.value === "true" ? "false" : "true" }); await reload(); }) }, "Toggle")))));
}

// ---------- Entities tab ----------
function Entities() {
  const [all, setAll] = useState("Building"), [none, setNone] = useState(""), [filter, setFilter] = useState(""), [data, setData] = useState(null), [offset, setOffset] = useState(0);
  const a = useAsync();
  const split = (s) => s.split(",").map(x => x.trim()).filter(Boolean);
  const run = (o) => a.run(async () => { setOffset(o || 0); setData(await call("entities", { all: split(all), none: split(none), filter, offset: o || 0, limit: 150 })); });
  return h("div", null,
    h("div", { style: { color: C.dim, marginBottom: "6rem" } }, "Find live entities by component (short or full type names, comma separated). Examples: Building, ResidentialProperty · Citizen · Car · Edge, Road · CompanyData."),
    h("div", { style: { display: "flex", alignItems: "center", marginBottom: "8rem" } },
      h(Input, { value: all, onChange: setAll, onEnter: () => run(0), width: "230rem", placeholder: "with components" }),
      h(Input, { value: none, onChange: setNone, onEnter: () => run(0), width: "160rem", placeholder: "without (optional)" }),
      h(Input, { value: filter, onChange: setFilter, onEnter: () => run(0), width: "160rem", placeholder: "prefab name contains" }),
      h(Btn, { onClick: () => run(0) }, "Find"),
      h(Btn, { onClick: () => a.run(async () => { const s = await call("selected"); go(s.path); }) }, "Inspect selected")),
    h(Err, { error: a.error }),
    data && h("div", { style: { color: C.dim, marginBottom: "4rem", display: "flex", alignItems: "center" } }, data.total + " entities",
      offset > 0 && h(Btn, { style: { marginLeft: "8rem" }, onClick: () => run(Math.max(0, offset - 150)) }, "‹ Prev"),
      offset + 150 < data.total && h(Btn, { style: { marginLeft: "8rem" }, onClick: () => run(offset + 150) }, "Next ›")),
    data && data.entities.map(e => h("div", { key: e.path, style: Object.assign({ cursor: "pointer" }, S.row), onClick: () => go(e.path) },
      h("div", { style: Object.assign({ width: "120rem", color: C.accent }, S.mono) }, e.path), h("div", null, e.label))));
}

// ---------- Search tab ----------
function Search() {
  const [q, setQ] = useState(""), [mode, setMode] = useState("name"), [scope, setScope] = useState(""), [asm, setAsm] = useState(""), [hits, setHits] = useState(null);
  const a = useAsync();
  const run = () => a.run(async () => setHits(await call("search", { query: q, mode, scope: scope || null, assembly: asm || null, depth: 2, limit: 300 })));
  const open = (c) => { const i = Math.max(c.path.lastIndexOf("."), c.path.lastIndexOf("[")); go(c.expandable ? c.path : c.path.substring(0, i)); };
  return h("div", null,
    h("div", { style: { display: "flex", alignItems: "center", marginBottom: "8rem" } },
      h(Btn, { on: mode === "name", onClick: () => setMode("name") }, "By name"), h(Btn, { on: mode === "value", onClick: () => setMode("value") }, "By value"),
      h(Input, { value: q, onChange: setQ, onEnter: run, width: "220rem", placeholder: mode === "value" ? "97, 0.5, Tariff…" : "Approval, Isolation…" }),
      h(Input, { value: scope, onChange: setScope, onEnter: run, width: "170rem", placeholder: "scope: systems | statics | path" }),
      h(Input, { value: asm, onChange: setAsm, onEnter: run, width: "140rem", placeholder: "assembly (optional)" }),
      h(Btn, { onClick: run }, "Search")),
    h("div", { style: { color: C.dim, marginBottom: "6rem" } }, "Value search matches numbers within a tiny tolerance and text or enum names containing the query. Scope a path (an entity like #1234:1, or a system) to search inside it; leave blank for every system."),
    h(Err, { error: a.error }),
    hits && h("div", { style: { color: C.dim, marginBottom: "4rem" } }, hits.length + " matches"),
    hits && hits.map(c => h("div", { key: c.path, style: Object.assign({ cursor: "pointer" }, S.row), onClick: () => open(c) },
      h("div", { style: Object.assign({ width: "470rem" }, S.mono) }, c.path), h("div", { style: Object.assign({ width: "140rem", color: C.dim }, S.mono) }, c.type), h("div", { style: S.mono }, c.value))));
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
    h("div", { style: S.card }, h("div", { style: S.h }, "Pinned values" + (st.city ? " — " + st.city : "")),
      h("div", { style: { color: C.dim, marginBottom: "6rem" } }, "Re-applied every other frame. Saved per city and restored on load, except pins on entities, which last for this session only."),
      (st.pins || []).length === 0 ? h("div", { style: { color: C.dim } }, "Nothing pinned.") :
        (st.pins || []).map(p => h("div", { key: p.path, style: S.row }, h("div", { style: Object.assign({ width: "480rem", cursor: "pointer" }, S.mono), onClick: () => { const i = Math.max(p.path.lastIndexOf("."), p.path.lastIndexOf("[")); go(p.path.substring(0, i)); } }, p.path),
          h("div", { style: Object.assign({ width: "110rem" }, S.mono) }, p.value), h("div", { style: { width: "80rem", color: p.persistent ? C.good : C.warn } }, p.persistent ? "saved" : "session"),
          p.failures ? h("div", { style: { color: C.bad, width: "70rem" } }, p.failures + " fails") : null,
          h(Btn, { onClick: () => a.run(async () => { await call("unpin", { path: p.path }); }) }, "Unpin"))),
      (st.pins || []).length > 0 && h(Btn, { style: { marginTop: "6rem" }, onClick: () => a.run(async () => { await call("unpinAll"); }) }, "Unpin all")),
    h("div", { style: S.card }, h("div", { style: Object.assign({ display: "flex", alignItems: "center" }, S.h) }, "History (this session)", h("div", { style: { marginLeft: "12rem" } }),
        h(Btn, { onClick: () => a.run(async () => { await call("undo"); await load(); }) }, "Undo last edit"), h(Btn, { onClick: load }, "Refresh")),
      hist && hist.map((e, i) => h("div", { key: i, style: S.row }, h("div", { style: { width: "70rem", color: C.dim } }, e.at),
        h("div", { style: Object.assign({ width: "420rem" }, S.mono) }, e.path), h("div", { style: S.mono }, e.call ? e.call + (e.returned ? " → " + e.returned : "") : e.old + " → " + e.new)))));
}

// ---------- window + toolbar button ----------
function Window() {
  const st = useStore();
  const resp = api.useValue(responseBinding);
  const status = api.useValue(statusBinding);
  useEffect(() => { onResponse(resp); }, [resp]);
  if (!st.open) return null;
  let s = {}; try { s = JSON.parse(status); } catch (e) { }
  const tabs = [["explorer", "Explorer"], ["entities", "Entities"], ["search", "Search"], ["pins", "Pins & history"]];
  if (s.presidente) tabs.push(["quick", "Presidente"], ["laws", "Laws"]);
  const Body = { quick: Quick, laws: Laws, explorer: Explorer, entities: Entities, search: Search, pins: Pins }[st.tab] || Explorer;
  return h("div", { style: S.win },
    h("div", { style: S.head }, h("div", { style: { fontSize: "16rem", fontWeight: "bold", flex: 1 } }, "Dev Tools"),
      h("div", { style: { color: C.warn, marginRight: "12rem" } }, "Edits live game state. Save a backup first."), h(Btn, { onClick: () => setStore({ open: false }) }, "Close")),
    h("div", { style: S.tabs }, tabs.map(([k, label]) => h(Btn, { key: k, on: st.tab === k, onClick: () => setStore({ tab: k }) }, label))),
    h("div", { style: S.body }, h(Body, { key: st.tab })));
}
function ToggleButton() {
  const st = useStore();
  return h("div", { style: Object.assign({}, S.btn, st.open ? S.btnOn : null, { pointerEvents: "auto", margin: "6rem", fontWeight: "bold" }), onClick: () => setStore({ open: !st.open }), title: "Dev Tools" }, "DEV");
}

// "Inspect in Dev Tools" section in the game's selected-object info panel (C#: InspectSection).
function InspectSectionView(props) {
  return h("div", { style: { padding: "6rem 10rem", display: "flex", alignItems: "center", pointerEvents: "auto" } },
    h("div", { style: { flex: 1, color: "rgba(255,255,255,0.7)", fontSize: "12rem" } }, "Dev Tools · " + (props.path || "")),
    h("div", { style: Object.assign({}, S.btn, { fontSize: "12rem" }), onClick: () => setStore({ open: true, tab: "explorer", path: props.path }) }, "Inspect in Dev Tools"));
}

const register = (moduleRegistry) => {
  moduleRegistry.append("GameTopLeft", ToggleButton);
  moduleRegistry.append("Game", Window);
  try {
    moduleRegistry.extend("game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx", "selectedInfoSectionComponents",
      (components) => { components["CS2DevTools.InspectSection"] = (p) => h(InspectSectionView, p); return components; });
  } catch (e) { console.error("CS2DevTools: info panel section registration failed; use Inspect selected in the panel instead.", e); }
  console.log("CS2DevTools UI registered.");
};
export default register;
export const hasCSS = false;
