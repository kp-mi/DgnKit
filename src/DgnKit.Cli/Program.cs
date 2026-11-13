using System.Diagnostics;
using System.Globalization;
using System.Text;
using DgnKit.Elements;
using DgnKit.Export;
using DgnKit.Model;

namespace DgnKit.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0) return Usage();
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "info" => Info(args.Skip(1).ToArray()),
                "scan" => Scan(args.Skip(1).ToArray()),
                "dump" => Dump(args.Skip(1).ToArray()),
                "export" => ExportCmd(args.Skip(1).ToArray()),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            dgnkit - standalone MicroStation DGN (V7 / V8 / V8i / CONNECT) reader & converter

            usage:
              dgnkit info   <file.dgn>                         summary: models, units, levels, element counts
              dgnkit scan   <dir> [--csv out.csv]              read every *.dgn under dir, report health
              dgnkit dump   <file.dgn> [-o out.json] [--model name] [--raw]
                                                               full engineering-data JSON (geometry, levels, styles, metadata)
              dgnkit export <file.dgn> -f dxf|dwg|kml|kmz|geojson|pdf|json [-o out] [--model name]
                            [--crs EPSG:2927|<wkt file>|local] [--refs] [--explode] [--paper A1|ANSI-D|WxHmm]
                            (kml/kmz/geojson use the GCS stored in the DGN when --crs is omitted)
            """);
        return 1;
    }

    private static string? Opt(string[] a, string name)
    {
        int i = Array.FindIndex(a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static bool Flag(string[] a, string name) => a.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    private static int Info(string[] a)
    {
        var sw = Stopwatch.StartNew();
        var f = DgnFile.Open(a[0]);
        Console.WriteLine($"File      : {f.Path}");
        Console.WriteLine($"Format    : {f.Format} (v{f.MajorVersion}.{f.MinorVersion})  read in {sw.ElapsedMilliseconds} ms");
        if (f.GeoCoordinateSystem != null) Console.WriteLine($"GCS       : {f.GeoCoordinateSystem}  [{f.GeoCoordinateSystem.Source}]");
        Console.WriteLine($"Levels    : {f.Levels.Count}   LineStyles: {f.LineStyles.All.Count}   Fonts: {f.Fonts.ById.Count}   TextStyles: {f.TextStyles.Count}   SharedCellDefs: {f.SharedCellDefinitions.Count}");
        foreach (var m in f.Models)
        {
            var all = m.AllElements().ToList();
            Console.WriteLine($"Model {m.Id,3} '{m.Name}' {(m.IsDictionary ? "[def]" : "")} {(m.Is3d ? "3D" : "2D")} units: {m.Units.Master.Label} (uor/master={m.Units.UorPerMaster:G6}) GO={m.GlobalOrigin}  top={m.Elements.Count} total={all.Count} refs={m.References.Count}");
            if (m.GeoCoordinateSystem != null) Console.WriteLine($"     GCS: {m.GeoCoordinateSystem}");
            foreach (var r in m.References) Console.WriteLine($"     ref: {r}");
        }
        Console.WriteLine("Element types:");
        foreach (var kv in f.ElementTypeCounts.OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key,4} {(Enum.IsDefined(typeof(ElementType), kv.Key) ? ((ElementType)kv.Key).ToString() : ""),-26} {kv.Value,8}");
        var lv = f.Levels.All.OrderBy(l => l.Id).Take(25).Select(l => $"{l.Id}:{l.Name}");
        Console.WriteLine("Levels (first 25): " + string.Join(", ", lv));
        if (f.LineStyles.All.Count > 0) Console.WriteLine("Line styles: " + string.Join(", ", f.LineStyles.All.Take(30).Select(l => $"{l.Number}:{l.Name}")));
        if (f.Fonts.ById.Count > 0) Console.WriteLine("Fonts: " + string.Join(", ", f.Fonts.ById.Values.Take(30).Select(x => $"{x.Id}:{x.Name}")));
        var props = f.Models.SelectMany(m => m.AllElements()).SelectMany(e => e.PropertyGroups).GroupBy(g => g.Source + ":" + g.Name).OrderByDescending(g => g.Count()).Take(15);
        Console.WriteLine("Metadata groups: " + string.Join(", ", props.Select(g => $"{g.Key} x{g.Count()}")));
        Console.WriteLine($"Diagnostics: {f.Diagnostics.Count}");
        foreach (var d in f.Diagnostics.Take(15)) Console.WriteLine("  ! " + d);
        return 0;
    }

    private static int Scan(string[] a)
    {
        var files = Directory.EnumerateFiles(a[0], "*.dgn", SearchOption.AllDirectories).OrderBy(x => x).ToList();
        var csv = new StringBuilder("file,format,version,models,levels,elements,text,cells,sharedcells,unknownTypes,diagnostics,ms,error,typeCounts\n");
        int ok = 0;
        foreach (var p in files)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var f = DgnFile.Open(p);
                int els = f.Models.Sum(m => m.AllElements().Count());
                int text = f.Models.Sum(m => m.AllElements().Count(e => e is TextElement));
                int cells = f.Models.Sum(m => m.AllElements().Count(e => e is CellElement));
                int sc = f.Models.Sum(m => m.AllElements().Count(e => e is SharedCellElement));
                var unknown = f.ElementTypeCounts.Keys.Where(k => !Enum.IsDefined(typeof(ElementType), k)).ToList();
                string tc = string.Join(" ", f.ElementTypeCounts.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"));
                csv.AppendLine($"\"{p}\",{f.Format},{f.MajorVersion}.{f.MinorVersion},{f.Models.Count},{f.Levels.Count},{els},{text},{cells},{sc},\"{string.Join(" ", unknown)}\",{f.Diagnostics.Count},{sw.ElapsedMilliseconds},,\"{tc}\"");
                Console.WriteLine($"OK   {Path.GetFileName(p),-48} {f.Format} models={f.Models.Count,2} levels={f.Levels.Count,4} elements={els,7} text={text,5} diag={f.Diagnostics.Count,4} {sw.ElapsedMilliseconds,5}ms");
                ok++;
            }
            catch (Exception ex)
            {
                csv.AppendLine($"\"{p}\",,,,,,,,,,,{sw.ElapsedMilliseconds},\"{ex.Message.Replace('"', '\'')}\"");
                Console.WriteLine($"FAIL {Path.GetFileName(p),-48} {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"{ok}/{files.Count} files read");
        var outCsv = Opt(a, "--csv");
        if (outCsv != null) File.WriteAllText(outCsv, csv.ToString());
        return ok == files.Count ? 0 : 1;
    }

    private static int Dump(string[] a)
    {
        var f = DgnFile.Open(a[0], new DgnReadOptions { KeepRawBytes = Flag(a, "--raw") });
        var outPath = Opt(a, "-o") ?? Path.ChangeExtension(a[0], ".dgnkit.json");
        using var fs = File.Create(outPath);
        JsonDump.Write(f, fs, new JsonDumpOptions { ModelName = Opt(a, "--model"), IncludeRaw = Flag(a, "--raw") });
        Console.WriteLine("wrote " + outPath);
        return 0;
    }

    private static int ExportCmd(string[] a)
    {
        var fmt = (Opt(a, "-f") ?? "dxf").ToLowerInvariant();
        var opt = new ExportOptions
        {
            ModelName = Opt(a, "--model"),
            Crs = Opt(a, "--crs"),
            IncludeReferences = Flag(a, "--refs"),
            ExplodeCells = Flag(a, "--explode"),
            Paper = Opt(a, "--paper"),
        };
        var f = DgnFile.Open(a[0], new DgnReadOptions { LoadReferences = opt.IncludeReferences });
        var outPath = Opt(a, "-o") ?? Path.ChangeExtension(a[0], "." + fmt);
        var exporter = Exporters.Get(fmt);
        var sw = Stopwatch.StartNew();
        var report = exporter.Export(f, outPath, opt);
        Console.WriteLine($"wrote {outPath} ({fmt}) in {sw.ElapsedMilliseconds} ms: {report}");
        return 0;
    }
}
