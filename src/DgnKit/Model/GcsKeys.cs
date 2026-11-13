using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DgnKit.Model
{
    /// <summary>
    /// Name → EPSG lookups for coordinate system names found in DGN files: MicroStation/CS-MAP GCS key names
    /// ("EPSG:2927", "HARN.WA-NF", "WA83-SF") and ESRI State Plane PROJCS names from GIS feature SpatialRef WKT.
    /// Covers the US DOT State Plane zones of the test corpus (WA, NE, TX, FL, OH, NJ) plus WGS84; unknown names give null.
    /// </summary>
    public static class GcsKeys
    {
        /// <summary>Maps CS-MAP / MicroStation GCS key names to EPSG ("EPSG:nnnn" keys parse directly).</summary>
        public static int? CsMapKeyToEpsg(string? key)
        {
            if (key == null) return null;
            string k = key.Trim().ToUpperInvariant();
            if (k.StartsWith("EPSG:", StringComparison.Ordinal))
                return int.TryParse(k.Substring(5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : (int?)null;
            switch (k)
            {
                case "WA83-NF": return 2285;
                case "WA83-SF": return 2286;
                case "WA83-N": return 32148;
                case "WA83-S": return 32149;
                case "WA-HPGN-NF": case "HARN/WA.WA-NF": case "HARN.WA-NF": return 2926;
                case "WA-HPGN-SF": case "HARN/WA.WA-SF": case "HARN.WA-SF": return 2927;
                case "NE83F": return 26852;
                case "NE83": return 32104;
                case "TX83-NF": return 2275;
                case "TX83-NCF": return 2276;
                case "TX83-CF": return 2277;
                case "TX83-SCF": return 2278;
                case "TX83-SF": return 2279;
                case "FL83-EF": return 2236;
                case "FL83-WF": return 2237;
                case "FL83-NF": return 2238;
                case "OH83-NF": return 3734;
                case "OH83-SF": return 3735;
                case "NJ83F": return 3424;
                case "LL84": case "LL-WGS84": return 4326;
                default: return null;
            }
        }

        private static readonly Regex EsriStatePlane = new Regex(
            @"^NAD_1983_(?:(HARN|NSRS2007|2011)_)?StatePlane_[A-Za-z_]+?_FIPS_(\d{4})(?:_(Feet|Meters|Ft_US))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Maps ESRI State Plane PROJCS names to EPSG, e.g. "NAD_1983_HARN_StatePlane_Washington_South_FIPS_4602_Feet" → 2927.
        /// </summary>
        public static int? EsriNameToEpsg(string? name)
        {
            if (name == null) return null;
            var m = EsriStatePlane.Match(name.Trim());
            if (!m.Success) return null;
            string realization = m.Groups[1].Value.ToUpperInvariant();
            string fips = m.Groups[2].Value;
            bool feet = m.Groups[3].Value.Length > 0 && !m.Groups[3].Value.Equals("Meters", StringComparison.OrdinalIgnoreCase);
            switch ((fips, realization, feet))
            {
                case ("4601", "", true): return 2285;
                case ("4602", "", true): return 2286;
                case ("4601", "", false): return 32148;
                case ("4602", "", false): return 32149;
                case ("4601", "HARN", true): return 2926;
                case ("4602", "HARN", true): return 2927;
                case ("4601", "2011", true): return 6597;
                case ("4602", "2011", true): return 6599;
                case ("2600", "", true): return 26852;
                case ("2600", "", false): return 32104;
                case ("2600", "2011", true): return 6880;
                case ("4201", "", true): return 2275;
                case ("4202", "", true): return 2276;
                case ("4203", "", true): return 2277;
                case ("4204", "", true): return 2278;
                case ("4205", "", true): return 2279;
                case ("4201", "", false): return 32137;
                case ("4202", "", false): return 32138;
                case ("4203", "", false): return 32139;
                case ("4204", "", false): return 32140;
                case ("4205", "", false): return 32141;
                case ("0901", "", true): return 2236;
                case ("0902", "", true): return 2237;
                case ("0903", "", true): return 2238;
                case ("0901", "", false): return 26958;
                case ("0902", "", false): return 26959;
                case ("0903", "", false): return 26960;
                case ("3401", "", true): return 3734;
                case ("3402", "", true): return 3735;
                case ("3401", "", false): return 32122;
                case ("3402", "", false): return 32123;
                case ("2900", "", true): return 3424;
                case ("2900", "", false): return 32111;
                default: return null;
            }
        }
    }
}
