using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SA.Core.Jobs;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops
{
    /// <summary>The handler contract (ARCHITECTURE section 7), with typed args from Core.</summary>
    internal interface IOpHandler
    {
        /// <summary>The op name exactly as declared in SA.Core.Jobs.Ops.AutoCadOps.</summary>
        string Name { get; }

        /// <summary>True only for ops that may run on a drawing whose INSUNITS is 0 (ops that set INSUNITS).</summary>
        bool AllowsUnitlessDrawing { get; }

        /// <summary>Document-level checks. No changes. Args may still contain "$ref" strings: see <see cref="OpContext.IsRef"/>.</summary>
        void Validate(OpContext ctx, OpArgs args, List<Issue> issues);

        /// <summary>Runs inside the job's transaction with references resolved. Returns the result with readback; Ok only when read back.</summary>
        OpResult Execute(OpContext ctx, OpArgs args);
    }

    /// <summary>
    /// Base class for every AutoCAD op handler. Execute is split in two so that readback cannot be forgotten:
    /// <see cref="Apply"/> makes the change, fills Created/Modified/Unchanged and Outputs, and returns a plan object
    /// describing what it intended (any type the handler likes); <see cref="Readback"/> re-reads what was made through the
    /// job transaction, fills Readback in job units and returns whether it matches the plan. The op is ok only if
    /// Readback returns true. Handlers are instantiated once and called only on the main thread; keep no per-job
    /// state in fields: pass it through the plan.
    /// </summary>
    internal abstract class OpHandler<TPlan> : IOpHandler where TPlan : class
    {
        public abstract string Name { get; }

        public virtual bool AllowsUnitlessDrawing => false;

        public abstract void Validate(OpContext ctx, OpArgs args, List<Issue> issues);

        protected abstract TPlan Apply(OpContext ctx, OpArgs args, OpResult result);

        /// <summary>Return false (and say why with <see cref="Mismatch"/>) when what was read back differs from the plan.</summary>
        protected abstract bool Readback(OpContext ctx, OpArgs args, OpResult result, TPlan plan);

        public OpResult Execute(OpContext ctx, OpArgs args)
        {
            var result = new OpResult(ctx.CurrentOp.Id, Name);
            var plan = Apply(ctx, args, result);
            if (result.Error != null) { result.Ok = false; return result; }
            result.Ok = Readback(ctx, args, result, plan);
            if (!result.Ok && result.Error is null)
                result.Error = new OpError(IssueCodes.ReadbackMismatch, "What was read back from the drawing does not match the request.");
            return result;
        }

        /// <summary>Records a readback mismatch on the result (the first one becomes the op error). Always returns false.</summary>
        protected static bool Mismatch(OpResult result, string message)
        {
            if (result.Error is null) result.Error = new OpError(IssueCodes.ReadbackMismatch, message);
            return false;
        }

        /// <summary>Throws when a re-run of the validation logic at execution time finds errors (the document changed under us).</summary>
        protected static void ThrowIfErrors(string op, List<Issue> issues)
        {
            foreach (var i in issues)
                if (i.IsError) throw new InvalidOperationException(op + ": " + i);
        }
    }

    /// <summary>
    /// Finds every concrete <see cref="IOpHandler"/> in this assembly with a parameterless constructor and keys it by name.
    /// A handler whose name is not an AutoCAD op schema in Core, or a duplicate name, is a start-up error written to the
    /// log and skipped. Ops without a handler are listed; a job naming one fails validation with issue no_handler.
    /// </summary>
    internal sealed class HandlerRegistry
    {
        private readonly Dictionary<string, IOpHandler> _handlers = new Dictionary<string, IOpHandler>(StringComparer.Ordinal);

        internal IReadOnlyCollection<string> Names => _handlers.Keys;
        internal List<string> Problems { get; } = new List<string>();
        internal List<string> Missing { get; } = new List<string>();

        internal static HandlerRegistry Discover(OpRegistry schemas)
        {
            var r = new HandlerRegistry();
            Type[] types;
            try { types = typeof(HandlerRegistry).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
            foreach (var t in types)
            {
                if (t is null || t.IsAbstract || t.IsInterface || !typeof(IOpHandler).IsAssignableFrom(t)) continue;
                var ctor = t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (ctor is null) { r.Problems.Add(t.FullName + " has no parameterless constructor; skipped."); continue; }
                IOpHandler h;
                try { h = (IOpHandler)ctor.Invoke(null); }
                catch (Exception e) { r.Problems.Add(t.FullName + " failed to construct: " + (e.InnerException ?? e).Message); continue; }
                if (schemas.Find(h.Name, AppKind.AutoCad) is null) { r.Problems.Add(t.FullName + " handles '" + h.Name + "', which is not an AutoCAD op schema; skipped."); continue; }
                if (r._handlers.ContainsKey(h.Name)) { r.Problems.Add("Two handlers for '" + h.Name + "' (" + t.FullName + " skipped)."); continue; }
                r._handlers.Add(h.Name, h);
            }
            foreach (var s in schemas.ForApp(AppKind.AutoCad))
                if (!r._handlers.ContainsKey(s.Name)) r.Missing.Add(s.Name);
            return r;
        }

        internal IOpHandler? Find(string op) => _handlers.TryGetValue(op, out var h) ? h : null;
    }
}
