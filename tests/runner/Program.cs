using System;
using System.Linq;
using HomelandSecurity.Immigration;
using Newtonsoft.Json.Linq;
using CS2DevTools;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "") { if (ok) pass++; else { fail++; Console.WriteLine("FAIL: " + name + " " + detail); } }
string V(Engine e, string path) => Engine.Short(e.Resolve(path).Last().Value);

var standing = new StandingSystem();
var e = new Engine();
e.UseForTests(new[] { typeof(StandingSystem).Assembly }, new object[] { standing });

var rootsObj = e.Roots("", null, true, 0, 500); var roots = (JArray)rootsObj["roots"];
Check("roots include system", roots.Any(r => (string)r["path"] == "StandingSystem"));
Check("roots include static type", roots.Any(r => (string)r["path"] == "@BoomLaws"));

// static auto-property backing field (how Presidente stores Approval)
e.Set("@StandingSystem.<Approval>k__BackingField", "71.5", out var old);
Check("static backing field set", StandingSystem.Approval == 71.5f, V(e, "@StandingSystem.<Approval>k__BackingField"));
Check("old value reported", Engine.Short(old) == "50");
e.Set("@StandingSystem.Approval", "60", out _);
Check("static property with private setter", StandingSystem.Approval == 60f);
e.Set("@BoomLaws.s_TariffPct", "0", out _);
Check("static class field", V(e, "@BoomLaws.s_TariffPct") == "0");

// instance scalars, enum, string, array element
e.Set("StandingSystem.m_PublicApproval", "88", out _);
Check("private float", V(e, "StandingSystem.m_PublicApproval") == "88");
e.Set("StandingSystem.m_Mood[5]", "-12.5", out _);
Check("array element", V(e, "StandingSystem.m_Mood[5]") == "-12.5");
e.Set("StandingSystem.m_Mode", "full", out _);
Check("enum by name", V(e, "StandingSystem.m_Mode") == "Full");
e.Set("StandingSystem.m_Mode", "0", out _);
Check("enum by number", V(e, "StandingSystem.m_Mode") == "Off");
e.Set("StandingSystem.m_Name", "Yerba Point", out _);
Check("string", V(e, "StandingSystem.m_Name") == "\"Yerba Point\"");

// private nested class in a list
e.Set("StandingSystem.m_Events[0].V", "9", out _);
Check("list of private class", V(e, "StandingSystem.m_Events[0].V") == "9");

// structs: inside array, as a field, and inside a class
e.Set("StandingSystem.m_Points[1].X", "3.25", out _);
Check("struct in array writes back", V(e, "StandingSystem.m_Points[1].X") == "3.25");
e.Set("StandingSystem.m_Single.Y", "7", out _);
Check("struct field writes back", V(e, "StandingSystem.m_Single.Y") == "7");
e.Set("StandingSystem.m_Inner.P.X", "1.5", out _);
Check("struct inside class", V(e, "StandingSystem.m_Inner.P.X") == "1.5");

// dictionary by ordinal
var dict = e.Inspect("StandingSystem.m_On", 0, 50, false);
Check("dict children", ((JArray)dict["children"]).Count == 2);
e.Set("StandingSystem.m_On[#0]", "false", out _);
Check("dict value set", V(e, "StandingSystem.m_On[#0]") == "false");

// errors
bool threw(Action a) { try { a(); return false; } catch { return true; } }
Check("whole numbers only for int", threw(() => e.Set("StandingSystem.m_Inner.Isolation", "1.5", out _)));
Check("object not settable", threw(() => e.Set("StandingSystem.m_Inner", "x", out _)));
Check("unknown member", threw(() => e.Resolve("StandingSystem.m_Nope")));
Check("bad index", threw(() => e.Resolve("StandingSystem.m_Mood[99]")));

// inspect object shows fields and flags
var ins = e.Inspect("StandingSystem", 0, 100, false);
var kids = (JArray)ins["children"];
Check("inspect lists fields", kids.Any(c => (string)c["name"] == "m_Events" && (bool?)c["expandable"] == true));
Check("enum options", kids.Any(c => (string)c["name"] == "m_Mode" && c["options"] != null));
Check("static field shown on system", kids.Any(c => (string)c["path"] == "@StandingSystem.<Approval>k__BackingField"));
var paged = e.Inspect("StandingSystem.m_Mood", 10, 5, false);
Check("paging", ((JArray)paged["children"]).Count == 5 && (string)paged["children"][0]["name"] == "[10]" && (int)paged["total"] == 29);

// methods, including out parameters and static methods
var ms = (JArray)e.ListMethods("StandingSystem", "")["methods"];
var tf = ms.First(m => (string)m["name"] == "TryFound");
var r = e.Invoke("StandingSystem", (int)tf["id"], new JArray("Fort Johnson Silicon", ""));
Check("invoke with out param", (string)r["returned"] == "true" && ((string)r["outs"]["message"]).Contains("founded"), r.ToString());
Check("invoke changed state", standing.m_Moved);
var tw = ms.First(m => (string)m["name"] == "Twice");
Check("static method", (string)e.Invoke("StandingSystem", (int)tw["id"], new JArray("21"))["returned"] == "42");

// search
var hits = e.Search("Isolation", "name", null, null, 2, 50);
Check("search finds nested field", hits.Any(h => (string)h["path"] == "StandingSystem.m_Inner.Isolation"));
var hits2 = e.Search("TariffPct", "name", null, null, 2, 50);
Check("search finds static field", hits2.Any(h => (string)h["path"] == "@BoomLaws.s_TariffPct"));

// full type-name system paths, flags enums, value search, dotted static roots
e.Set("HomelandSecurity.Immigration.StandingSystem.m_PublicApproval", "55", out _);
Check("full-name system path", V(e, "StandingSystem.m_PublicApproval") == "55");
e.Set("StandingSystem.m_Traits", "Loud|Old", out _);
Check("flags enum A|B", V(e, "StandingSystem.m_Traits") == "Loud, Old", V(e, "StandingSystem.m_Traits"));
var vh = e.Search("1234.5", "value", null, null, 2, 50);
Check("value search number", vh.Any(h => (string)h["path"] == "StandingSystem.m_Budget"), vh.ToString());
var vs = e.Search("Yerba", "value", null, null, 2, 50);
Check("value search text", vs.Any(h => (string)h["path"] == "StandingSystem.m_Name"));
var scoped = e.Search("30", "value", "StandingSystem.m_Inner", null, 2, 50);
Check("scoped value search", scoped.Any(h => (string)h["path"] == "StandingSystem.m_Inner.Isolation"), scoped.ToString());
e.Set("@HomelandSecurity.Immigration.BoomLaws.s_TariffPct", "12", out _);
Check("dotted static root", V(e, "@BoomLaws.s_TariffPct") == "12");
Check("roots report assemblies", ((JArray)rootsObj["assemblies"]).Count == 1);
Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
