using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.UI.Binding;
using Game.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PresidenteDevTools
{
    // Bridges the in-game panel to the engine: one request trigger, one response value, pins applied every frame.
    public sealed partial class DevToolsUISystem : UISystemBase
    {
        const string Group = "PresidenteDevTools";
        readonly Engine engine = new Engine();
        Curated curated;
        ValueBinding<string> response, status;
        readonly Dictionary<string, string> pins = new Dictionary<string, string>();
        readonly Dictionary<string, int> pinFailures = new Dictionary<string, int>();
        readonly List<JObject> history = new List<JObject>();
        float lastRefresh = -100f;
        int frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            curated = new Curated(engine);
            AddBinding(response = new ValueBinding<string>(Group, "response", "{}"));
            AddBinding(status = new ValueBinding<string>(Group, "status", "{}"));
            AddBinding(new TriggerBinding<string>(Group, "request", Handle));
        }

        void EnsureEngine()
        {
            if (UnityEngine.Time.realtimeSinceStartup - lastRefresh < 5f && engine.Ready) return;
            lastRefresh = UnityEngine.Time.realtimeSinceStartup;
            engine.Refresh(World);
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            if (pins.Count == 0 || ++frame % 2 != 0) return;
            EnsureEngine();
            if (!engine.Ready) return;
            foreach (var pin in pins.ToList())
            {
                try { engine.Set(pin.Key, pin.Value, out _); pinFailures.Remove(pin.Key); }
                catch (Exception e)
                {
                    pinFailures.TryGetValue(pin.Key, out var n);
                    if (++n >= 120) { pins.Remove(pin.Key); Mod.Log.Warn($"Pin {pin.Key} removed after repeated failures: {e.GetBaseException().Message}"); PublishStatus(); }
                    pinFailures[pin.Key] = n;
                }
            }
        }

        void PublishStatus()
        {
            status.Update(new JObject
            {
                ["ready"] = engine.Ready, ["presidente"] = engine.Asm?.GetName().Version?.ToString() ?? "not loaded",
                ["pins"] = new JArray(pins.Select(p => new JObject { ["path"] = p.Key, ["value"] = p.Value, ["failures"] = pinFailures.TryGetValue(p.Key, out var n) ? n : 0 })),
                ["history"] = history.Count
            }.ToString(Formatting.None));
        }

        void Handle(string json)
        {
            JObject req; int seq = 0;
            try { req = JObject.Parse(json); seq = (int?)req["seq"] ?? 0; }
            catch (Exception e) { Reply(0, null, "bad_request: " + e.Message); return; }
            try
            {
                EnsureEngine();
                if (!engine.Ready) throw new InvalidOperationException("Presidente (HomelandSecurity) is not loaded");
                Reply(seq, Run((string)req["op"], req), null);
            }
            catch (Exception e) { Reply(seq, null, e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message); }
            PublishStatus();
        }

        void Reply(int seq, JToken result, string error) =>
            response.Update(new JObject { ["seq"] = seq, ["ok"] = error == null, ["result"] = result, ["error"] = error }.ToString(Formatting.None));

        JToken Run(string op, JObject r)
        {
            string path = (string)r["path"];
            switch (op)
            {
                case "status": return new JObject { ["ready"] = engine.Ready };
                case "roots": return engine.Roots((string)r["filter"]);
                case "inspect": return engine.Inspect(path, (int?)r["offset"] ?? 0, (int?)r["limit"] ?? 200, (bool?)r["properties"] ?? false);
                case "set":
                {
                    var value = (string)r["value"];
                    var applied = engine.Set(path, value, out var old);
                    Record(path, old, applied);
                    if (pins.ContainsKey(path)) pins[path] = value;
                    return new JObject { ["path"] = path, ["old"] = Engine.Short(old), ["value"] = Engine.Short(engine.Resolve(path).Last().Value) };
                }
                case "pin":
                {
                    var value = (string)r["value"] ?? RawOf(engine.Resolve(path).Last().Value);
                    engine.Set(path, value, out var old); Record(path, old, value);
                    pins[path] = value; pinFailures.Remove(path);
                    return new JObject { ["pinned"] = path, ["value"] = value };
                }
                case "unpin": pins.Remove(path); pinFailures.Remove(path); return new JObject { ["unpinned"] = path };
                case "unpinAll": pins.Clear(); pinFailures.Clear(); return new JObject { ["unpinned"] = "all" };
                case "methods": return engine.ListMethods(path, (string)r["filter"]);
                case "invoke": { var res = engine.Invoke(path, (int)r["id"], r["args"] as JArray); history.Add(new JObject { ["at"] = DateTime.Now.ToString("HH:mm:ss"), ["path"] = path, ["call"] = res["method"], ["returned"] = res["returned"] }); return res; }
                case "search": return engine.Search((string)r["query"], (int?)r["depth"] ?? 2, (int?)r["limit"] ?? 200);
                case "history": return new JArray(history.AsEnumerable().Reverse().Take(200));
                case "undo":
                {
                    var last = history.LastOrDefault(h => h["undo"] != null) ?? throw new InvalidOperationException("nothing_to_undo");
                    var p = (string)last["path"]; var raw = (string)last["undo"];
                    engine.Set(p, raw, out _); pins.Remove(p); history.Remove(last);
                    return new JObject { ["restored"] = p, ["value"] = raw };
                }
                case "quick": return curated.Overview();
                case "mood": return curated.Mood((int)r["actor"], (float)r["amount"], (bool?)r["permanent"] ?? false);
                case "clearMoods": return curated.ClearMoods((int?)r["actor"] ?? -1);
                case "laws": return curated.Laws((string)r["filter"]);
                case "forceLaw": { var res = curated.ForceLaw((string)r["key"], (bool)r["on"]); history.Add(new JObject { ["at"] = DateTime.Now.ToString("HH:mm:ss"), ["path"] = "law " + r["key"], ["call"] = "WriteLaw(" + r["key"] + ", " + r["on"] + ")" }); return res; }
                case "money": return curated.SetMoney((long)r["amount"]);
                default: throw new ArgumentException("unknown_op:" + op);
            }
        }

        static string RawOf(object v) => v == null ? "null" : v is string s ? s : v is bool b ? (b ? "true" : "false") : v is Unity.Entities.Entity e ? e.Index + ":" + e.Version : v is IFormattable f && !v.GetType().IsEnum ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : v.ToString();

        void Record(string path, object old, object applied)
        {
            history.Add(new JObject { ["at"] = DateTime.Now.ToString("HH:mm:ss"), ["path"] = path, ["old"] = Engine.Short(old), ["new"] = Engine.Short(applied), ["undo"] = RawOf(old) });
            if (history.Count > 500) history.RemoveAt(0);
        }
    }
}
