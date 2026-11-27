using Autodesk.Revit.DB;
using SA.Revit.Ops.Modelling.Support;

namespace SA.Revit.Ops.Modelling
{
    /// <summary>door.place: a wall-hosted door. The checks and readback are described on <see cref="HostedInsertPlace"/>.</summary>
    public sealed class DoorPlace : HostedInsertPlace
    {
        public override string Name => "door.place";
        protected override BuiltInCategory Category => BuiltInCategory.OST_Doors;
        protected override string Kind => "door";
    }
}
