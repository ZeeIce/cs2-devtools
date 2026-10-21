using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using Game;
using Game.City;
using Game.UI;
using Game.UI.InGame;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CS2DevTools
{
    // Bridges the in-game panel (and Api, for other mods) to the engine: one request trigger, one response value,
    // pins applied every other frame and saved per city.
    public sealed partial class DevToolsUISystem : UISystemBase
    {
        const string Group = "CS2DevTools";
        internal static DevToolsUISystem Instance { get; private set; }
        readonly Engine engine = new Engine();
        Curated presidente;
        ValueBinding<string> response, status;
        readonly Dictionary<string, string> pins = new Dictionary<string, string>();
        readonly Dictionary<string, int> pinFailures = new Dictionary<string, int>();
        readonly List<JObject> history = new List<JObject>();
        float lastRefresh = -100f;
        int frame;
        string pinCity;

        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            presidente = new Curated(engine);
            AddBinding(response = new ValueBinding<string>(Group, "response", "{}"));
            AddBinding(status = new ValueBinding<string>(Group, "status", "{}"));
            AddBinding(new TriggerBinding<string>(Group, "request", json => response.Update(Handle(json))));
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            pins.Clear(); pinFailures.Clear(); lastRefresh = -100f;
            if (mode == GameMode.Game) LoadPins();
            PublishStatus();
        }

        void EnsureEngine(bool force = false)
        {
            if (!force && UnityEngine.Time.realtimeSinceStartup - lastRefresh < 5f && engine.Ready) return;
            lastRefresh = UnityEngine.Time.realtimeSinceStartup;
            engine.Refresh(World);
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            if (pins.Count == 0 || ++frame % 2 != 0) return;
            EnsureEngine();
            foreach (var pin in pins.ToList())
            {
                try { engine.Set(pin.Key, pin.Value, out _); pinFailures.Remove(pin.Key); }
                catch (Exception e)
                {
                    pinFailures.TryGetValue(pin.Key, out var n);
                    if (++n >= 120) { pins.Remove(pin.Key); Mod.Log.Warn($"Pin {pin.Key} removed after repeated failures: {e.GetBaseException().Message}"); SavePins(); PublishStatus(); }
                    pinFailures[pin.Key] = n;
                }
            }
        }

        // ---------- persistent pins (per city; entity pins are session-only because entity ids change on load) ----------
        static string PinsFile => Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", "CS2DevTools", "pins.json");
        string CityName() { try { return World.GetExistingSystemManaged<CityConfigurationSystem>()?.cityName ?? ""; } catch { return ""; } }

        void LoadPins()
        {
            pinCity = CityName();
            try
            {
                if (!File.Exists(PinsFile)) return;
                var all = JObject.Parse(File.ReadAllText(PinsFile));
                if (all[pinCity] is JObject saved) foreach (var p in saved.Properties()) pins[p.Name] = (string)p.Value;
                if (pins.Count > 0) Mod.Log.Info($"Restored {pins.Count} pins for {pinCity}");
            }
            catch (Exception e) { Mod.Log.Warn("Pins not restored: " + e.Message); }
        }

        void SavePins()
        {
            try
            {
                var all = File.Exists(PinsFile) ? JObject.Parse(File.ReadAllText(PinsFile)) : new JObject();
                var city = new JObject(); foreach (var p in pins.Where(p => !p.Key.TrimStart().StartsWith("#"))) city[p.Key] = p.Value;
                if (city.Count == 0) all.Remove(pinCity ?? ""); else all[pinCity ?? ""] = city;
                Directory.CreateDirectory(Path.GetDirectoryName(PinsFile));
                File.WriteAllText(PinsFile, all.ToString(Formatting.Indented));
            }
            catch (Exception e) { Mod.Log.Warn("Pins not saved: " + e.Message); }
        }

        void PublishStatus()
        {
            status.Update(new JObject
            {
                ["ready"] = engine.Ready, ["presidente"] = SafeAvailable(), ["city"] = CityName(), ["assemblies"] = engine.Assemblies.Count(),
                ["pins"] = new JArray(pins.Select(p => new JObject { ["path"] = p.Key, ["value"] = p.Value, ["failures"] = pinFailures.TryGetValue(p.Key, out var n) ? n : 0, ["persistent"] = !p.Key.TrimStart().StartsWith("#") })),
                ["history"] = history.Count
            }.ToString(Formatting.None));
        }

        internal string DescribeEntity(Unity.Entities.Entity e) { try { EnsureEngine(); return engine.Describe(e); } catch { return ""; } }

        bool SafeAvailable() { try { return presidente.Available; } catch { return false; } }

        // Single entry point for the panel and for other mods (Api). Returns {seq, ok, result|error}.
        internal string Handle(string json)
        {
            JObject req; int seq = 0;
            try { req = JObject.Parse(json); seq = (int?)req["seq"] ?? 0; }
            catch (Exception e) { return Reply(0, null, "bad_request: " + e.Message); }
            string result;
            try { EnsureEngine(); result = Reply(seq, Run((string)req["op"], req), null); }
            catch (Exception e) { result = Reply(seq, null, e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message); }
            PublishStatus();
            return result;
        }

        static string Reply(int seq, JToken result, string error) =>
            new JObject { ["seq"] = seq, ["ok"] = error == null, ["result"] = result, ["error"] = error }.ToString(Formatting.None);

        internal static readonly HashSet<string> WriteOps = new HashSet<string> { "set", "pin", "unpin", "unpinAll", "invoke", "undo", "mood", "clearMoods", "forceLaw", "money" };

        JToken Run(string op, JObject r)
        {
            string path = (string)r["path"];
            switch (op)
            {
                case "status": EnsureEngine(true); return new JObject { ["ready"] = engine.Ready, ["presidente"] = SafeAvailable(), ["assemblies"] = new JArray(engine.Assemblies.Select(a => a.GetName().Name).OrderBy(n => n)) };
                case "roots": return engine.Roots((string)r["filter"], (string)r["assembly"], (bool?)r["statics"] ?? false, (int?)r["offset"] ?? 0, (int?)r["limit"] ?? 200);
                case "inspect": return engine.Inspect(path, (int?)r["offset"] ?? 0, (int?)r["limit"] ?? 200, (bool?)r["properties"] ?? false);
                case "entities": return engine.Entities(r["all"] as JArray, r["none"] as JArray, (string)r["filter"], (int?)r["offset"] ?? 0, (int?)r["limit"] ?? 100);
                case "selected":
                {
                    var e = World.GetExistingSystemManaged<SelectedInfoUISystem>()?.selectedEntity ?? Unity.Entities.Entity.Null;
                    if (e == Unity.Entities.Entity.Null) throw new InvalidOperationException("nothing_selected: click a building, vehicle, citizen or road in the game first");
                    return new JObject { ["path"] = "#" + e.Index + ":" + e.Version, ["label"] = engine.Describe(e) };
                }
                case "set":
                {
                    var value = (string)r["value"];
                    var applied = engine.Set(path, value, out var old);
                    Record(path, old, applied);
                    if (pins.ContainsKey(path)) { pins[path] = value; SavePins(); }
                    return new JObject { ["path"] = path, ["old"] = Engine.Short(old), ["value"] = Engine.Short(engine.Resolve(path).Last().Value) };
                }
                case "pin":
                {
                    var value = (string)r["value"] ?? RawOf(engine.Resolve(path).Last().Value);
                    engine.Set(path, value, out var old); Record(path, old, value);
                    pins[path] = value; pinFailures.Remove(path); SavePins();
                    return new JObject { ["pinned"] = path, ["value"] = value, ["persistent"] = !path.TrimStart().StartsWith("#") };
                }
                case "unpin": pins.Remove(path); pinFailures.Remove(path); SavePins(); return new JObject { ["unpinned"] = path };
                case "unpinAll": pins.Clear(); pinFailures.Clear(); SavePins(); return new JObject { ["unpinned"] = "all" };
                case "methods": return engine.ListMethods(path, (string)r["filter"]);
                case "invoke": { var res = engine.Invoke(path, (int)r["id"], r["args"] as JArray); history.Add(new JObject { ["at"] = DateTime.Now.ToString("HH:mm:ss"), ["path"] = path, ["call"] = res["method"], ["returned"] = res["returned"] }); return res; }
                case "search": return engine.Search((string)r["query"], (string)r["mode"] ?? "name", (string)r["scope"], (string)r["assembly"], Math.Min(4, (int?)r["depth"] ?? 2), Math.Min(1000, (int?)r["limit"] ?? 200));
                case "history": return new JArray(history.AsEnumerable().Reverse().Take(200));
                case "undo":
                {
                    var last = history.LastOrDefault(h => h["undo"] != null) ?? throw new InvalidOperationException("nothing_to_undo");
                    var p = (string)last["path"]; var raw = (string)last["undo"];
                    engine.Set(p, raw, out _); if (pins.Remove(p)) SavePins(); history.Remove(last);
                    return new JObject { ["restored"] = p, ["value"] = raw };
                }
                case "pins": return new JArray(pins.Select(p => new JObject { ["path"] = p.Key, ["value"] = p.Value }));
                // ---------- Presidente plugin ----------
                case "quick": RequirePresidente(); return presidente.Overview();
                case "mood": RequirePresidente(); return presidente.Mood((int)r["actor"], (float)r["amount"], (bool?)r["permanent"] ?? false);
                case "clearMoods": RequirePresidente(); return presidente.ClearMoods((int?)r["actor"] ?? -1);
                case "laws": RequirePresidente(); return presidente.Laws((string)r["filter"]);
                case "forceLaw": { RequirePresidente(); var res = presidente.ForceLaw((string)r["key"], (bool)r["on"]); history.Add(new JObject { ["at"] = DateTime.Now.ToString("HH:mm:ss"), ["path"] = "law " + r["key"], ["call"] = "WriteLaw(" + r["key"] + ", " + r["on"] + ")" }); return res; }
                case "money": return presidente.SetMoney((long)r["amount"]);
                default: throw new ArgumentException("unknown_op:" + op);
            }
        }

        void RequirePresidente() { if (!SafeAvailable()) throw new InvalidOperationException("presidente_not_loaded"); }

        static string RawOf(object v) => v == null ? "null" : v is string s ? s : v is bool b ? (b ? "true" : "false") : v is Unity.Entities.Entity e ? e.Index + ":" + e.Version : v is IFormattable f && !v.GetType().IsEnum ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : v.ToString();

        void Record(string path, object old, object applied)
        {
            history.Add(new JObject { ["at"] = DateTime.Now.ToString("HH:mm:ss"), ["path"] = path, ["old"] = Engine.Short(old), ["new"] = Engine.Short(applied), ["undo"] = RawOf(old) });
            if (history.Count > 500) history.RemoveAt(0);
        }
    }

    // Entry point for other mods (the agent bridge calls this by reflection; no compile-time dependency either way).
    // Request and response are the panel's JSON. Must be called on the main thread.
    public static class Api
    {
        public static bool Ready => DevToolsUISystem.Instance != null;
        public static bool IsWrite(string op) => DevToolsUISystem.WriteOps.Contains(op ?? "");
        public static string Handle(string json) => DevToolsUISystem.Instance?.Handle(json) ?? "{\"ok\":false,\"error\":\"devtools_not_running\"}";
    }
}
