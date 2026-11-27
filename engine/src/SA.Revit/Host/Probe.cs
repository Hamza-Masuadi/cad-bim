using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SA.Core.Json;

namespace SA.Revit.Host
{
    /// <summary>
    /// The probe method: whether named API types and members exist in the running release, by reflection over the
    /// loaded RevitAPI and RevitAPIUI assemblies (and RevitAddInUtility if loaded). It only inspects metadata, so it
    /// runs on the pipe thread without an API context. Names: "Autodesk.Revit.DB.Wall", "Wall", "Wall.Create",
    /// "ElementId.Value", "Autodesk.Revit.DB.ElementId.IntegerValue".
    /// </summary>
    internal static class Probe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        public static JsonObject Run(JsonObject p)
        {
            var names = new List<string>();
            if (p["names"] is JsonArray a) names.AddRange(a.OfType<JsonString>().Select(s => s.Value));
            var asms = AppDomain.CurrentDomain.GetAssemblies()
                .Where(x => { var n = x.GetName().Name ?? ""; return n == "RevitAPI" || n == "RevitAPIUI" || n == "RevitAddInUtility"; })
                .ToList();
            var results = new JsonArray();
            foreach (var name in names.Take(500)) results.Add(One(name, asms));
            return new JsonObject()
                .Add("releaseYear", Compat.ReleaseInfo.Year)
                .Add("assemblies", new JsonArray(asms.Select(x => (JsonValue)(x.GetName().Name + " " + x.GetName().Version))))
                .Add("results", results);
        }

        private static JsonObject One(string name, List<Assembly> asms)
        {
            var o = new JsonObject().Add("name", name);
            var t = FindType(name, asms);
            if (t != null) return o.Add("exists", true).Add("kind", Kind(t)).Add("type", t.FullName ?? t.Name).Add("assembly", t.Assembly.GetName().Name ?? "")
                .Add("obsolete", t.GetCustomAttribute<ObsoleteAttribute>() is ObsoleteAttribute ot ? (JsonValue)(ot.Message ?? "obsolete") : JsonValue.Null);
            int dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                var owner = FindType(name.Substring(0, dot), asms);
                string member = name.Substring(dot + 1);
                if (owner != null)
                {
                    MemberInfo[] ms;
                    try { ms = owner.GetMember(member, All); } catch (Exception) { ms = new MemberInfo[0]; }
                    if (ms.Length > 0)
                    {
                        var sigs = new JsonArray();
                        foreach (var m in ms.Take(20)) sigs.Add(Signature(m));
                        var obs = ms.Select(m => m.GetCustomAttribute<ObsoleteAttribute>()).FirstOrDefault(x => x != null);
                        return o.Add("exists", true).Add("kind", ms[0].MemberType.ToString().ToLowerInvariant()).Add("type", owner.FullName ?? owner.Name)
                            .Add("signatures", sigs).Add("obsolete", obs != null ? (JsonValue)(obs.Message ?? "obsolete") : JsonValue.Null);
                    }
                    return o.Add("exists", false).Add("type", owner.FullName ?? owner.Name).Add("reason", "type exists, member not found");
                }
            }
            return o.Add("exists", false).Add("reason", "no such type in the loaded Revit API assemblies");
        }

        private static Type? FindType(string name, List<Assembly> asms)
        {
            foreach (var a in asms)
            {
                var t = a.GetType(name, false);
                if (t != null && t.IsPublic || t != null && t.IsNestedPublic) return t;
            }
            if (name.IndexOf('.') >= 0 && name.StartsWith("Autodesk.", StringComparison.Ordinal)) return null;
            foreach (var ns in new[] { "Autodesk.Revit.DB.", "Autodesk.Revit.UI.", "Autodesk.Revit.ApplicationServices.", "Autodesk.Revit.DB.Architecture.",
                                       "Autodesk.Revit.DB.Structure.", "Autodesk.Revit.DB.Mechanical.", "Autodesk.Revit.DB.Plumbing.", "Autodesk.Revit.DB.Electrical.",
                                       "Autodesk.Revit.DB.Events.", "Autodesk.Revit.UI.Events.", "Autodesk.Revit.Exceptions." })
            {
                foreach (var a in asms)
                {
                    Type? t = null;
                    try { t = a.GetType(ns + name, false); } catch (Exception) { }
                    if (t != null && (t.IsPublic || t.IsNestedPublic)) return t;
                }
            }
            return null;
        }

        private static string Kind(Type t) => t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class";

        private static string Signature(MemberInfo m)
        {
            switch (m)
            {
                case MethodInfo mi:
                    return (mi.IsStatic ? "static " : "") + mi.ReturnType.Name + " " + mi.Name + "(" + string.Join(", ", mi.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name)) + ")";
                case ConstructorInfo ci:
                    return "ctor(" + string.Join(", ", ci.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name)) + ")";
                case PropertyInfo pi:
                    return pi.PropertyType.Name + " " + pi.Name + " {" + (pi.CanRead ? " get;" : "") + (pi.CanWrite ? " set;" : "") + " }";
                case FieldInfo fi:
                    return fi.FieldType.Name + " " + fi.Name;
                case EventInfo ei:
                    return "event " + (ei.EventHandlerType?.Name ?? "") + " " + ei.Name;
                default:
                    return m.ToString() ?? m.Name;
            }
        }
    }
}
