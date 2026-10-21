using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Game.City;
using Game.Simulation;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace CS2DevTools
{
    // Presidente plugin: hand-picked controls over its politics, built on its own methods where they exist. Shown only when Presidente is loaded.
    internal sealed class Curated
    {
        internal const string ApprovalPath = "@HomelandSecurity.Immigration.StandingSystem.<Approval>k__BackingField";
        internal const string PublicApprovalPath = "HomelandSecurity.Immigration.StandingSystem.m_PublicApproval";
        const string Key = "devtools";
        const int Permanent = 1000000; // events dated in the future never fade (Presidente's Fade(age <= 0) == 1)
        readonly Engine engine;
        internal Curated(Engine e) { engine = e; }

        internal bool Available => engine.AssemblyNamed("HomelandSecurity") != null && engine.System("HomelandSecurity.Immigration.StandingSystem") != null;
        object Standing => engine.System("HomelandSecurity.Immigration.StandingSystem") ?? throw new InvalidOperationException("presidente_not_loaded");
        static object Call(object target, string name, params object[] args)
        {
            var m = target.GetType().GetMethods(Engine.Inst).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length) ?? throw new MissingMethodException(target.GetType().Name, name);
            return m.Invoke(target, args);
        }

        string[] ActorNames()
        {
            var t = engine.TypeNamed("HomelandSecurity.Immigration.StandingTable");
            return t?.GetField("Names", Engine.Stat)?.GetValue(null) as string[] ?? Enumerable.Range(0, 29).Select(i => "Actor " + i).ToArray();
        }

        internal JObject Overview()
        {
            var s = Standing;
            var names = ActorNames();
            var moods = new JArray();
            for (int i = 0; i < 29; i++)
            {
                float mood = 0f; try { mood = (float)Call(s, "Mood", i); } catch { }
                moods.Add(new JObject { ["actor"] = i, ["name"] = i < names.Length ? names[i] : "Actor " + i, ["mood"] = Math.Round(mood, 2), ["injected"] = Math.Round(Injected(i), 2) });
            }
            float liking0 = 0, liking1 = 0; try { liking0 = (float)Call(s, "Liking", 0); liking1 = (float)Call(s, "Liking", 1); } catch { }
            return new JObject
            {
                ["approval"] = Engine.Short(engine.Resolve(ApprovalPath).Last().Value), ["approvalPath"] = ApprovalPath,
                ["publicApproval"] = Engine.Short(engine.Resolve(PublicApprovalPath).Last().Value), ["publicApprovalPath"] = PublicApprovalPath,
                ["liking"] = new JArray(Math.Round(liking0, 2), Math.Round(liking1, 2)), ["month"] = (int)Call(s, "Month"),
                ["moods"] = moods, ["money"] = Money(), ["moneyNote"] = "Vanilla city treasury (PlayerMoney). Presidente's national accounts read from it."
            };
        }

        float Injected(int actor)
        {
            float sum = 0;
            foreach (var ev in Events()) if ((byte)Field(ev, "A") == actor && ((string)Field(ev, "K") ?? "").StartsWith(Key, StringComparison.Ordinal)) sum += (float)Field(ev, "V");
            return sum;
        }

        IList Events() => (IList)Standing.GetType().GetField("m_Events", Engine.Inst).GetValue(Standing);
        static object Field(object o, string n) => o.GetType().GetField(n, Engine.Inst).GetValue(o);

        // Adds a mood event through Presidente's own Add(actor, amount, month, key). Temporary events fade over 12 months.
        internal JObject Mood(int actor, float amount, bool permanent)
        {
            if (actor < 0 || actor >= 29) throw new ArgumentException("actor_0_to_28");
            var s = Standing; int month = (int)Call(s, "Month");
            Call(s, "Add", actor, amount, permanent ? month + Permanent : month, permanent ? Key + ":permanent" : Key);
            return new JObject { ["actor"] = actor, ["added"] = amount, ["permanent"] = permanent, ["moodNow"] = Math.Round((float)Call(s, "Mood", actor), 2) };
        }

        internal JObject ClearMoods(int actor)
        {
            var list = Events(); int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var ev = list[i];
                if (((string)Field(ev, "K") ?? "").StartsWith(Key, StringComparison.Ordinal) && (actor < 0 || (byte)Field(ev, "A") == actor)) { list.RemoveAt(i); removed++; }
            }
            Standing.GetType().GetField("m_MoodDirty", Engine.Inst).SetValue(Standing, true);
            return new JObject { ["removed"] = removed };
        }

        // Every law with its state; forcing writes it straight into the code without a vote.
        internal JObject Laws(string filter)
        {
            var s = Standing; var rows = new JArray();
            foreach (var row in LawRows())
            {
                string key = (string)RowMember(row, "Key"), name = (string)RowMember(row, "Name");
                if (!string.IsNullOrEmpty(filter) && (key + " " + name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool on = false; try { on = (bool)Call(s, "On", key); } catch { }
                rows.Add(new JObject { ["key"] = key, ["name"] = name, ["agency"] = (string)RowMember(row, "Agency") ?? "", ["on"] = on });
            }
            return new JObject { ["laws"] = rows };
        }

        internal JObject ForceLaw(string key, bool on)
        {
            var ok = (bool)Call(Standing, "WriteLaw", key, on);
            bool now = (bool)Call(Standing, "On", key);
            return new JObject { ["key"] = key, ["wanted"] = on, ["written"] = ok, ["on"] = now };
        }

        IEnumerable LawRows()
        {
            var t = engine.TypeNamed("HomelandSecurity.Immigration.StandingTable") ?? throw new InvalidOperationException("standing_table_missing");
            foreach (var f in t.GetFields(Engine.Stat))
            {
                if (!(f.GetValue(null) is IEnumerable e) || f.FieldType == typeof(string[])) continue;
                var et = f.FieldType.IsArray ? f.FieldType.GetElementType() : f.FieldType.IsGenericType ? f.FieldType.GetGenericArguments()[0] : null;
                if (et == null || et.Name != "Row") continue;
                var isLaw = t.GetMethod("IsLaw", Engine.Stat, null, new[] { typeof(string) }, null);
                foreach (var row in e) { var key = (string)RowMember(row, "Key"); if (key != null && (isLaw == null || (bool)isLaw.Invoke(null, new object[] { key }))) yield return row; }
                yield break;
            }
        }

        static object RowMember(object row, string name)
        {
            var t = row.GetType();
            var f = t.GetField(name, Engine.Inst); if (f != null) return f.GetValue(row);
            var p = t.GetProperty(name, Engine.Inst); return p?.GetValue(row);
        }

        // ---------- vanilla money ----------
        EntityManager Em => World.DefaultGameObjectInjectionWorld.EntityManager;
        Entity CityEntity() => World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<CitySystem>()?.City ?? Entity.Null;

        JToken Money()
        {
            var city = CityEntity();
            if (city == Entity.Null || !Em.HasComponent<PlayerMoney>(city)) return null;
            var pm = Em.GetComponentData<PlayerMoney>(city);
            return new JObject { ["money"] = pm.money, ["unlimited"] = pm.m_Unlimited };
        }

        internal JObject SetMoney(long amount)
        {
            var city = CityEntity();
            if (city == Entity.Null || !Em.HasComponent<PlayerMoney>(city)) throw new InvalidOperationException("no_city");
            object boxed = Em.GetComponentData<PlayerMoney>(city);
            var f = typeof(PlayerMoney).GetField("m_Money", Engine.Inst) ?? throw new MissingFieldException("PlayerMoney.m_Money");
            f.SetValue(boxed, global::System.Convert.ChangeType(Math.Max(int.MinValue, Math.Min(int.MaxValue, amount)), f.FieldType));
            Em.SetComponentData(city, (PlayerMoney)boxed);
            return new JObject { ["money"] = Em.GetComponentData<PlayerMoney>(city).money };
        }
    }
}
