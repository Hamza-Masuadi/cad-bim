using Autodesk.Revit.DB;
using SA.Revit.Ops.Modelling.Support;

namespace SA.Revit.Ops.Modelling
{
    /// <summary>
    /// window.place: a wall-hosted window. Without sillHeight the type's default sill is kept and read back. The checks
    /// and readback are described on <see cref="HostedInsertPlace"/>.
    /// </summary>
    public sealed class WindowPlace : HostedInsertPlace
    {
        public override string Name => "window.place";
        protected override BuiltInCategory Category => BuiltInCategory.OST_Windows;
        protected override string Kind => "window";
    }
}
