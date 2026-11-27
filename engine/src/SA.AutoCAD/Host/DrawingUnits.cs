using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.Core.Geometry;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Units;

namespace SA.AutoCAD.Host
{
    /// <summary>
    /// The drawing's insertion units (INSUNITS, <c>Database.Insunits</c>) and conversion between job units and drawing
    /// units. All geometry handed to the AutoCAD API goes through <see cref="ToDrawing(double)"/>; everything read back
    /// goes through <see cref="ToJob(double)"/>.
    ///
    /// Rule for unitless drawings (INSUNITS = 0): the snapshot reports <c>unitless: true</c> and <c>mmPerUnit: null</c>.
    /// A job is refused at validation (issue <c>units_unitless</c>) unless every op in it declares that it works on a
    /// unitless drawing (only ops that set INSUNITS should). The engine never assumes that a unitless drawing is in the
    /// job's unit, because a wrong guess scales everything by 25.4, 304.8 or 1000 without any visible error.
    /// Every other INSUNITS value with a defined physical size is supported, including US survey feet.
    /// </summary>
    internal sealed class DrawingUnits
    {
        private DrawingUnits(int code, LengthUnit jobUnit)
        {
            Code = code;
            JobUnit = jobUnit;
            MmPerUnit = MillimetresPer(code);
        }

        /// <summary>The INSUNITS value (UnitsValue as an integer).</summary>
        public int Code { get; }
        public LengthUnit JobUnit { get; }
        /// <summary>Millimetres per drawing unit; null when unitless or unknown.</summary>
        public double? MmPerUnit { get; }
        public bool IsUnitless => Code == 0;
        public bool IsSupported => MmPerUnit.HasValue;
        public string Name => NameOf(Code);

        /// <summary>The drawing unit as a job length unit, when it is one of mm, cm, m, in, ft.</summary>
        public LengthUnit? AsLengthUnit
        {
            get
            {
                switch (Code)
                {
                    case 1: return LengthUnit.Inch;
                    case 2: return LengthUnit.Foot;
                    case 4: return LengthUnit.Millimetre;
                    case 5: return LengthUnit.Centimetre;
                    case 6: return LengthUnit.Metre;
                    default: return null;
                }
            }
        }

        public static DrawingUnits Read(Database db, LengthUnit jobUnit) => new DrawingUnits((int)db.Insunits, jobUnit);

        /// <summary>Drawing units per job unit.</summary>
        public double Factor
        {
            get
            {
                if (!MmPerUnit.HasValue)
                    throw new EngineException(ErrorCodes.ValidationFailed,
                        "The drawing's insertion units are " + Name + "; lengths cannot be converted. Set INSUNITS first (fix.units or doc.setup in a separate job).");
                return Length.MillimetresPer(JobUnit) / MmPerUnit.Value;
            }
        }

        public double ToDrawing(double jobLength) => jobLength * Factor;

        public double ToJob(double drawingLength) => drawingLength / Factor;

        public Point3d ToDrawing(Point2 p, double z = 0) => new Point3d(p.X * Factor, p.Y * Factor, z * Factor);

        public Point2d ToDrawing2d(Point2 p) => new Point2d(p.X * Factor, p.Y * Factor);

        public Point2 ToJob(Point3d p) => new Point2(p.X / Factor, p.Y / Factor);

        public Point2 ToJob(Point2d p) => new Point2(p.X / Factor, p.Y / Factor);

        /// <summary>A job-unit point as JSON [x, y], rounded to 1e-9 to keep reports readable.</summary>
        public static JsonArray PointJson(Point2 p) => new JsonArray(Round(p.X), Round(p.Y));

        public static double Round(double v) => Math.Round(v, 9);

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("insUnits", Code).Add("name", Name).Add("unitless", IsUnitless);
            o.Add("mmPerUnit", MmPerUnit.HasValue ? (JsonValue)MmPerUnit.Value : JsonValue.Null);
            var lu = AsLengthUnit;
            o.Add("lengthUnit", lu.HasValue ? (JsonValue)Length.Symbol(lu.Value) : JsonValue.Null);
            return o;
        }

        public static JsonObject Describe(Database db)
        {
            int code = (int)db.Insunits;
            var o = new JsonObject().Add("insUnits", code).Add("name", NameOf(code)).Add("unitless", code == 0);
            var mm = MillimetresPer(code);
            o.Add("mmPerUnit", mm.HasValue ? (JsonValue)mm.Value : JsonValue.Null);
            var du = new DrawingUnits(code, LengthUnit.Millimetre).AsLengthUnit;
            o.Add("lengthUnit", du.HasValue ? (JsonValue)Length.Symbol(du.Value) : JsonValue.Null);
            return o;
        }

        /// <summary>Millimetres per unit for each INSUNITS code (DXF header documentation), null for 0 and unknown codes.</summary>
        public static double? MillimetresPer(int code)
        {
            switch (code)
            {
                case 1: return 25.4;                       // inches
                case 2: return 304.8;                      // feet
                case 3: return 1609344.0;                  // miles
                case 4: return 1.0;                        // millimetres
                case 5: return 10.0;                       // centimetres
                case 6: return 1000.0;                     // metres
                case 7: return 1000000.0;                  // kilometres
                case 8: return 25.4e-6;                    // microinches
                case 9: return 0.0254;                     // mils
                case 10: return 914.4;                     // yards
                case 11: return 1e-7;                      // angstroms
                case 12: return 1e-6;                      // nanometres
                case 13: return 1e-3;                      // microns
                case 14: return 100.0;                     // decimetres
                case 15: return 10000.0;                   // decametres
                case 16: return 100000.0;                  // hectometres
                case 17: return 1e12;                      // gigametres
                case 18: return 1.495978707e14;            // astronomical units
                case 19: return 9.4607304725808e18;        // light years
                case 20: return 3.0856775814913673e19;     // parsecs
                case 21: return 1200.0 / 3937.0 * 1000.0;  // US survey feet
                case 22: return 100.0 / 3937.0 * 1000.0;   // US survey inches
                case 23: return 3600.0 / 3937.0 * 1000.0;  // US survey yards
                case 24: return 6336000.0 / 3937.0 * 1000.0; // US survey miles
                default: return null;
            }
        }

        public static string NameOf(int code)
        {
            switch (code)
            {
                case 0: return "unitless";
                case 1: return "inches";
                case 2: return "feet";
                case 3: return "miles";
                case 4: return "millimeters";
                case 5: return "centimeters";
                case 6: return "meters";
                case 7: return "kilometers";
                case 8: return "microinches";
                case 9: return "mils";
                case 10: return "yards";
                case 11: return "angstroms";
                case 12: return "nanometers";
                case 13: return "microns";
                case 14: return "decimeters";
                case 15: return "decameters";
                case 16: return "hectometers";
                case 17: return "gigameters";
                case 18: return "astronomical units";
                case 19: return "light years";
                case 20: return "parsecs";
                case 21: return "US survey feet";
                case 22: return "US survey inches";
                case 23: return "US survey yards";
                case 24: return "US survey miles";
                default: return "code " + code;
            }
        }
    }
}
