using Autodesk.Revit.DB;

namespace SA.Revit.Ops.Docs
{
    /// <summary>
    /// view.ceiling.create: a reflected ceiling plan on a level (ViewFamily.CeilingPlan). Crop, naming, template and
    /// scale rules are those of <see cref="PlanViewCreateBase"/>. The crop rectangle is given in plan coordinates as
    /// seen from above, like every other plan point in a job; the inverse crop-box transform takes care of the RCP's
    /// mirrored view axes.
    /// </summary>
    public sealed class ViewCeilingCreate : PlanViewCreateBase
    {
        public override string Name => "view.ceiling.create";
        protected override ViewFamily Family => ViewFamily.CeilingPlan;
        protected override ViewType Kind => ViewType.CeilingPlan;
    }
}
