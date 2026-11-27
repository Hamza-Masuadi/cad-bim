using System;
using System.Linq;
using SA.Core.Json;
using SA.Core.Units;

namespace SA.Core.Families
{
    /// <summary>The block a family.fromcad box is made for, in job units, in the family's coordinates (origin = block base point).</summary>
    public sealed class CadBoxBlock
    {
        public string FamilyName = "";
        /// <summary>The family's category as family.create takes it (family.create's schema needs category or template).</summary>
        public string Category = "";
        public string TypeName = "";
        public string Block = "";
        public string Source = "";
        public string MaterialParameter = "";
        public bool BodyInPlan;
        public double MinX, MinY, MaxX, MaxY, Height;
    }

    /// <summary>
    /// family.fromcad's box body as family.create args, so it is checked by <see cref="FamilyRecipe.Parse"/> and built by
    /// the same builder and flex test as family.create. One extrusion box between planes "CAD Left"/"CAD Right",
    /// "CAD Front"/"CAD Back" and "Ref. Level"/"CAD Top", with Width, Depth and Height labelling those pairs. On each plan
    /// axis the edge nearest the base point stays put: the template's centre plane when the edge lies on it, else a
    /// pinned plane; the other edge follows the size parameter, so the box grows away from the base point.
    /// </summary>
    public static class CadBoxRecipe
    {
        public const string Width = "Width";
        public const string Depth = "Depth";
        public const string Height = "Height";
        public const string Left = "CAD Left";
        public const string Right = "CAD Right";
        public const string Front = "CAD Front";
        public const string Back = "CAD Back";
        public const string Top = "CAD Top";

        public static JsonObject Args(CadBoxBlock b, LengthUnit unit)
        {
            double tol = Tolerance.DefaultLength(unit);
            var planes = new JsonArray();
            var dims = new JsonArray();
            var (left, right) = Axis("x", b.MinX, b.MaxX, Left, Right, FamilyRecipe.CenterLeftRight, tol, planes, dims);
            var (front, back) = Axis("y", b.MinY, b.MaxY, Front, Back, FamilyRecipe.CenterFrontBack, tol, planes, dims);
            planes.Add(new JsonObject().Add("name", Top).Add("axis", "z").Add("at", b.Height));

            var parameters = new JsonArray(
                Param(Width, "length", "dimensions"), Param(Depth, "length", "dimensions"), Param(Height, "length", "dimensions"),
                Param(b.MaterialParameter, "material", "materials"),
                Param(CadFamilies.BlockParameter, "text", "identityData"), Param(CadFamilies.SourceParameter, "text", "identityData"));
            var values = new JsonObject()
                .Add(Width, b.MaxX - b.MinX).Add(Depth, b.MaxY - b.MinY).Add(Height, b.Height)
                .Add(CadFamilies.BlockParameter, b.Block).Add(CadFamilies.SourceParameter, b.Source);
            var form = new JsonObject().Add("id", "box").Add("kind", "extrusion")
                .Add("box", new JsonObject()
                    .Add("x", JsonArray.FromStrings(new[] { left, right }))
                    .Add("y", JsonArray.FromStrings(new[] { front, back }))
                    .Add("z", JsonArray.FromStrings(new[] { FamilyRecipe.RefLevel, Top })))
                .Add("material", b.MaterialParameter);
            if (!b.BodyInPlan) form.Add("visibility", new JsonObject().Add("plan", false));

            return new JsonObject()
                .Add("name", b.FamilyName)
                .Add("category", b.Category)
                .Add("parameters", parameters)
                .Add("types", new JsonArray(new JsonObject().Add("name", b.TypeName).Add("values", values)))
                .Add("planes", planes)
                .Add("dimensions", new JsonArray(new JsonValue[] { Dim(left, right, Width), Dim(front, back, Depth), Dim(FamilyRecipe.RefLevel, Top, Height) }.Concat(dims)))
                .Add("forms", new JsonArray(form))
                .Add("flex", new JsonObject().Add("x", Width).Add("y", Depth).Add("z", Height));
        }

        /// <summary>The low and high plane names on one plan axis; adds the planes it declares.</summary>
        private static (string lo, string hi) Axis(string axis, double lo, double hi, string loName, string hiName, string centre, double tol, JsonArray planes, JsonArray dims)
        {
            if (lo < -tol && hi > tol && Math.Abs(lo + hi) <= tol)
            {
                // Centred on the base point: an equal dimension keeps it centred, as the built-in recipes do.
                planes.Add(Plane(loName, axis, lo, false));
                planes.Add(Plane(hiName, axis, hi, false));
                dims.Add(new JsonObject().Add("planes", JsonArray.FromStrings(new[] { loName, centre, hiName })).Add("equal", true));
                return (loName, hiName);
            }
            bool loOnCentre = Math.Abs(lo) <= tol, hiOnCentre = !loOnCentre && Math.Abs(hi) <= tol;
            bool pinLo = !loOnCentre && !hiOnCentre && Math.Abs(lo) <= Math.Abs(hi);
            bool pinHi = !loOnCentre && !hiOnCentre && !pinLo;
            if (!loOnCentre) planes.Add(Plane(loName, axis, lo, pinLo));
            if (!hiOnCentre) planes.Add(Plane(hiName, axis, hi, pinHi));
            return (loOnCentre ? centre : loName, hiOnCentre ? centre : hiName);
        }

        private static JsonObject Plane(string name, string axis, double at, bool pinned)
        {
            var o = new JsonObject().Add("name", name).Add("axis", axis).Add("at", at);
            if (pinned) o.Add("pinned", true);
            return o;
        }

        private static JsonObject Param(string name, string spec, string group) =>
            new JsonObject().Add("name", name).Add("spec", spec).Add("group", group);

        private static JsonObject Dim(string a, string b, string parameter) =>
            new JsonObject().Add("planes", JsonArray.FromStrings(new[] { a, b })).Add("parameter", parameter);
    }
}
