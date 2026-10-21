using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;

namespace CS2DevTools
{
    // Path-addressed reflection over the base game, every loaded mod, and live ECS entities. Nothing on disk is
    // changed; values are read and written on the live objects. Paths look like:
    //   CitySystem.m_City                      a system (short name, or full name when two share one)
    //   @StandingSystem.<Approval>k__BackingField   a type's static members ('@')
    //   SomeSystem.m_Map[#2]                   the third entry of a dictionary or set
    //   #1234:1.Building.m_Flags               entity 1234 version 1, component Building, field m_Flags
    //   #1234:1.Renter[0].m_Renter             element 0 of the entity's Renter buffer
    internal sealed class Engine
    {
        internal const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly string[] frameworkPrefixes = { "System", "mscorlib", "netstandard", "Mono.", "Unity.", "UnityEngine", "Newtonsoft", "Colossal", "cohtml", "Burst", "Microsoft", "PDX", "Bee.", "0Harmony", "Harmony", "Steamworks", "Galaxy", "Cinemachine", "TextMesh", "nunit", "Anonymously" };

        readonly Dictionary<string, object> systems = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Type> types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Type> componentTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<Assembly> assemblies = new List<Assembly>();
        World world;
        bool typesLoaded;

        internal bool Ready => assemblies.Count > 0;
        internal IEnumerable<Assembly> Assemblies => assemblies;
        internal World World => world;
        internal EntityManager Em => world.EntityManager;

        internal static bool IsFramework(Assembly a)
        {
            var n = a.GetName().Name; if (a.IsDynamic) return true;
            return frameworkPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || n == "Assembly-CSharp-firstpass";
        }

        internal void Refresh(World w)
        {
            world = w;
            var current = AppDomain.CurrentDomain.GetAssemblies().Where(a => !IsFramework(a)).ToList();
            if (current.Count != assemblies.Count) { assemblies = current; typesLoaded = false; }
            LoadTypes();
            systems.Clear(); var shortCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in w.Systems)
            {
                var t = s.GetType(); if (IsFramework(t.Assembly)) continue;
                systems[t.FullName] = s;
                shortCount[t.Name] = shortCount.TryGetValue(t.Name, out var c) ? c + 1 : 1;
            }
            foreach (var s in w.Systems) { var t = s.GetType(); if (!IsFramework(t.Assembly) && shortCount[t.Name] == 1) systems[t.Name] = s; }
        }

        // Offline tests supply stand-in assemblies and system objects instead of an ECS world.
        internal void UseForTests(IEnumerable<Assembly> asms, IEnumerable<object> roots)
        {
            assemblies = asms.ToList(); typesLoaded = false; LoadTypes(); systems.Clear();
            foreach (var r in roots) { systems[r.GetType().FullName] = r; systems[r.GetType().Name] = r; }
        }

        void LoadTypes()
        {
            if (typesLoaded) return;
            types.Clear(); componentTypes.Clear(); ambiguous.Clear();
            foreach (var asm in assemblies.Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("Unity.Entities") || a.GetName().Name == "Game")).Distinct())
            {
                Type[] all; try { all = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { all = e.Types.Where(t => t != null).ToArray(); } catch { continue; }
                foreach (var t in all)
                {
                    if (t.Name.StartsWith("<", StringComparison.Ordinal) || t.IsGenericTypeDefinition) continue;
                    if (assemblies.Contains(asm)) { Register(types, t); }
                    if (t.IsValueType && (typeof(IComponentData).IsAssignableFrom(t) || typeof(IBufferElementData).IsAssignableFrom(t))) Register(componentTypes, t);
                }
            }
            typesLoaded = true;
        }

        void Register(Dictionary<string, Type> map, Type t)
        {
            map[t.FullName.Replace('+', '.')] = t;
            string shortName = t.IsNested ? t.DeclaringType.Name + "." + t.Name : t.Name;
            if (map.TryGetValue(shortName, out var existing) && existing != t) { ambiguous.Add(shortName); map.Remove(shortName); }
            else if (!ambiguous.Contains(shortName)) map[shortName] = t;
        }

        internal object System(string name) { systems.TryGetValue(name, out var s); return s; }
        internal Type TypeNamed(string name) { LoadTypes(); types.TryGetValue(name, out var t); return t; }
        internal Type ComponentNamed(string name) { LoadTypes(); componentTypes.TryGetValue(name, out var t); return t; }
        internal Assembly AssemblyNamed(string name) => assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

        // ---------- roots ----------
        internal JObject Roots(string filter, string assembly, bool statics, int offset, int limit)
        {
            var rows = new List<JObject>();
            foreach (var kv in systems.Where(k => k.Key == k.Value.GetType().FullName).OrderBy(k => k.Value.GetType().Assembly.GetName().Name).ThenBy(k => k.Value.GetType().Name))
            {
                var t = kv.Value.GetType();
                if (!Match(t.FullName, filter) || (assembly != null && !string.Equals(t.Assembly.GetName().Name, assembly, StringComparison.OrdinalIgnoreCase))) continue;
                rows.Add(new JObject { ["path"] = systems.ContainsKey(t.Name) && systems[t.Name] == kv.Value ? t.Name : t.FullName, ["kind"] = "system", ["type"] = t.FullName, ["assembly"] = t.Assembly.GetName().Name, ["fields"] = Fields(t, Inst).Count() });
            }
            if (statics)
                foreach (var t in types.Values.Distinct().OrderBy(t => t.FullName))
                {
                    if (!Match(t.FullName, filter) || (assembly != null && !string.Equals(t.Assembly.GetName().Name, assembly, StringComparison.OrdinalIgnoreCase))) continue;
                    int n = Fields(t, Stat).Count(); if (n == 0) continue;
                    rows.Add(new JObject { ["path"] = "@" + RootName(t), ["kind"] = "static", ["type"] = t.FullName, ["assembly"] = t.Assembly.GetName().Name, ["fields"] = n });
                }
            limit = Math.Max(1, Math.Min(500, limit));
            return new JObject { ["total"] = rows.Count, ["offset"] = offset, ["roots"] = new JArray(rows.Skip(offset).Take(limit)),
                ["assemblies"] = new JArray(assemblies.Select(a => a.GetName().Name).OrderBy(n => n)) };
        }

        string RootName(Type t) { var s = t.IsNested ? t.DeclaringType.Name + "." + t.Name : t.Name; return types.TryGetValue(s, out var x) && x == t ? s : t.FullName.Replace('+', '.'); }
        static bool Match(string s, string f) => string.IsNullOrEmpty(f) || (s ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;

        // Declared fields down the hierarchy, stopping at framework base classes (Unity ECS, Colossal, System).
        internal IEnumerable<FieldInfo> Fields(Type t, BindingFlags flags)
        {
            var seen = new HashSet<string>();
            for (var cur = t; cur != null && cur != typeof(object) && cur != typeof(ValueType); cur = cur.BaseType)
            {
                if (cur != t && IsFramework(cur.Assembly)) break;
                foreach (var f in cur.GetFields(flags | BindingFlags.DeclaredOnly)) if (!f.IsLiteral && seen.Add(f.Name)) yield return f;
                if ((flags & BindingFlags.Static) != 0) break;
            }
        }

        // ---------- paths ----------
        abstract class Seg { }
        sealed class Member : Seg { public string Name; }
        sealed class Index : Seg { public int I; }
        sealed class Ordinal : Seg { public int I; }

        static List<Seg> Parse(string path, out string root, out char kind)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("empty_path");
            path = path.Trim(); kind = path[0] == '@' || path[0] == '#' ? path[0] : 's';
            if (kind != 's') path = path.Substring(1);
            var segs = new List<Seg>(); int i = 0;
            if (kind == '#') { int dot = path.IndexOf('.'); root = dot < 0 ? path : path.Substring(0, dot); i = dot < 0 ? path.Length : dot; }
            else root = ReadName(path, ref i, kind == '@');
            while (i < path.Length)
            {
                char c = path[i];
                if (c == '.') { i++; segs.Add(new Member { Name = ReadName(path, ref i, false) }); }
                else if (c == '[')
                {
                    int end = path.IndexOf(']', i); if (end < 0) throw new ArgumentException("unclosed_index");
                    var inner = path.Substring(i + 1, end - i - 1).Trim(); i = end + 1;
                    if (inner.StartsWith("#", StringComparison.Ordinal)) segs.Add(new Ordinal { I = int.Parse(inner.Substring(1), CultureInfo.InvariantCulture) });
                    else segs.Add(new Index { I = int.Parse(inner, CultureInfo.InvariantCulture) });
                }
                else throw new ArgumentException("bad_path_at_" + i);
            }
            return segs;
        }

        // Static roots may be written with dots in the type name (Namespace.Type.field); greedy matching resolves them later.
        static string ReadName(string p, ref int i, bool greedy)
        {
            int start = i;
            while (i < p.Length && p[i] != '[' && (greedy || p[i] != '.')) i++;
            if (i == start) throw new ArgumentException("empty_name");
            return p.Substring(start, i - start);
        }

        internal static Entity ParseEntity(string s)
        {
            s = s.Trim().TrimStart('#').Replace("Entity(", "").Replace(")", "");
            var parts = s.Split(':');
            return new Entity { Index = int.Parse(parts[0], CultureInfo.InvariantCulture), Version = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1 };
        }

        internal sealed class Hop
        {
            public object Holder; public Type DeclaredType; public object Value; public string Label;
            public Func<object, object> Get; public Action<object, object> Set; public bool Editable;
            public Entity Entity; public Type Component; // set on entity and component hops
        }

        internal List<Hop> Resolve(string path)
        {
            var segs = Parse(path, out var rootName, out var kind);
            var hops = new List<Hop>(); object cur;
            if (kind == '@')
            {
                // "@Type.field": split the greedy root into the longest known type name plus member names.
                var parts = rootName.Split('.'); Type t = null; int used = 0;
                for (int k = parts.Length; k >= 1 && t == null; k--) { t = TypeNamed(string.Join(".", parts.Take(k))); if (t != null) used = k; }
                if (t == null) throw new ArgumentException("unknown_type:" + rootName);
                var members = parts.Skip(used).Select(n => (Seg)new Member { Name = n }).ToList(); segs.InsertRange(0, members);
                hops.Add(new Hop { DeclaredType = t, Label = "@" + RootName(t) });
                if (segs.Count == 0) return hops;
                var m = segs[0] as Member ?? throw new ArgumentException("static_needs_member");
                var hop = MemberHop(t, null, m.Name, true); hops.Add(hop); cur = hop.Value; segs.RemoveAt(0);
            }
            else if (kind == '#')
            {
                var e = ParseEntity(rootName);
                if (world == null || !Em.Exists(e)) throw new ArgumentException("entity_not_found_or_stale");
                hops.Add(new Hop { Value = e, DeclaredType = typeof(Entity), Label = "#" + e.Index + ":" + e.Version, Entity = e });
                cur = e;
                if (segs.Count > 0 && segs[0] is Member cm)
                {
                    var hop = ComponentHop(e, cm.Name); hops.Add(hop); cur = hop.Value; segs.RemoveAt(0);
                }
            }
            else
            {
                // Full type names contain dots: take the longest leading run of member names that names a system.
                if (System(rootName) == null)
                {
                    var name = rootName; int take = 0;
                    for (int k = 0; k < segs.Count && segs[k] is Member mk; k++) { name += "." + mk.Name; if (System(name) != null) { rootName = name; take = k + 1; } }
                    segs.RemoveRange(0, take);
                }
                cur = System(rootName) ?? throw new ArgumentException("unknown_system:" + rootName + (ambiguous.Contains(rootName) ? " (ambiguous: use the full type name)" : ""));
                hops.Add(new Hop { Value = cur, DeclaredType = cur.GetType(), Label = rootName });
            }
            foreach (var seg in segs)
            {
                if (cur == null) throw new InvalidOperationException("null_in_path");
                Hop hop;
                if (seg is Member m) hop = MemberHop(cur.GetType(), cur, m.Name, false);
                else if (seg is Index ix) hop = IndexHop(cur, ix.I);
                else hop = OrdinalHop(cur, ((Ordinal)seg).I);
                hops.Add(hop); cur = hop.Value;
            }
            return hops;
        }

        // ---------- entities ----------
        static readonly MethodInfo getComponent = typeof(EntityManager).GetMethods().First(m => m.Name == "GetComponentData" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
        static readonly MethodInfo setComponent = typeof(EntityManager).GetMethods().First(m => m.Name == "SetComponentData" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);
        static readonly MethodInfo getBuffer = typeof(EntityManager).GetMethods().First(m => m.Name == "GetBuffer" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);

        internal IEnumerable<ComponentType> ComponentsOf(Entity e)
        {
            using (var arr = Em.GetComponentTypes(e, Allocator.Temp)) return arr.ToArray();
        }

        static bool IsZeroSize(Type t) => t.IsValueType && t.GetFields(Inst).Length == 0;

        Hop ComponentHop(Entity e, string name)
        {
            var ct = ComponentsOf(e).Select(c => c.GetManagedType()).FirstOrDefault(t => t != null && (t.Name == name || t.FullName == name || t.FullName.Replace('+', '.') == name || (t.IsNested && t.DeclaringType.Name + "." + t.Name == name)))
                ?? throw new ArgumentException("entity_has_no_component:" + name);
            if (typeof(IBufferElementData).IsAssignableFrom(ct))
            {
                var buffer = getBuffer.MakeGenericMethod(ct).Invoke(Em, new object[] { e, false });
                var arr = buffer.GetType().GetMethod("AsNativeArray").Invoke(buffer, null);
                return new Hop { Holder = e, DeclaredType = arr.GetType(), Value = arr, Label = ct.Name, Entity = e, Component = ct };
            }
            if (typeof(IComponentData).IsAssignableFrom(ct) && ct.IsValueType)
            {
                if (IsZeroSize(ct)) return new Hop { Holder = e, DeclaredType = ct, Value = Activator.CreateInstance(ct), Label = ct.Name, Entity = e, Component = ct };
                var g = getComponent.MakeGenericMethod(ct); var s = setComponent.MakeGenericMethod(ct);
                return new Hop { Holder = e, DeclaredType = ct, Label = ct.Name, Entity = e, Component = ct, Value = g.Invoke(Em, new object[] { e }),
                    Get = h => g.Invoke(Em, new object[] { (Entity)h }), Set = (h, v) => s.Invoke(Em, new object[] { (Entity)h, v }), Editable = true };
            }
            if (ct.IsClass) { var o = Em.GetComponentObject<object>(e, ComponentType.ReadOnly(ct)); return new Hop { Holder = e, DeclaredType = ct, Value = o, Label = ct.Name, Entity = e, Component = ct }; }
            return new Hop { Holder = e, DeclaredType = ct, Value = null, Label = ct.Name + " (unsupported)", Entity = e, Component = ct };
        }

        internal JObject Entities(JArray all, JArray none, string prefabFilter, int offset, int limit)
        {
            if (world == null) throw new InvalidOperationException("no_world");
            var allTypes = (all ?? new JArray()).Values<string>().Select(n => ComponentNamed(n) ?? throw new ArgumentException("unknown_component:" + n)).ToList();
            var noneTypes = (none ?? new JArray()).Values<string>().Select(n => ComponentNamed(n) ?? throw new ArgumentException("unknown_component:" + n)).ToList();
            if (allTypes.Count == 0) throw new ArgumentException("at_least_one_component_required");
            var desc = new EntityQueryDesc { All = allTypes.Select(t => ComponentType.ReadOnly(t)).ToArray(), None = noneTypes.Select(t => ComponentType.ReadOnly(t)).ToArray(), Options = EntityQueryOptions.IncludePrefab };
            var rows = new JArray(); int total = 0; limit = Math.Max(1, Math.Min(500, limit));
            using (var q = Em.CreateEntityQuery(desc))
            using (var arr = q.ToEntityArray(Allocator.Temp))
                foreach (var e in arr.ToArray().OrderBy(x => x.Index))
                {
                    var label = Describe(e);
                    if (!string.IsNullOrEmpty(prefabFilter) && label.IndexOf(prefabFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (total++ < offset || rows.Count >= limit) continue;
                    rows.Add(new JObject { ["path"] = "#" + e.Index + ":" + e.Version, ["label"] = label });
                }
            return new JObject { ["total"] = total, ["offset"] = offset, ["entities"] = rows };
        }

        // Prefab name and position, for listing and for entity references shown as values.
        internal string Describe(Entity e)
        {
            try
            {
                var em = Em; string s = "";
                if (em.HasComponent<Game.Prefabs.PrefabRef>(e)) { var p = em.GetComponentData<Game.Prefabs.PrefabRef>(e).m_Prefab; s = PrefabName(p); }
                else if (em.HasComponent<Game.Prefabs.PrefabData>(e)) s = "prefab " + PrefabName(e);
                if (em.HasComponent<Game.Objects.Transform>(e)) { var t = em.GetComponentData<Game.Objects.Transform>(e).m_Position; s += string.Format(CultureInfo.InvariantCulture, " @ {0:0},{1:0}", t.x, t.z); }
                else if (em.HasComponent<Game.Net.Node>(e)) { var t = em.GetComponentData<Game.Net.Node>(e).m_Position; s += string.Format(CultureInfo.InvariantCulture, " node @ {0:0},{1:0}", t.x, t.z); }
                return s.Trim();
            }
            catch { return ""; }
        }

        string PrefabName(Entity prefab) { try { return world.GetExistingSystemManaged<Game.Prefabs.PrefabSystem>().GetPrefabName(prefab); } catch { return ""; } }

        // ---------- member / index hops ----------
        Hop MemberHop(Type t, object holder, string name, bool isStatic)
        {
            var flags = isStatic ? Stat : Inst;
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                var f = cur.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (f != null) return new Hop { Holder = holder, DeclaredType = f.FieldType, Label = name, Value = f.GetValue(holder), Get = h => f.GetValue(h), Set = (h, v) => f.SetValue(h, v), Editable = !f.IsLiteral };
                var p = cur.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0 && p.CanRead)
                {
                    var setter = p.GetSetMethod(true);
                    return new Hop { Holder = holder, DeclaredType = p.PropertyType, Label = name, Value = p.GetValue(holder), Get = h => p.GetValue(h), Set = setter == null ? null : (Action<object, object>)((h, v) => p.SetValue(h, v)), Editable = setter != null };
                }
            }
            throw new ArgumentException("no_member:" + name);
        }

        // Arrays, IList, and struct collections with Length + indexer (NativeArray, NativeList, DynamicBuffer).
        static Hop IndexHop(object holder, int i)
        {
            if (holder is Array arr)
            {
                if (arr.Rank != 1 || i < 0 || i >= arr.Length) throw new ArgumentException("index_out_of_range");
                return new Hop { Holder = holder, DeclaredType = arr.GetType().GetElementType(), Label = "[" + i + "]", Value = arr.GetValue(i), Get = h => ((Array)h).GetValue(i), Set = (h, v) => ((Array)h).SetValue(v, i), Editable = true };
            }
            if (holder is IList list)
            {
                if (i < 0 || i >= list.Count) throw new ArgumentException("index_out_of_range");
                return new Hop { Holder = holder, DeclaredType = ElementType(holder.GetType()) ?? typeof(object), Label = "[" + i + "]", Value = list[i], Get = h => ((IList)h)[i], Set = (h, v) => ((IList)h)[i] = v, Editable = !list.IsReadOnly };
            }
            var idx = Indexer(holder.GetType());
            if (idx != null)
            {
                int n = Count(holder); if (i < 0 || i >= n) throw new ArgumentException("index_out_of_range");
                var setter = idx.GetSetMethod(true);
                return new Hop { Holder = holder, DeclaredType = idx.PropertyType, Label = "[" + i + "]", Value = idx.GetValue(holder, new object[] { i }), Get = h => idx.GetValue(h, new object[] { i }),
                    Set = setter == null ? null : (Action<object, object>)((h, v) => idx.SetValue(h, v, new object[] { i })), Editable = setter != null };
            }
            throw new ArgumentException("not_indexable");
        }

        static PropertyInfo Indexer(Type t)
        {
            if (!t.IsValueType || t.GetProperty("Length", Inst) == null) return null;
            return t.GetProperties(Inst).FirstOrDefault(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType == typeof(int) && !p.PropertyType.IsByRef);
        }
        static int Count(object o) { try { return (int)o.GetType().GetProperty("Length", Inst).GetValue(o); } catch { return 0; } }
        internal static bool IsIndexable(object o) => o != null && Indexer(o.GetType()) != null;

        static Hop OrdinalHop(object holder, int i)
        {
            if (holder is IDictionary dict)
            {
                int n = 0;
                foreach (DictionaryEntry e in dict)
                {
                    if (n++ != i) continue;
                    var key = e.Key; var args = holder.GetType().GetGenericArguments();
                    return new Hop { Holder = holder, DeclaredType = args.Length == 2 ? args[1] : typeof(object), Label = "[#" + i + "] " + Short(key), Value = e.Value, Get = h => ((IDictionary)h)[key], Set = (h, v) => ((IDictionary)h)[key] = v, Editable = true };
                }
                throw new ArgumentException("index_out_of_range");
            }
            if (holder is IEnumerable en && !(holder is string))
            {
                int n = 0;
                foreach (var item in en) if (n++ == i) return new Hop { Holder = holder, DeclaredType = item?.GetType() ?? typeof(object), Label = "[#" + i + "]", Value = item, Editable = false };
                throw new ArgumentException("index_out_of_range");
            }
            throw new ArgumentException("not_enumerable");
        }

        static Type ElementType(Type t)
        {
            if (t.IsArray) return t.GetElementType();
            foreach (var i in t.GetInterfaces()) if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>)) return i.GetGenericArguments()[0];
            return null;
        }

        // ---------- reading ----------
        internal static bool IsScalar(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Entity);

        internal static string Short(object v)
        {
            if (v == null) return "null";
            switch (v)
            {
                case string s: return "\"" + (s.Length > 160 ? s.Substring(0, 160) + "…" : s) + "\"";
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                case Entity e: return "Entity(" + e.Index + ":" + e.Version + ")";
                case IFormattable fm when v.GetType().IsPrimitive || v is decimal: return fm.ToString(null, CultureInfo.InvariantCulture);
            }
            var t = v.GetType();
            if (t.IsEnum) return v.ToString();
            if (v is ICollection c) return TypeName(t) + " [" + c.Count + "]";
            if (IsIndexable(v)) return TypeName(t) + " [" + Count(v) + "]";
            if (t.IsValueType && t.Namespace != null && t.Namespace.StartsWith("Unity.Mathematics", StringComparison.Ordinal)) return v.ToString();
            return TypeName(t);
        }

        internal static string TypeName(Type t)
        {
            if (t == null) return "?";
            if (!t.IsGenericType) return t.IsNested ? t.DeclaringType.Name + "." + t.Name : t.Name;
            var n = t.Name; int tick = n.IndexOf('`'); if (tick > 0) n = n.Substring(0, tick);
            return n + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
        }

        static string Kind(Type declared, object v)
        {
            var t = v?.GetType() ?? declared;
            if (v == null && !t.IsValueType) return "null";
            if (t == typeof(bool)) return "bool";
            if (t.IsEnum) return "enum";
            if (t == typeof(string)) return "string";
            if (t == typeof(Entity)) return "entity";
            if (t.IsPrimitive || t == typeof(decimal)) return "number";
            if (v is IDictionary) return "dict";
            if (v is IList || IsIndexable(v)) return "list";
            if (v is IEnumerable) return "set";
            return "object";
        }

        JObject Child(string path, string name, Type declared, object v, bool editable, bool isStatic = false, bool isProperty = false)
        {
            var t = v?.GetType() ?? declared;
            var o = new JObject { ["name"] = name, ["path"] = path, ["type"] = TypeName(declared), ["value"] = Short(v), ["kind"] = Kind(declared, v), ["editable"] = editable && (IsScalar(t) || v == null && declared == typeof(string)) };
            if (isStatic) o["static"] = true;
            if (isProperty) o["property"] = true;
            if (t.IsEnum) { o["options"] = new JArray(Enum.GetNames(t)); if (t.GetCustomAttributes(typeof(FlagsAttribute), false).Length > 0) o["flags"] = true; }
            if (v is Entity e && e != Entity.Null && world != null)
            {
                bool exists = false; try { exists = Em.Exists(e); } catch { }
                if (exists) { o["entity"] = "#" + e.Index + ":" + e.Version; o["entityLabel"] = Describe(e); }
            }
            if (v != null && !IsScalar(t)) o["expandable"] = true;
            return o;
        }

        internal JObject Inspect(string path, int offset, int limit, bool properties)
        {
            limit = Math.Max(1, Math.Min(500, limit)); offset = Math.Max(0, offset);
            var hops = Resolve(path);
            var last = hops[hops.Count - 1];
            var result = new JObject { ["path"] = path, ["label"] = last.Label, ["type"] = TypeName(last.Value?.GetType() ?? last.DeclaredType), ["value"] = Short(last.Value) };
            var children = new JArray(); int total = 0;
            bool staticRoot = path.TrimStart().StartsWith("@", StringComparison.Ordinal) && hops.Count == 1;
            bool entityRoot = path.TrimStart().StartsWith("#", StringComparison.Ordinal) && hops.Count == 1;
            if (entityRoot)
            {
                var e = last.Entity; result["entityLabel"] = Describe(e);
                foreach (var ct in ComponentsOf(e).OrderBy(c => c.GetManagedType()?.Name))
                {
                    var t = ct.GetManagedType(); if (t == null) continue;
                    if (total++ < offset || children.Count >= limit) continue;
                    bool buffer = typeof(IBufferElementData).IsAssignableFrom(t), tag = !buffer && IsZeroSize(t);
                    var row = new JObject { ["name"] = t.Name, ["path"] = path.Trim() + "." + t.Name, ["type"] = t.FullName, ["kind"] = buffer ? "buffer" : tag ? "tag" : "component", ["value"] = buffer ? "buffer" : tag ? "tag" : "", ["editable"] = false, ["assembly"] = t.Assembly.GetName().Name };
                    if (!tag) row["expandable"] = true;
                    if (buffer) { try { row["value"] = "buffer [" + Count(ComponentHop(e, t.Name).Value) + "]"; } catch { } }
                    children.Add(row);
                }
            }
            else if (staticRoot)
            {
                foreach (var f in Fields(last.DeclaredType, Stat)) { if (total++ < offset || children.Count >= limit) continue; object v = null; try { v = f.GetValue(null); } catch { } children.Add(Child(path + "." + f.Name, f.Name, f.FieldType, v, true, true)); }
                if (properties)
                    foreach (var p in last.DeclaredType.GetProperties(Stat | BindingFlags.DeclaredOnly).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
                    { if (total++ < offset || children.Count >= limit) continue; object v; try { v = p.GetValue(null); } catch (Exception e) { v = "<" + e.GetBaseException().GetType().Name + ">"; } children.Add(Child(path + "." + p.Name, p.Name, p.PropertyType, v, p.GetSetMethod(true) != null, true, true)); }
            }
            else
            {
                var v0 = last.Value;
                if (v0 is IDictionary dict)
                {
                    int i = 0; var args = dict.GetType().GetGenericArguments();
                    foreach (DictionaryEntry e in dict) { int n = i++; total++; if (n < offset || children.Count >= limit) continue; var c = Child(path + "[#" + n + "]", Short(e.Key), args.Length == 2 ? args[1] : typeof(object), e.Value, true); c["key"] = Short(e.Key); children.Add(c); }
                }
                else if (v0 is IList list)
                {
                    total = list.Count; var et = ElementType(list.GetType()) ?? typeof(object);
                    for (int i = offset; i < list.Count && children.Count < limit; i++) children.Add(Child(path + "[" + i + "]", "[" + i + "]", et, list[i], !list.IsReadOnly));
                }
                else if (IsIndexable(v0))
                {
                    var idx = Indexer(v0.GetType()); total = Count(v0); bool writable = idx.GetSetMethod(true) != null;
                    for (int i = offset; i < total && children.Count < limit; i++) children.Add(Child(path + "[" + i + "]", "[" + i + "]", idx.PropertyType, idx.GetValue(v0, new object[] { i }), writable));
                }
                else if (v0 is IEnumerable en && !(v0 is string))
                {
                    int i = 0;
                    foreach (var item in en) { int n = i++; total++; if (n < offset || children.Count >= limit) continue; children.Add(Child(path + "[#" + n + "]", "[#" + n + "]", item?.GetType() ?? typeof(object), item, false)); }
                }
                else if (v0 != null && !IsScalar(v0.GetType()))
                {
                    var t = v0.GetType(); bool own = !IsFramework(t.Assembly);
                    foreach (var f in Fields(t, Inst)) { if (total++ < offset || children.Count >= limit) continue; object v; try { v = f.GetValue(v0); } catch { v = null; } children.Add(Child(path + "." + f.Name, f.Name, f.FieldType, v, last.Editable || last.Set != null || !t.IsValueType || hops.Count > 1)); }
                    if (own && !t.IsValueType)
                        foreach (var f in Fields(t, Stat)) { if (total++ < offset || children.Count >= limit) continue; object v; try { v = f.GetValue(null); } catch { v = null; } children.Add(Child("@" + RootName(t) + "." + f.Name, "static " + f.Name, f.FieldType, v, true, true)); }
                    if (properties)
                        foreach (var p in t.GetProperties(Inst).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
                        { if (total++ < offset || children.Count >= limit) continue; object v; try { v = p.GetValue(v0); } catch (Exception e) { v = "<" + e.GetBaseException().GetType().Name + ">"; } children.Add(Child(path + "." + p.Name, p.Name, p.PropertyType, v, p.GetSetMethod(true) != null, false, true)); }
                }
            }
            result["children"] = children; result["total"] = total; result["offset"] = offset;
            return result;
        }

        // ---------- writing ----------
        internal static object Convert(string raw, Type t)
        {
            raw = raw ?? "";
            var u = Nullable.GetUnderlyingType(t);
            if (u != null) { if (raw.Trim().Length == 0 || raw.Trim() == "null") return null; t = u; }
            if (t == typeof(string)) return raw == "null" ? null : raw;
            var s = raw.Trim();
            if (t == typeof(bool)) { if (s == "1") return true; if (s == "0") return false; return bool.Parse(s); }
            if (t.IsEnum)
            {
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return Enum.ToObject(t, n);
                return Enum.Parse(t, s.Replace("|", ","), true); // "A|B" or "A, B" for [Flags]
            }
            if (t == typeof(Entity)) return ParseEntity(s);
            if (t == typeof(char)) return s.Length > 0 ? s[0] : '\0';
            if (t.IsPrimitive || t == typeof(decimal))
            {
                var d = decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
                bool fractional = t == typeof(float) || t == typeof(double) || t == typeof(decimal);
                if (!fractional && d != decimal.Truncate(d)) throw new ArgumentException("whole_number_required");
                return global::System.Convert.ChangeType(d, t, CultureInfo.InvariantCulture);
            }
            throw new ArgumentException("not_editable_type:" + TypeName(t));
        }

        // Sets the leaf and writes value types back up the chain (struct fields, components via SetComponentData).
        internal object Set(string path, string raw, out object old)
        {
            var hops = Resolve(path);
            var leaf = hops[hops.Count - 1];
            if (leaf.Set == null || !leaf.Editable) throw new InvalidOperationException("read_only");
            var target = leaf.Value?.GetType() ?? leaf.DeclaredType;
            if (!IsScalar(target) && !(leaf.Value == null && leaf.DeclaredType == typeof(string))) throw new InvalidOperationException("not_a_scalar");
            var value = Convert(raw, leaf.DeclaredType.IsEnum || leaf.DeclaredType == typeof(object) ? target : leaf.DeclaredType);
            old = leaf.Value;
            leaf.Set(leaf.Holder, value);
            for (int i = hops.Count - 2; i >= 1; i--)
            {
                var h = hops[i];
                if (h.Value == null || !h.Value.GetType().IsValueType) break;
                if (IsIndexable(h.Value)) break; // native containers alias their memory: the element write already landed
                if (h.Set == null) throw new InvalidOperationException("struct_parent_read_only");
                h.Set(h.Holder, h.Value);
            }
            return value;
        }

        // ---------- methods ----------
        internal IEnumerable<MethodInfo> Methods(Type t, bool isStatic)
        {
            var flags = (isStatic ? Stat : Inst | Stat) | BindingFlags.DeclaredOnly;
            for (var cur = t; cur != null && cur != typeof(object) && (cur == t || !IsFramework(cur.Assembly)); cur = cur.BaseType)
                foreach (var m in cur.GetMethods(flags))
                    if (!m.IsSpecialName && !m.IsGenericMethodDefinition && !m.Name.Contains("<") && !IsLifecycle(m.Name)) yield return m;
        }

        static bool IsLifecycle(string n) => n == "OnCreate" || n == "OnDestroy" || n == "OnUpdate" || n == "OnStartRunning" || n == "OnStopRunning" || n == "OnGameLoaded" || n == "OnGamePreload" || n == "OnGameLoadingComplete" || n == "OnCreateForCompiler" || n == "__AssignQueries";
        static bool ParamSupported(ParameterInfo p) { var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType; return p.IsOut || IsScalar(t); }
        internal static string Signature(MethodInfo m) => (m.IsStatic ? "static " : "") + TypeName(m.ReturnType) + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + TypeName(p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType) + " " + p.Name)) + ")";

        Type MethodTarget(string path, out object instance)
        {
            var hops = Resolve(path); var last = hops[hops.Count - 1];
            bool staticOnly = path.TrimStart().StartsWith("@", StringComparison.Ordinal) && hops.Count == 1;
            instance = staticOnly ? null : last.Value;
            var t = staticOnly ? last.DeclaredType : last.Value?.GetType();
            if (t == null) throw new InvalidOperationException("null_target");
            if (path.TrimStart().StartsWith("#", StringComparison.Ordinal) && hops.Count <= 2) throw new InvalidOperationException("entities_have_no_methods: open a system instead");
            return t;
        }

        internal JObject ListMethods(string path, string filter)
        {
            var t = MethodTarget(path, out var inst); bool staticOnly = inst == null;
            var list = new JArray(); int i = 0;
            foreach (var m in Methods(t, staticOnly).OrderBy(m => m.Name))
            {
                int id = i++; if (!Match(m.Name, filter)) continue;
                list.Add(new JObject { ["id"] = id, ["name"] = m.Name, ["signature"] = Signature(m), ["callable"] = m.GetParameters().All(ParamSupported) && !m.ContainsGenericParameters,
                    ["params"] = new JArray(m.GetParameters().Select(p => new JObject { ["name"] = p.Name, ["type"] = TypeName(p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType), ["out"] = p.IsOut })) });
            }
            return new JObject { ["path"] = path, ["type"] = TypeName(t), ["methods"] = list };
        }

        internal JObject Invoke(string path, int id, JArray args)
        {
            var t = MethodTarget(path, out var inst);
            var m = Methods(t, inst == null).OrderBy(x => x.Name).ElementAtOrDefault(id) ?? throw new ArgumentException("no_method");
            var ps = m.GetParameters(); var values = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType() : ps[i].ParameterType;
                if (ps[i].IsOut) { values[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null; continue; }
                if (!IsScalar(pt)) throw new ArgumentException("unsupported_parameter:" + ps[i].Name);
                values[i] = Convert(args != null && i < args.Count ? (string)args[i] : "", pt);
            }
            var ret = m.Invoke(m.IsStatic ? null : inst, values);
            var outs = new JObject();
            for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef) outs[ps[i].Name] = Short(values[i]);
            return new JObject { ["method"] = Signature(m), ["returned"] = m.ReturnType == typeof(void) ? "void" : Short(ret), ["outs"] = outs };
        }

        // ---------- search ----------
        // mode "name": field names containing the query. mode "value": scalar values equal to a number (within
        // tolerance) or text containing the query. scope: "systems" (default), "statics", or an entity/object path.
        internal JArray Search(string query, string mode, string scope, string assembly, int maxDepth, int limit)
        {
            var hits = new JArray(); if (string.IsNullOrWhiteSpace(query)) return hits;
            bool byValue = mode == "value";
            bool numeric = double.TryParse(query, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
            double tolerance = Math.Max(1e-6, Math.Abs(number) * 1e-5);
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            bool Hit(string name, object v)
            {
                if (!byValue) return Match(name, query);
                if (v == null) return false;
                if (numeric && (v is IConvertible) && !(v is string) && !(v is bool) && !(v is char))
                { try { return Math.Abs(global::System.Convert.ToDouble(v, CultureInfo.InvariantCulture) - number) <= tolerance; } catch { return false; } }
                if (v is Entity e) return Short(e).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                return (v is string || v.GetType().IsEnum) && v.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            void Walk(object obj, string path, int depth)
            {
                if (obj == null || hits.Count >= limit || depth > maxDepth) return;
                var t = obj.GetType();
                if (!t.IsValueType && !visited.Add(obj)) return;
                if (IsScalar(t)) return;
                if (obj is IList list) { for (int i = 0; i < list.Count && i < 256 && hits.Count < limit; i++) { var v = list[i]; if (v != null && IsScalar(v.GetType())) { if (Hit("[" + i + "]", v)) hits.Add(Child(path + "[" + i + "]", "[" + i + "]", v.GetType(), v, true)); } else Walk(v, path + "[" + i + "]", depth + 1); } return; }
                if (obj is IDictionary dict) { int i = 0; foreach (DictionaryEntry e in dict) { if (i >= 256 || hits.Count >= limit) break; var v = e.Value; if (v != null && IsScalar(v.GetType())) { if (Hit(Short(e.Key), v) || (byValue && Hit("", e.Key))) hits.Add(Child(path + "[#" + i + "]", Short(e.Key), v.GetType(), v, true)); } else Walk(v, path + "[#" + i + "]", depth + 1); i++; } return; }
                if (obj is IEnumerable || IsFramework(t.Assembly) && !t.IsValueType) return;
                foreach (var f in Fields(t, Inst))
                {
                    if (hits.Count >= limit) return;
                    object v; try { v = f.GetValue(obj); } catch { continue; }
                    var p = path + "." + f.Name;
                    bool scalar = v == null ? IsScalar(f.FieldType) : IsScalar(v.GetType());
                    if (scalar ? Hit(f.Name, v) : (!byValue && Match(f.Name, query))) hits.Add(Child(p, f.Name, f.FieldType, v, true));
                    if (!scalar && v != null && !(v is ComponentSystemBase)) Walk(v, p, depth + 1);
                }
            }
            if (!string.IsNullOrEmpty(scope) && scope != "systems" && scope != "statics")
            {
                var root = Resolve(scope).Last();
                if (scope.TrimStart().StartsWith("#", StringComparison.Ordinal) && root.Component == null)
                    foreach (var ct in ComponentsOf(root.Entity)) { var t = ct.GetManagedType(); if (t == null) continue; try { var h = ComponentHop(root.Entity, t.Name); Walk(h.Value, scope.Trim() + "." + t.Name, 0); } catch { } }
                else Walk(root.Value, scope.Trim(), 0);
                return hits;
            }
            if (scope != "statics")
                foreach (var kv in systems.Where(k => k.Key == k.Value.GetType().FullName).OrderBy(k => k.Key))
                {
                    if (assembly != null && !string.Equals(kv.Value.GetType().Assembly.GetName().Name, assembly, StringComparison.OrdinalIgnoreCase)) continue;
                    var t = kv.Value.GetType(); Walk(kv.Value, systems.ContainsKey(t.Name) ? t.Name : t.FullName, 0);
                    if (hits.Count >= limit) break;
                }
            if (scope == "statics" || scope == null || scope == "systems" && !byValue)
                foreach (var t in types.Values.Distinct().OrderBy(t => t.FullName))
                {
                    if (hits.Count >= limit) break;
                    if (assembly != null && !string.Equals(t.Assembly.GetName().Name, assembly, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var f in Fields(t, Stat))
                    {
                        if (hits.Count >= limit) break;
                        object v; try { v = f.GetValue(null); } catch { continue; }
                        bool scalar = v == null ? IsScalar(f.FieldType) : IsScalar(v.GetType());
                        if (scalar ? Hit(f.Name, v) : (!byValue && Match(f.Name, query))) hits.Add(Child("@" + RootName(t) + "." + f.Name, RootName(t) + "." + f.Name, f.FieldType, v, true, true));
                    }
                }
            return hits;
        }

        sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
