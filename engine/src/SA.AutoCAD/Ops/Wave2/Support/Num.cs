using System;
using System.Collections.Generic;
using System.Linq;
using SA.AutoCAD.Host;
using SA.Core.Geometry;
using SA.Core.Json;

namespace SA.AutoCAD.Ops.Wave2.Support
{
    internal static class Num
    {
        internal static double Rad(double degrees) => degrees * Math.PI / 180;

        internal static double Deg(double radians) => radians * 180 / Math.PI;

        /// <summary>An angle in [0, 2 pi).</summary>
        internal static double Wrap(double radians)
        {
            double a = radians % (2 * Math.PI);
            if (a < 0) a += 2 * Math.PI;
            return a;
        }

        /// <summary>Difference of two angles, in [0, pi].</summary>
        internal static double AngleGap(double a, double b)
        {
            double d = Math.Abs(Wrap(a) - Wrap(b));
            return Math.Min(d, 2 * Math.PI - d);
        }

        /// <summary>A list of plain numbers (empty when absent).</summary>
        internal static List<double> List(SA.Core.Jobs.OpArgs args, string field)
        {
            var list = new List<double>();
            if (!(args.Get(field) is JsonArray a)) return list;
            for (int i = 0; i < a.Count; i++)
            {
                if (!(a[i] is JsonNumber n)) throw new SA.Core.Jobs.OpArgsException(args.Path + "." + field + "[" + i + "]", "expected a number");
                list.Add(n.Value);
            }
            return list;
        }

        internal static JsonArray Points(IEnumerable<Point2> pts) => new JsonArray(pts.Select(p => (JsonValue)DrawingUnits.PointJson(p)));

        internal static JsonArray Strings(IEnumerable<string> values) => JsonArray.FromStrings(values);

        /// <summary>Area in job units squared converted to m2, ft2 or kept ("job").</summary>
        internal static double Area(double jobArea, SA.Core.Units.LengthUnit jobUnit, string areaUnit)
        {
            if (areaUnit == "job") return jobArea;
            var target = areaUnit == "ft2" ? SA.Core.Units.LengthUnit.Foot : SA.Core.Units.LengthUnit.Metre;
            double f = SA.Core.Units.Length.Convert(1, jobUnit, target);
            return jobArea * f * f;
        }
    }
}
