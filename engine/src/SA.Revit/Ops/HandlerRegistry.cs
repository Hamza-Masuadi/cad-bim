using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SA.Core.Jobs;

namespace SA.Revit.Ops
{
    /// <summary>
    /// Finds every concrete <see cref="IOpHandler"/> with a parameterless constructor in this assembly and matches
    /// it to Core's Revit op schemas. A handler whose name has no schema is ignored (and logged); a schema without a
    /// handler is listed in ping and refused at validation with issue code no_handler.
    /// </summary>
    internal sealed class HandlerRegistry
    {
        private readonly Dictionary<string, IOpHandler> _handlers = new Dictionary<string, IOpHandler>(StringComparer.Ordinal);

        public HandlerRegistry(OpRegistry schemas)
        {
            Schemas = schemas;
            foreach (var t in SafeTypes(typeof(HandlerRegistry).Assembly))
            {
                if (t.IsAbstract || t.IsInterface || !typeof(IOpHandler).IsAssignableFrom(t)) continue;
                if (t.GetConstructor(Type.EmptyTypes) is null) { Problems.Add(t.FullName + ": no parameterless constructor"); continue; }
                IOpHandler h;
                try { h = (IOpHandler)Activator.CreateInstance(t)!; }
                catch (Exception e) { Problems.Add(t.FullName + ": " + e.Message); continue; }
                if (schemas.Find(h.Name, AppKind.Revit) is null) { Problems.Add(t.FullName + ": op '" + h.Name + "' has no Revit schema in Core"); continue; }
                if (_handlers.ContainsKey(h.Name)) { Problems.Add(t.FullName + ": a second handler for '" + h.Name + "'"); continue; }
                _handlers.Add(h.Name, h);
            }
        }

        public OpRegistry Schemas { get; }
        public List<string> Problems { get; } = new List<string>();
        public IReadOnlyCollection<string> Names => _handlers.Keys;

        public IOpHandler? Find(string op) => _handlers.TryGetValue(op, out var h) ? h : null;

        public List<string> Missing() =>
            Schemas.ForApp(AppKind.Revit).Select(s => s.Name).Where(n => !_handlers.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();

        internal static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).Select(t => t!); }
        }
    }
}
