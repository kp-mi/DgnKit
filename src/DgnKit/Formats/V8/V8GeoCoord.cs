using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DgnKit.Elements;
using DgnKit.IO;
using DgnKit.Model;

namespace DgnKit.Formats.V8
{
    /// <summary>
    /// Geographic coordinate system storage (see docs/FORMAT-V8.md §7.1).
    /// <para>
    /// MicroStation's model GCS ("DgnGCS") is a non-graphic <b>type 66 application element with signature 182</b>
    /// (MSGEOCOORD_SIGNATURE) among the model's control elements (<c>Dgn^C</c>). The body is a fixed 1496-byte record
    /// derived from CS-MAP's coordinate system parameters: 8-bit NUL-padded key names/descriptions and doubles.
    /// Offsets below are from the element start:
    /// </para>
    /// <code>
    ///  0x20 i16 signature (182)       0x24 u32 data byte count (1496)    0x28 u32 2 (version?)
    ///  0x34 f64 ellipsoid semi-major   0x3C f64 semi-minor
    ///  0xB4 char[24] GCS key name ("EPSG:2927", "HARN.WA-NF")          0xCC char[24] group ("EPSGPRJ", "SPCSHPF")
    ///  0xFC char[24] projection key ("LM")                             0x114 char[64] projection description
    ///  0x154 char[64] datum description  0x194 char[64] ellipsoid description
    ///  0x1D4 char[64] GCS description    0x214 char[64] source
    ///  0x2DC i32 EPSG code (0 when unknown)
    ///  0x37C f64 central meridian  0x384 f64 false easting  0x38C f64 false northing  (CS units)
    ///  0x394 f64[4] useful range: min lon, min lat, max lon, max lat (0 = unset)
    ///  0x3D4 f64 scale reduction  0x3DC f64 standard parallel 1  0x3E4 f64 standard parallel 2  0x3EC f64 origin latitude
    ///  0x3F8 char[16] unit name ("Foot" = US survey foot)              0x408 char[24] datum key ("EPSG:6152", "NAD83/HARN-A")
    /// </code>
    /// <para>
    /// Files that contain GIS data imported through MicroStation's feature/SHP workflow also carry the source spatial
    /// reference as ESRI WKT in an ECSchema custom attribute (<c>FeatureDefinition.SpatialRef</c>), stored as UTF-16 XML
    /// in a type 39 DgnStore ("fLMX") in <c>Dgn^Nm</c>. That is used as a file-level fallback.
    /// </para>
    /// </summary>
    internal static class V8GeoCoord
    {
        public const int Signature = 182;
        private const int MinLength = 0x420;
        private const double FtUs = 0.3048006096012192;

        public static void Read(DgnFile file)
        {
            foreach (var m in file.Models)
            {
                var gcsElements = m.ControlElements.Where(IsGcsElement).ToList();
                if (gcsElements.Count == 0) continue;
                // ⚠️ a model may also hold a "reference" GCS; both corpus files carry exactly one element (the primary).
                var g = Decode(gcsElements[0].RawBytes!);
                if (g == null) continue;
                g.Source = $"MSGEOCOORD type 66 element {gcsElements[0].Id} in model '{m.Name}'";
                m.GeoCoordinateSystem = g;
                if (gcsElements.Count > 1) file.Warn($"Model '{m.Name}' has {gcsElements.Count} GCS elements; using the first");
            }

            var withGcs = file.DefaultModel?.GeoCoordinateSystem ?? file.Models.Select(x => x.GeoCoordinateSystem).FirstOrDefault(x => x != null);
            file.GeoCoordinateSystem = withGcs ?? ReadFeatureSpatialRef(file);
        }

        internal static bool IsGcsElement(Element e) =>
            e.RawType == (int)ElementType.MicroStationApplication && !e.IsGraphic && e.RawBytes != null && e.RawBytes.Length >= MinLength
            && BitConverter.ToInt16(e.RawBytes, 0x20) == Signature;

        /// <summary>Decodes the body of a MSGEOCOORD (type 66, signature 182) element.</summary>
        public static GeoCoordinateInfo? Decode(byte[] raw)
        {
            if (raw.Length < MinLength) return null;
            var r = new LeReader(raw);
            string key = Text(r, 0xB4, 24);
            if (key.Length == 0) return null;
            var g = new GeoCoordinateInfo
            {
                Name = key,
                Group = Nz(Text(r, 0xCC, 24)),
                ProjectionKey = Nz(Text(r, 0xFC, 24)),
                ProjectionName = Nz(Text(r, 0x114, 64)),
                DatumName = Nz(Text(r, 0x154, 64)),
                EllipsoidName = Nz(Text(r, 0x194, 64)),
                Description = Nz(Text(r, 0x1D4, 64)),
                SourceReference = Nz(Text(r, 0x214, 64)),
                Units = Nz(Text(r, 0x3F8, 16)),
                DatumKey = Nz(Text(r, 0x408, 24)),
                SemiMajorAxis = Finite(r.F64(0x34)),
                SemiMinorAxis = Finite(r.F64(0x3C)),
                CentralMeridian = Finite(r.F64(0x37C)),
                FalseEasting = Finite(r.F64(0x384)),
                FalseNorthing = Finite(r.F64(0x38C)),
                ScaleReduction = Finite(r.F64(0x3D4)),
                StandardParallel1 = Finite(r.F64(0x3DC)),
                StandardParallel2 = Finite(r.F64(0x3E4)),
                OriginLatitude = Finite(r.F64(0x3EC)),
            };
            double minLon = r.F64(0x394), minLat = r.F64(0x39C), maxLon = r.F64(0x3A4), maxLat = r.F64(0x3AC);
            if (minLon != 0 || minLat != 0 || maxLon != 0 || maxLat != 0)
            { g.MinLongitude = minLon; g.MinLatitude = minLat; g.MaxLongitude = maxLon; g.MaxLatitude = maxLat; }

            int epsg = r.I32(0x2DC);
            if (epsg > 0 && epsg < 1_000_000) g.Epsg = epsg;
            g.Epsg ??= GcsKeys.CsMapKeyToEpsg(key);
            g.UnitMeters = UnitMeters(g.Units);
            g.Wkt = BuildWkt(g);
            return g;
        }

        /// <summary>Metres per CS-MAP unit name (null when unknown).</summary>
        internal static double? UnitMeters(string? unit)
        {
            switch ((unit ?? "").Trim().ToUpperInvariant())
            {
                case "METER": case "METRE": case "METERS": case "METRES": case "M": return 1.0;
                case "FOOT": case "USFOOT": case "US FOOT": case "SURVEY FOOT": case "US SURVEY FOOT": case "FTUS": return FtUs;
                case "IFOOT": case "INTERNATIONAL FOOT": case "FT": return 0.3048;
                case "KILOMETER": case "KILOMETRE": return 1000.0;
                default: return null;
            }
        }

        /// <summary>
        /// WKT built from the stored parameters, for projections whose parameter slots are verified
        /// (CS-MAP "LM" = Lambert Conformal Conic 2SP, "LL" = geographic). Others return null (⚠️ slots unverified).
        /// </summary>
        internal static string? BuildWkt(GeoCoordinateInfo g)
        {
            if (g.SemiMajorAxis is not double a || a < 6_000_000 || a > 7_000_000) return null;
            double b = g.SemiMinorAxis ?? 0;
            double invF = b > 0 && b < a ? a / (a - b) : 0;
            string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            string datum = Clean(g.DatumKey ?? g.DatumName ?? "Unknown");
            string ellipsoid = Clean(g.EllipsoidName ?? "Unknown");
            string geog = $"GEOGCS[\"{Clean(g.DatumName ?? datum)}\",DATUM[\"{datum}\",SPHEROID[\"{ellipsoid}\",{F(a)},{F(invF)}]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]";
            string proj = (g.ProjectionKey ?? "").ToUpperInvariant();
            if (proj == "LL") return geog;
            if (proj != "LM") return null;
            if (g.UnitMeters is not double unit || g.CentralMeridian is not double cm || g.StandardParallel1 is not double sp1
                || g.StandardParallel2 is not double sp2 || g.OriginLatitude is not double lat0) return null;
            string name = Clean(g.Description ?? g.Name ?? "Lambert Conformal Conic");
            var sb = new StringBuilder();
            sb.Append($"PROJCS[\"{name}\",{geog},PROJECTION[\"Lambert_Conformal_Conic_2SP\"],");
            sb.Append($"PARAMETER[\"standard_parallel_1\",{F(sp1)}],PARAMETER[\"standard_parallel_2\",{F(sp2)}],");
            sb.Append($"PARAMETER[\"latitude_of_origin\",{F(lat0)}],PARAMETER[\"central_meridian\",{F(cm)}],");
            sb.Append($"PARAMETER[\"false_easting\",{F(g.FalseEasting ?? 0)}],PARAMETER[\"false_northing\",{F(g.FalseNorthing ?? 0)}],");
            sb.Append($"UNIT[\"{Clean(g.Units ?? "unit")}\",{F(unit)}]");
            if (g.Epsg.HasValue) sb.Append($",AUTHORITY[\"EPSG\",\"{g.Epsg.Value}\"]");
            sb.Append(']');
            return sb.ToString();
        }

        private static readonly Regex SpatialRefRx = new Regex(@"<SpatialRef>\s*((?:PROJCS|GEOGCS)\[.*?\])\s*</SpatialRef>", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>ESRI WKT from GIS feature definitions (ECSchema XML in type 39 DgnStores); the most common one wins.</summary>
        internal static GeoCoordinateInfo? ReadFeatureSpatialRef(DgnFile file)
        {
            var found = new List<(string Wkt, ulong Id)>();
            foreach (var e in file.NonModelElements.SelectMany(x => x.SelfAndDescendants()))
            {
                if (e.RawType != (int)ElementType.DgnStoreHeader || e.RawBytes == null || e.RawBytes.Length < 0x60) continue;
                var raw = e.RawBytes;
                if (Encoding.ASCII.GetString(raw, 0x28, 4) != "fLMX") continue;
                // UTF-16 XML; try both byte alignments
                for (int align = 0; align < 2; align++)
                {
                    string text = Encoding.Unicode.GetString(raw, align, (raw.Length - align) & ~1);
                    if (text.IndexOf("<SpatialRef>", StringComparison.Ordinal) < 0) continue;
                    foreach (Match m in SpatialRefRx.Matches(text))
                        found.Add((System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), e.Id));
                    break;
                }
            }
            if (found.Count == 0) return null;
            var best = found.GroupBy(f => f.Wkt).OrderByDescending(g => g.Count()).First();
            var wkt = best.Key;
            var nm = Regex.Match(wkt, "^(?:PROJCS|GEOGCS)\\[\"([^\"]*)\"");
            var info = new GeoCoordinateInfo
            {
                Name = nm.Success ? nm.Groups[1].Value : null,
                Wkt = wkt,
                Source = $"ECSchema FeatureDefinition.SpatialRef (DgnStore {best.First().Id})",
            };
            // AUTHORITY at the end of the outermost PROJCS/GEOGCS
            var auth = Regex.Match(wkt, "AUTHORITY\\[\"EPSG\",\\s*\"?(\\d+)\"?\\]\\]\\s*$");
            if (auth.Success) info.Epsg = int.Parse(auth.Groups[1].Value, CultureInfo.InvariantCulture);
            info.Epsg ??= GcsKeys.EsriNameToEpsg(info.Name);
            if (found.Select(f => f.Wkt).Distinct().Count() > 1)
                file.Warn($"GIS feature definitions carry {found.Select(f => f.Wkt).Distinct().Count()} different SpatialRefs; using the most common");
            return info;
        }

        private static string Text(LeReader r, int o, int max) => r.Ansi(o, max).Trim();
        private static string? Nz(string s) => s.Length == 0 ? null : s;
        private static double? Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;
        private static string Clean(string s) => s.Replace("\"", "'");
    }
}
