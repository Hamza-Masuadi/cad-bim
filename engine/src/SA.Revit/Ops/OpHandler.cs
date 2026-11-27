using System.Collections.Generic;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.Revit.Ops
{
    /// <summary>
    /// The handler contract (ARCHITECTURE section 7), split in three so readback happens after commit:
    /// Validate (no changes) - Execute (inside the op's transaction) - Readback (after the transaction committed and
    /// the document regenerated).
    /// </summary>
    public interface IOpHandler
    {
        /// <summary>The op name, exactly as in Core's Revit op schemas.</summary>
        string Name { get; }

        /// <summary>False for ops that only read or export: the runner then opens no transaction for them.</summary>
        bool ModifiesModel { get; }

        /// <summary>True only for family-editor ops; every other op is refused in a family document.</summary>
        bool AllowsFamilyDocument { get; }

        void Validate(OpContext ctx, OpArgs args, List<Issue> issues);

        OpResult Execute(OpContext ctx, OpArgs args);

        void Readback(OpContext ctx, OpArgs args, OpResult result);
    }

    /// <summary>Base class for handlers. Subclass it, give it a parameterless constructor, and it is found at start-up.</summary>
    public abstract class OpHandler : IOpHandler
    {
        public abstract string Name { get; }
        public virtual bool ModifiesModel => true;
        public virtual bool AllowsFamilyDocument => false;

        public abstract void Validate(OpContext ctx, OpArgs args, List<Issue> issues);

        public OpResult Execute(OpContext ctx, OpArgs args)
        {
            var result = new OpResult(ctx.Op.Id, Name);
            Apply(ctx, args, result);
            return result;
        }

        /// <summary>Make the change. Fill Created / Modified / Deleted / Unchanged and Outputs. Throw <see cref="OpFailedException"/> to fail.</summary>
        protected abstract void Apply(OpContext ctx, OpArgs args, OpResult result);

        /// <summary>Read the document and fill Readback (job units); use <see cref="Support.ReadbackChecker"/> to set Ok.</summary>
        public abstract void Readback(OpContext ctx, OpArgs args, OpResult result);

        protected static JsonArray Ids(IEnumerable<string> ids) => JsonArray.FromStrings(ids);
    }
}
