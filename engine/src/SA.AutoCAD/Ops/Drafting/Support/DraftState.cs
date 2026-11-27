using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SA.Core.Geometry;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>What an earlier op of the same job will create, beyond the names in <see cref="PlannedNames"/>.</summary>
    internal sealed class PlannedBlock
    {
        public string Name = "";
        public List<string> Tags = new List<string>();
        public bool Annotative;
    }

    /// <summary>An opening an earlier op of the same job will cut, in job units along a wall.</summary>
    internal sealed class PlannedOpening
    {
        public string WallKey = "";
        public double From, To;
        public string OpId = "";
    }

    /// <summary>
    /// Per-job validation state shared by the drafting handlers. It is keyed by the job's <see cref="OpContext"/> (one per
    /// validation or run), so handlers keep no per-job state in their own fields. Only meaningful while validating: at
    /// execution the earlier ops have run and the drawing itself is the truth.
    /// </summary>
    internal sealed class DraftState
    {
        private static readonly ConditionalWeakTable<OpContext, DraftState> States = new ConditionalWeakTable<OpContext, DraftState>();

        internal static DraftState For(OpContext ctx) => States.GetValue(ctx, _ => new DraftState());

        public Dictionary<string, PlannedBlock> Blocks { get; } = new Dictionary<string, PlannedBlock>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> DimStyleAnnotative { get; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public List<PlannedOpening> Openings { get; } = new List<PlannedOpening>();
        /// <summary>Annotation scales doc.setup will add: name to drawing units per paper unit.</summary>
        public Dictionary<string, double> AnnotationScales { get; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The CANNOSCALE doc.setup will set, if any.</summary>
        public string? CurrentAnnotationScale { get; set; }
    }
}
