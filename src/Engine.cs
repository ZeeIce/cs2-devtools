using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Unity.Entities;

namespace PresidenteDevTools
{
    // Path-addressed reflection over Presidente (HomelandSecurity) state. Nothing in Presidente's DLL is changed;
    // values are read and written on the live objects. Paths look like:
    //   StandingSystem.m_Mood[3]   @StandingSystem.<Approval>k__BackingField   TechRaceSystem.m_Foundries[0].m_Node
    //   SomeSystem.m_Map[#2]  (the third entry of a dictionary or set, in enumeration order)
    // A leading '@' addresses a type's static members.
    internal sealed class Engine
    {
        internal const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        internal Assembly Asm { get; private set; }
        readonly Dictionary<string, object> systems = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Type> types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        World world;

        internal bool Ready => Asm != null;

        internal void Refresh(World w)
        {
            world = w;
            if (Asm == null) Asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "HomelandSecurity");
            if (Asm == null) return;
            LoadTypes();
            systems.Clear();
            foreach (var s in w.Systems)
            {
                var t = s.GetType();
                if (t.Assembly == Asm && !systems.ContainsKey(t.Name)) systems[t.Name] = s;
            }
        }

        // Offline tests supply a stand-in assembly and system objects instead of an ECS world.
        internal void UseForTests(Assembly asm, IEnumerable<object> roots)
        {
            Asm = asm; LoadTypes(); systems.Clear();
            foreach (var r in roots) systems[r.GetType().Name] = r;
        }

        void LoadTypes()
        {
            if (types.Count == 0)
            {
                Type[] all;
                try { all = Asm.GetTypes(); } catch (ReflectionTypeLoadException e) { all = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in all)
                {
                    if (t.IsNested || t.Name.StartsWith("<", StringComparison.Ordinal)) continue;
                    if (!types.ContainsKey(t.Name)) types[t.Name] = t;
                }
            }
        }

        internal object System(string name) { systems.TryGetValue(name, out var s); return s; }
        internal Type TypeNamed(string name) { types.TryGetValue(name, out var t); return t; }

        internal JArray Roots(string filter)
        {
            var result = new JArray();
            foreach (var kv in systems.OrderBy(k => k.Key))
                if (Match(kv.Key, filter)) result.Add(new JObject { ["path"] = kv.Key, ["kind"] = "system", ["type"] = kv.Value.GetType().FullName, ["fields"] = Fields(kv.Value.GetType(), Inst).Count() });
            foreach (var kv in types.OrderBy(k => k.Key))
            {
                if (systems.ContainsKey(kv.Key) && !Fields(kv.Value, Stat).Any()) continue;
                int n = Fields(kv.Value, Stat).Count();
                if (n > 0 && Match(kv.Key, filter)) result.Add(new JObject { ["path"] = "@" + kv.Key, ["kind"] = "static", ["type"] = kv.Value.FullName, ["fields"] = n });
            }
            return result;
        }

        static bool Match(string s, string f) => string.IsNullOrEmpty(f) || s.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;

        // Fields declared by Presidente's own types (base game classes are skipped), or every field for other types.
        internal IEnumerable<FieldInfo> Fields(Type t, BindingFlags flags)
        {
            var seen = new HashSet<string>();
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                if (cur.Assembly != Asm && t.Assembly == Asm) break;
                foreach (var f in cur.GetFields(flags | BindingFlags.DeclaredOnly))
                    if (!f.IsLiteral && seen.Add(f.Name)) yield return f;
                if ((flags & BindingFlags.Static) != 0) break;
            }
        }

        // ---------- paths ----------
        abstract class Seg { }
        sealed class Member : Seg { public string Name; }
        sealed class Index : Seg { public int I; }
        sealed class Ordinal : Seg { public int I; }

        static List<Seg> Parse(string path, out string root, out bool isStatic)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("empty_path");
            path = path.Trim();
            isStatic = path.StartsWith("@", StringComparison.Ordinal);
            if (isStatic) path = path.Substring(1);
            var segs = new List<Seg>();
            int i = 0; root = ReadName(path, ref i);
            while (i < path.Length)
            {
                char c = path[i];
                if (c == '.') { i++; segs.Add(new Member { Name = ReadName(path, ref i) }); }
                else if (c == '[')
                {
                    int end = path.IndexOf(']', i);
                    if (end < 0) throw new ArgumentException("unclosed_index");
                    var inner = path.Substring(i + 1, end - i - 1).Trim(); i = end + 1;
                    if (inner.StartsWith("#", StringComparison.Ordinal)) segs.Add(new Ordinal { I = int.Parse(inner.Substring(1), CultureInfo.InvariantCulture) });
                    else segs.Add(new Index { I = int.Parse(inner, CultureInfo.InvariantCulture) });
                }
                else throw new ArgumentException("bad_path_at_" + i);
            }
            return segs;
        }

        static string ReadName(string p, ref int i)
        {
            int start = i;
            while (i < p.Length && p[i] != '.' && p[i] != '[') i++;
            if (i == start) throw new ArgumentException("empty_name");
            return p.Substring(start, i - start);
        }

        // One resolved hop: the holder, how to get/set the value from it, and the value.
        internal sealed class Hop
        {
            public object Holder; public Type DeclaredType; public object Value; public string Label;
            public Func<object, object> Get; public Action<object, object> Set; public bool Editable;
        }

        internal List<Hop> Resolve(string path)
        {
            var segs = Parse(path, out var rootName, out var isStatic);
            var hops = new List<Hop>();
            object cur; Type curType;
            if (isStatic)
            {
                curType = TypeNamed(rootName) ?? throw new ArgumentException("unknown_type:" + rootName);
                cur = null;
                if (segs.Count == 0) { hops.Add(new Hop { Value = null, DeclaredType = curType, Label = "@" + rootName }); return hops; }
                var m = (Member)(segs[0] as Member ?? throw new ArgumentException("static_needs_member"));
                hops.Add(new Hop { Value = null, DeclaredType = curType, Label = "@" + rootName });
                var hop = MemberHop(curType, null, m.Name, true);
                hops.Add(hop); cur = hop.Value; segs.RemoveAt(0);
            }
            else
            {
                cur = System(rootName) ?? throw new ArgumentException("unknown_system:" + rootName);
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

        Hop MemberHop(Type t, object holder, string name, bool isStatic)
        {
            var flags = isStatic ? Stat : Inst;
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                var f = cur.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (f != null)
                    return new Hop { Holder = holder, DeclaredType = f.FieldType, Label = name, Value = f.GetValue(holder), Get = h => f.GetValue(h), Set = (h, v) => f.SetValue(h, v), Editable = !f.IsLiteral };
                var p = cur.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0 && p.CanRead)
                {
                    var setter = p.GetSetMethod(true);
                    return new Hop { Holder = holder, DeclaredType = p.PropertyType, Label = name, Value = p.GetValue(holder), Get = h => p.GetValue(h), Set = setter == null ? null : (Action<object, object>)((h, v) => p.SetValue(h, v)), Editable = setter != null };
                }
            }
            throw new ArgumentException("no_member:" + name);
        }

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
                var et = ElementType(holder.GetType()) ?? typeof(object);
                return new Hop { Holder = holder, DeclaredType = et, Label = "[" + i + "]", Value = list[i], Get = h => ((IList)h)[i], Set = (h, v) => ((IList)h)[i] = v, Editable = !list.IsReadOnly };
            }
            throw new ArgumentException("not_indexable");
        }

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
            if (v is IList) return "list";
            if (v is IEnumerable) return "set";
            return "object";
        }

        JObject Child(string path, string name, Type declared, object v, bool editable, bool isStatic = false, bool isProperty = false)
        {
            var t = v?.GetType() ?? declared;
            var o = new JObject { ["name"] = name, ["path"] = path, ["type"] = TypeName(declared), ["value"] = Short(v), ["kind"] = Kind(declared, v), ["editable"] = editable && (IsScalar(t) || v == null && declared == typeof(string)) };
            if (isStatic) o["static"] = true;
            if (isProperty) o["property"] = true;
            if (t.IsEnum) o["options"] = new JArray(Enum.GetNames(t));
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
            bool staticRoot = path.StartsWith("@", StringComparison.Ordinal) && hops.Count == 1;
            if (staticRoot)
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
                    int i = 0;
                    foreach (DictionaryEntry e in dict) { int n = i++; total++; if (n < offset || children.Count >= limit) continue; var args = dict.GetType().GetGenericArguments(); var c = Child(path + "[#" + n + "]", Short(e.Key), args.Length == 2 ? args[1] : typeof(object), e.Value, true); c["key"] = Short(e.Key); children.Add(c); }
                }
                else if (v0 is IList list)
                {
                    total = list.Count; var et = ElementType(list.GetType()) ?? typeof(object);
                    for (int i = offset; i < list.Count && children.Count < limit; i++) children.Add(Child(path + "[" + i + "]", "[" + i + "]", et, list[i], !list.IsReadOnly));
                }
                else if (v0 is IEnumerable en && !(v0 is string))
                {
                    int i = 0;
                    foreach (var item in en) { int n = i++; total++; if (n < offset || children.Count >= limit) continue; children.Add(Child(path + "[#" + n + "]", "[#" + n + "]", item?.GetType() ?? typeof(object), item, false)); }
                }
                else if (v0 != null && !IsScalar(v0.GetType()))
                {
                    var t = v0.GetType();
                    foreach (var f in Fields(t, Inst)) { if (total++ < offset || children.Count >= limit) continue; object v; try { v = f.GetValue(v0); } catch { v = null; } children.Add(Child(path + "." + f.Name, f.Name, f.FieldType, v, true)); }
                    if (t.Assembly == Asm)
                        foreach (var f in Fields(t, Stat)) { if (total++ < offset || children.Count >= limit) continue; object v; try { v = f.GetValue(null); } catch { v = null; } children.Add(Child("@" + t.Name + "." + f.Name, "static " + f.Name, f.FieldType, v, true, true)); }
                    if (properties)
                        foreach (var p in t.GetProperties(Inst).Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && (p.DeclaringType.Assembly == Asm || t.Assembly != Asm)))
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
            if (t.IsEnum) return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Enum.ToObject(t, n) : Enum.Parse(t, s, true);
            if (t == typeof(Entity))
            {
                var parts = s.Replace("Entity(", "").Replace(")", "").Split(':');
                return new Entity { Index = int.Parse(parts[0], CultureInfo.InvariantCulture), Version = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1 };
            }
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

        // Sets the leaf and writes boxed structs back up the chain so edits inside value types stick.
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
                if (h.Set == null) throw new InvalidOperationException("struct_parent_read_only");
                h.Set(h.Holder, h.Value);
            }
            return value;
        }

        // ---------- methods ----------
        internal IEnumerable<MethodInfo> Methods(Type t, bool isStatic)
        {
            var flags = (isStatic ? Stat : Inst | Stat) | BindingFlags.DeclaredOnly;
            for (var cur = t; cur != null && cur.Assembly == Asm; cur = cur.BaseType)
                foreach (var m in cur.GetMethods(flags))
                    if (!m.IsSpecialName && !m.IsGenericMethodDefinition && !m.Name.Contains("<") && !IsLifecycle(m.Name))
                        yield return m;
        }

        static bool IsLifecycle(string n) => n == "OnCreate" || n == "OnDestroy" || n == "OnUpdate" || n == "OnStartRunning" || n == "OnStopRunning" || n == "OnGameLoaded" || n == "OnGamePreload" || n == "OnGameLoadingComplete";

        static bool ParamSupported(ParameterInfo p) { var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType; return p.IsOut || IsScalar(t); }

        internal static string Signature(MethodInfo m) => (m.IsStatic ? "static " : "") + TypeName(m.ReturnType) + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + TypeName(p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType) + " " + p.Name)) + ")";

        internal JObject ListMethods(string path, string filter)
        {
            var hops = Resolve(path); var last = hops[hops.Count - 1];
            bool staticOnly = path.StartsWith("@", StringComparison.Ordinal) && hops.Count == 1;
            var t = staticOnly ? last.DeclaredType : last.Value?.GetType();
            if (t == null) throw new InvalidOperationException("null_target");
            var list = new JArray(); int i = 0;
            foreach (var m in Methods(t, staticOnly).OrderBy(m => m.Name))
            {
                int id = i++;
                if (!Match(m.Name, filter)) continue;
                list.Add(new JObject { ["id"] = id, ["name"] = m.Name, ["signature"] = Signature(m), ["callable"] = m.GetParameters().All(ParamSupported) && !m.ContainsGenericParameters,
                    ["params"] = new JArray(m.GetParameters().Select(p => new JObject { ["name"] = p.Name, ["type"] = TypeName(p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType), ["out"] = p.IsOut })) });
            }
            return new JObject { ["path"] = path, ["type"] = TypeName(t), ["methods"] = list };
        }

        internal JObject Invoke(string path, int id, JArray args)
        {
            var hops = Resolve(path); var last = hops[hops.Count - 1];
            bool staticOnly = path.StartsWith("@", StringComparison.Ordinal) && hops.Count == 1;
            var t = staticOnly ? last.DeclaredType : last.Value?.GetType();
            var m = Methods(t, staticOnly).OrderBy(x => x.Name).ElementAtOrDefault(id) ?? throw new ArgumentException("no_method");
            var ps = m.GetParameters(); var values = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType() : ps[i].ParameterType;
                if (ps[i].IsOut) { values[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null; continue; }
                if (!IsScalar(pt)) throw new ArgumentException("unsupported_parameter:" + ps[i].Name);
                values[i] = Convert(args != null && i < args.Count ? (string)args[i] : "", pt);
            }
            var ret = m.Invoke(m.IsStatic ? null : last.Value, values);
            var outs = new JObject();
            for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsByRef) outs[ps[i].Name] = Short(values[i]);
            return new JObject { ["method"] = Signature(m), ["returned"] = m.ReturnType == typeof(void) ? "void" : Short(ret), ["outs"] = outs };
        }

        // ---------- search ----------
        internal JArray Search(string query, int maxDepth, int limit)
        {
            var hits = new JArray();
            if (string.IsNullOrWhiteSpace(query)) return hits;
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void Walk(object obj, string path, int depth)
            {
                if (obj == null || hits.Count >= limit || depth > maxDepth || !visited.Add(obj)) return;
                var t = obj.GetType();
                if (IsScalar(t) || obj is IEnumerable) return;
                foreach (var f in Fields(t, Inst))
                {
                    if (hits.Count >= limit) return;
                    object v; try { v = f.GetValue(obj); } catch { continue; }
                    var p = path + "." + f.Name;
                    if (Match(f.Name, query)) hits.Add(Child(p, f.Name, f.FieldType, v, true));
                    if (v != null && !IsScalar(v.GetType()) && !(v is IEnumerable) && v.GetType().Assembly == Asm && !(v is ComponentSystemBase)) Walk(v, p, depth + 1);
                }
            }
            foreach (var kv in systems.OrderBy(k => k.Key)) Walk(kv.Value, kv.Key, 0);
            foreach (var kv in types.OrderBy(k => k.Key))
                foreach (var f in Fields(kv.Value, Stat))
                {
                    if (hits.Count >= limit) break;
                    if (!Match(f.Name, query)) continue;
                    object v; try { v = f.GetValue(null); } catch { continue; }
                    hits.Add(Child("@" + kv.Key + "." + f.Name, kv.Key + "." + f.Name, f.FieldType, v, true, true));
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
