using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// phase.set: Phase Created and Phase Demolished of elements (Element.CreatedPhaseId, DemolishedPhaseId), and the
    /// phase and phase filter of views (VIEW_PHASE, VIEW_PHASE_FILTER).
    ///
    /// Each element is checked with ArePhasesModifiable, IsPhaseCreatedValid and IsPhaseDemolishedValid, and the order
    /// of the two phases (created no later than demolished, by index in Document.Phases) against what the element
    /// will have afterwards. When both are set, demolished is cleared first so that an intermediate state is never out
    /// of order. Elements already matching are reported unchanged and not declared. A view whose phase is controlled by
    /// its template (read-only parameter) is refused, naming the template.
    /// </summary>
    public sealed class PhaseSet : OpHandler
    {
        public override string Name => "phase.set";

        private sealed class Change
        {
            public Element Element = null!;
            public ElementId? Created;
            public ElementId? Demolished;
        }

        private sealed class ViewChange
        {
            public View View = null!;
            public ElementId? Phase;
            public ElementId? Filter;
        }

        private sealed class Plan
        {
            public List<Change> Elements = new List<Change>();
            public List<Element> Unchanged = new List<Element>();
            public List<ViewChange> Views = new List<ViewChange>();
            public List<View> UnchangedViews = new List<View>();
        }

        private static List<Phase> Phases(Document doc)
        {
            var list = new List<Phase>();
            foreach (Phase ph in doc.Phases) list.Add(ph);
            return list;
        }

        private static string PhaseName(Document doc, ElementId id) => ElementIds.IsValid(id) ? doc.GetElement(id)?.Name ?? Kit.Id(id) : "none";

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            var phases = Phases(doc);
            var index = new Dictionary<long, int>();
            for (int i = 0; i < phases.Count; i++) index[ElementIds.ToLong(phases[i].Id)] = i;

            ElementId? PhaseArg(string field, bool allowNone)
            {
                if (!(args.GetStringOrNull(field) is string n)) return null;
                if (allowNone && string.Equals(n, "none", StringComparison.OrdinalIgnoreCase)) return ElementId.InvalidElementId;
                var f = Lookup.ByName(phases, n, "phase");
                if (!f.Ok) { OpContext.Error(issues, ctx.FieldPath(args, field), f.Code, f.Error!); return null; }
                return f.Value!.Id;
            }

            var created = PhaseArg("created", false);
            var demolished = PhaseArg("demolished", true);
            bool selects = args.Has("elements") || args.Has("filter");
            if (selects && !args.Has("created") && !args.Has("demolished"))
                OpContext.Error(issues, ctx.FieldPath(args, args.Has("elements") ? "elements" : "filter"), RevitIssueCodes.Invalid, "give created, demolished or both for the elements");
            if (!selects && (args.Has("created") || args.Has("demolished")))
                OpContext.Error(issues, ctx.FieldPath(args, args.Has("created") ? "created" : "demolished"), RevitIssueCodes.Invalid, "created and demolished apply to elements; give elements or filter");

            if (selects && !Kit.HasErrors(issues))
            {
                var list = ElementSelect.Resolve(ctx, args, "elements", "filter", issues, out bool pending);
                if (!pending && list.Count == 0 && !Kit.HasErrors(issues))
                    OpContext.Error(issues, ctx.FieldPath(args, args.Has("elements") ? "elements" : "filter"), RevitIssueCodes.NotFound, "no elements selected");
                var seen = new HashSet<long>();
                foreach (var e in list)
                {
                    if (!seen.Add(ElementIds.ToLong(e.Id))) continue;
                    string at = ctx.FieldPath(args, args.Has("elements") ? "elements" : "filter");
                    string who = "element " + Kit.Id(e.Id);
                    if (!e.ArePhasesModifiable()) { OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, who + " (" + (e.Category?.Name ?? e.GetType().Name) + ") does not allow its phases to be changed"); continue; }
                    // The validators judge one phase against the element's current other phase. When both change, Apply
                    // clears demolished first, so a refusal that only reflects the current order is left to the order
                    // check below; Revit still refuses a truly invalid phase in Apply.
                    bool both = created != null && demolished != null;
                    if (created != null && !e.IsPhaseCreatedValid(created) && !(both && ElementIds.IsValid(e.DemolishedPhaseId)))
                    { OpContext.Error(issues, at, RevitIssueCodes.Invalid, who + " cannot be created in phase '" + PhaseName(doc, created) + "'"); continue; }
                    if (demolished != null && !e.IsPhaseDemolishedValid(demolished) && !both)
                    { OpContext.Error(issues, at, RevitIssueCodes.Invalid, who + " cannot be demolished in phase '" + PhaseName(doc, demolished) + "'"); continue; }
                    var c = created ?? e.CreatedPhaseId;
                    var d = demolished ?? e.DemolishedPhaseId;
                    if (ElementIds.IsValid(c) && ElementIds.IsValid(d) &&
                        index.TryGetValue(ElementIds.ToLong(c), out int ci) && index.TryGetValue(ElementIds.ToLong(d), out int di) && di < ci)
                    {
                        OpContext.Error(issues, at, RevitIssueCodes.Invalid, who + " would be demolished ('" + PhaseName(doc, d) + "') before it is created ('" + PhaseName(doc, c) + "')");
                        continue;
                    }
                    bool same = (created is null || e.CreatedPhaseId == created) && (demolished is null || e.DemolishedPhaseId == demolished);
                    if (same) p.Unchanged.Add(e);
                    else
                    {
                        p.Elements.Add(new Change { Element = e, Created = created, Demolished = demolished });
                        ctx.DeclareModifies(e.Id);
                    }
                }
            }

            var viewsArg = args.Raw["views"] as JsonArray ?? new JsonArray();
            if (viewsArg.Count > 0)
            {
                if (!args.Has("viewPhase") && !args.Has("phaseFilter"))
                    OpContext.Error(issues, ctx.FieldPath(args, "views"), RevitIssueCodes.Invalid, "give viewPhase, phaseFilter or both for the views");
                var vPhase = PhaseArg("viewPhase", false);
                ElementId? vFilter = null;
                if (args.GetStringOrNull("phaseFilter") is string fn)
                {
                    if (string.Equals(fn, "none", StringComparison.OrdinalIgnoreCase)) vFilter = ElementId.InvalidElementId;
                    else
                    {
                        var f = Lookup.ByName(new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>(), fn, "phase filter");
                        if (!f.Ok) OpContext.Error(issues, ctx.FieldPath(args, "phaseFilter"), f.Code, f.Error!);
                        else vFilter = f.Value!.Id;
                    }
                }
                for (int i = 0; i < viewsArg.Count; i++)
                {
                    string at = JsonPath.Index(ctx.FieldPath(args, "views"), i);
                    if (OpContext.IsPending(viewsArg[i])) continue;
                    var fv = Lookup.View(doc, viewsArg[i]);
                    if (!fv.Ok) { OpContext.Error(issues, at, fv.Code, fv.Error!); continue; }
                    var v = fv.Value!;
                    bool ok = true;
                    foreach (var (bip, want, what) in new[] { (BuiltInParameter.VIEW_PHASE, vPhase, "phase"), (BuiltInParameter.VIEW_PHASE_FILTER, vFilter, "phase filter") })
                    {
                        if (want is null) continue;
                        var prm = v.get_Parameter(bip);
                        if (prm is null) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "view '" + v.Name + "' has no " + what); ok = false; }
                        else if (prm.IsReadOnly)
                        {
                            string tpl = ElementIds.IsValid(v.ViewTemplateId) ? " (controlled by view template '" + doc.GetElement(v.ViewTemplateId)?.Name + "')" : "";
                            OpContext.Error(issues, at, RevitIssueCodes.ReadOnly, "the " + what + " of view '" + v.Name + "' is read-only" + tpl);
                            ok = false;
                        }
                    }
                    if (!ok) continue;
                    bool same = (vPhase is null || Kit.ViewPhase(v) == vPhase) &&
                                (vFilter is null || (v.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() ?? ElementId.InvalidElementId) == vFilter);
                    if (same) p.UnchangedViews.Add(v);
                    else
                    {
                        p.Views.Add(new ViewChange { View = v, Phase = vPhase, Filter = vFilter });
                        ctx.DeclareModifies(v.Id);
                    }
                }
            }
            else if (args.Has("viewPhase") || args.Has("phaseFilter"))
                OpContext.Error(issues, ctx.FieldPath(args, args.Has("viewPhase") ? "viewPhase" : "phaseFilter"), RevitIssueCodes.Invalid, "viewPhase and phaseFilter apply to views; give views");
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            foreach (var c in p!.Elements)
            {
                var e = c.Element;
                try
                {
                    if (c.Created != null && c.Demolished != null)
                    {
                        if (ElementIds.IsValid(e.DemolishedPhaseId)) e.DemolishedPhaseId = ElementId.InvalidElementId;
                        e.CreatedPhaseId = c.Created;
                        if (ElementIds.IsValid(c.Demolished)) e.DemolishedPhaseId = c.Demolished;
                    }
                    else if (c.Created != null) e.CreatedPhaseId = c.Created;
                    else if (c.Demolished != null) e.DemolishedPhaseId = c.Demolished;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw new OpFailedException("phase_refused", "Revit refused the phases of element " + Kit.Id(e.Id) + ": " + ex.Message);
                }
                result.Modified.Add(Kit.Id(e.Id));
            }
            foreach (var vc in p.Views)
            {
                if (vc.Phase != null && !(vc.View.get_Parameter(BuiltInParameter.VIEW_PHASE)?.Set(vc.Phase) ?? false))
                    throw new OpFailedException("parameter_value", "Revit refused the phase of view '" + vc.View.Name + "'");
                if (vc.Filter != null && !(vc.View.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.Set(vc.Filter) ?? false))
                    throw new OpFailedException("parameter_value", "Revit refused the phase filter of view '" + vc.View.Name + "'");
                result.Modified.Add(Kit.Id(vc.View.Id));
            }
            result.Unchanged.AddRange(p.Unchanged.Select(e => Kit.Id(e.Id)));
            result.Unchanged.AddRange(p.UnchangedViews.Select(v => Kit.Id(v.Id)));
            result.Outputs.Add("elementCount", p.Elements.Count);
            result.Outputs.Add("viewCount", p.Views.Count);
            result.Readback.Add("elementIds", Ids(p.Elements.Select(c => Kit.Id(c.Element.Id)).Concat(p.Unchanged.Select(e => Kit.Id(e.Id)))));
            result.Readback.Add("viewIds", Ids(p.Views.Select(v => Kit.Id(v.View.Id)).Concat(p.UnchangedViews.Select(v => Kit.Id(v.Id)))));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var check = new ReadbackChecker(ctx.Units);
            var phases = Phases(doc);
            string? wantC = args.GetStringOrNull("created") is string c ? Lookup.ByName(phases, c, "phase").Value?.Name : null;
            string? wantD = args.GetStringOrNull("demolished") is string d ? (string.Equals(d, "none", StringComparison.OrdinalIgnoreCase) ? "none" : Lookup.ByName(phases, d, "phase").Value?.Name) : null;
            var elements = new JsonArray();
            foreach (var s in (result.Readback["elementIds"] as JsonArray ?? new JsonArray()).OfType<JsonString>())
            {
                if (!ElementIds.TryParse(s.Value, out var id) || !(doc.GetElement(id) is Element e)) { check.Fail("element " + s.Value + " no longer exists"); continue; }
                var o = new JsonObject().Add("id", s.Value);
                check.Text(o, "created", PhaseName(doc, e.CreatedPhaseId), wantC, "element " + s.Value + " phase created");
                check.Text(o, "demolished", PhaseName(doc, e.DemolishedPhaseId), wantD, "element " + s.Value + " phase demolished");
                elements.Add(o);
            }
            string? wantVp = args.GetStringOrNull("viewPhase") is string vp ? Lookup.ByName(phases, vp, "phase").Value?.Name : null;
            string? wantVf = args.GetStringOrNull("phaseFilter") is string vf
                ? (string.Equals(vf, "none", StringComparison.OrdinalIgnoreCase) ? "none" : Lookup.ByName(new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>(), vf, "phase filter").Value?.Name)
                : null;
            var views = new JsonArray();
            foreach (var s in (result.Readback["viewIds"] as JsonArray ?? new JsonArray()).OfType<JsonString>())
            {
                if (!ElementIds.TryParse(s.Value, out var id) || !(doc.GetElement(id) is View v)) { check.Fail("view " + s.Value + " no longer exists"); continue; }
                var o = new JsonObject().Add("id", s.Value).Add("name", v.Name);
                check.Text(o, "phase", PhaseName(doc, Kit.ViewPhase(v)), wantVp, "view '" + v.Name + "' phase");
                check.Text(o, "phaseFilter", PhaseName(doc, v.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() ?? ElementId.InvalidElementId), wantVf, "view '" + v.Name + "' phase filter");
                views.Add(o);
            }
            result.Readback.Add("elements", elements);
            result.Readback.Add("views", views);
            check.Apply(result);
        }
    }
}
