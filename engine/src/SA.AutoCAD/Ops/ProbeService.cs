using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SA.AutoCAD.Host;
using SA.Core.Json;
using SA.Core.Protocol;

namespace SA.AutoCAD.Ops
{
    /// <summary>
    /// The probe method: reports whether named types and members exist in the AutoCAD assemblies loaded in this process,
    /// by reflection only (no AutoCAD call, so it runs on the pipe thread). Accepted names:
    /// "Namespace.Type", "Type" (short name; every match is listed), "Type.Member" and "Namespace.Type.Member".
    /// Assemblies searched: those whose name starts with "Ac", "Ad" or "Autodesk" (acdbmgd, acmgd, accoremgd, AcWindows...).
    /// </summary>
    internal static class ProbeService
    {
        private const int MaxNames = 200;
        private const int MaxMembers = 20;
        /// <summary>The loaded Autodesk types of one probe call, by full and by short name (built per call, never shared).</summary>
        private sealed class TypeIndex
        {
            public readonly Dictionary<string, Type> ByFullName = new Dictionary<string, Type>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<Type>> ByShortName = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
        }

        private static bool IsAutodesk(Assembly a)
        {
            string n = a.GetName().Name ?? "";
            return n.StartsWith("Ac", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Ad", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Autodesk", StringComparison.OrdinalIgnoreCase);
        }

        private static TypeIndex Index()
        {
            var index = new TypeIndex();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Where(IsAutodesk))
            {
                Type[] types;
                try { types = a.GetExportedTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
                catch (Exception) { continue; }
                foreach (var t in types)
                {
                    if (t?.FullName is null) continue;
                    string fn = t.FullName.Replace('+', '.');
                    if (!index.ByFullName.ContainsKey(fn)) index.ByFullName.Add(fn, t);
                    if (!index.ByShortName.TryGetValue(t.Name, out var list)) index.ByShortName[t.Name] = list = new List<Type>();
                    list.Add(t);
                }
            }
            return index;
        }

        private static List<Type> FindTypes(TypeIndex index, string name)
        {
            if (index.ByFullName.TryGetValue(name, out var t)) return new List<Type> { t };
            if (name.IndexOf('.') < 0 && index.ByShortName.TryGetValue(name, out var list)) return list;
            return new List<Type>();
        }

        internal static JsonValue Probe(JsonObject p)
        {
            if (!(p["names"] is JsonArray names) || names.Count == 0)
                throw new EngineException(ErrorCodes.BadRequest, "params.names must be a non-empty list of Type or Type.Member names.");
            if (names.Count > MaxNames) throw new EngineException(ErrorCodes.BadRequest, "At most " + MaxNames + " names per probe.");
            // Assemblies can load later (demand-loaded modules), so each probe indexes afresh; concurrent probes share nothing.
            var index = Index();
            var results = new JsonArray();
            foreach (var v in names)
            {
                string name = (v as JsonString)?.Value?.Trim() ?? "";
                results.Add(ProbeOne(index, name));
            }
            return new JsonObject().Add("engine", EngineInfo.Describe()).Add("results", results);
        }

        private static JsonObject ProbeOne(TypeIndex index, string name)
        {
            var o = new JsonObject().Add("name", name);
            if (name.Length == 0) return o.Add("exists", false).Add("kind", "invalid");
            var types = FindTypes(index, name);
            if (types.Count > 0)
                return o.Add("exists", true).Add("kind", "type").Add("types", new JsonArray(types.Select(t => (JsonValue)TypeJson(t))));
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1) return o.Add("exists", false).Add("kind", "missing");
            string typePart = name.Substring(0, dot);
            string member = name.Substring(dot + 1);
            var owners = FindTypes(index, typePart);
            if (owners.Count == 0) return o.Add("exists", false).Add("kind", "missing").Add("reason", "type '" + typePart + "' not found");
            var found = new JsonArray();
            foreach (var t in owners)
            {
                MemberInfo[] members;
                try { members = t.GetMember(member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy); }
                catch (Exception) { continue; }
                foreach (var m in members.Take(MaxMembers))
                    found.Add(new JsonObject().Add("type", t.FullName ?? t.Name).Add("assembly", t.Assembly.GetName().Name ?? "")
                        .Add("memberType", m.MemberType.ToString()).Add("signature", m.ToString() ?? member));
            }
            return o.Add("exists", found.Count > 0).Add("kind", found.Count > 0 ? "member" : "missing").Add("members", found);
        }

        private static JsonObject TypeJson(Type t) => new JsonObject()
            .Add("fullName", t.FullName ?? t.Name)
            .Add("assembly", t.Assembly.GetName().Name ?? "")
            .Add("assemblyVersion", t.Assembly.GetName().Version?.ToString() ?? "")
            .Add("kind", t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class");
    }
}
