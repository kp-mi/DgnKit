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
                "imodel" => IModelCmd(args.Skip(1).ToArray()),
                "logic" => LogicCli.Run(args.Skip(1).ToArray()),
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
              dgnkit imodel <file.dgn> [-o out.imx.jsonl] [--model name] [--2d-spatial] [--bim out.bim]
                            map the DGN onto BIS and write the iModel interchange (IMX, docs/IMODEL-INTERCHANGE.md);
                            --bim also runs the iTwin.js connector (connector/, needs Node.js) to create a snapshot iModel
                            --logic [rules] adds engineering-logic classifications and relationships (docs/LOGIC.md)
              dgnkit logic  <file.dgn|file.geojson>... [--rules builtin|builtin:<pack>|file|dir]... [--model name] [--no-refs]
                            [-o out.logic.json] [--report out.md] [--graphml out.graphml] [--cypher out.cypher] [--neo4j dir]
                            classify engineering objects, infer relationships, derive, validate (docs/LOGIC.md)
              dgnkit logic query <input>... --from <key> [--query downstream|upstream|feeds|network|supply] [--rules …]
              dgnkit logic rules [--rules …]                    list packs, classes, relations and rules
              dgnkit logic scan <dir> [--rules …] [--csv out.csv] [--report out.md]
                            run the rules over every *.dgn / *.geojson under dir
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

    private static int IModelCmd(string[] a)
    {
        var sw = Stopwatch.StartNew();
        var f = DgnFile.Open(a[0]);
        var imx = Opt(a, "-o") ?? Path.ChangeExtension(a[0], ".imx.jsonl");
        var model = Opt(a, "--model");
        DgnKit.IModel.Dgn.ImxEnrichment? enrichment = null;
        if (Flag(a, "--logic"))
        {
            // optional value: --logic <rules>; a following option or nothing means the built-in packs
            var lv = Opt(a, "--logic");
            var rules = DgnKit.Logic.Rules.RuleSet.FromPaths(lv == null || lv.StartsWith("-") ? new[] { "builtin" } : new[] { lv });
            var fs = DgnKit.Logic.Dgn.DgnFeatureExtractor.Extract(f, new DgnKit.Logic.Dgn.DgnFeatureOptions { Models = model == null ? null : new[] { model }, IncludeReferences = false });
            var lr = DgnKit.Logic.Engine.LogicEngine.Run(fs, rules);
            enrichment = DgnKit.IModel.Logic.LogicImx.ToEnrichment(lr);
            Console.WriteLine($"logic: {lr.Classified.Count()} classified, {lr.Graph.Edges.Count} edges, {lr.Findings.Count} findings; {enrichment.Relationships.Count} relationships to write");
        }
        var report = DgnKit.IModel.Dgn.DgnImxExporter.Export(f, imx, new DgnKit.IModel.Dgn.DgnImxOptions
        {
            TwoDAsSpatial = Flag(a, "--2d-spatial"),
            Models = model == null ? null : new[] { model },
            Enrichment = enrichment,
        });
        Console.WriteLine($"wrote {imx} in {sw.ElapsedMilliseconds} ms: {report}" + (report.DroppedRelationships > 0 ? $"; {report.DroppedRelationships} relationships dropped (end not in this file)" : ""));

        var bim = Opt(a, "--bim");
        if (bim == null) return 0;
        var connector = FindConnector();
        if (connector == null)
        {
            Console.Error.WriteLine("error: connector/dist/main.js not found — build it first (cd connector && npm install && npm run build)");
            return 3;
        }
        var psi = new ProcessStartInfo("node") { UseShellExecute = false };
        foreach (var arg in new[] { connector, imx, bim }) psi.ArgumentList.Add(arg);
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { p = null; } // not on PATH
        if (p == null)
        {
            Console.Error.WriteLine("error: could not start node — is Node.js (22, 24 or 26) installed and on PATH?");
            return 3;
        }
        using (p)
        {
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    /// <summary>Looks for the built iTwin.js connector (connector/dist/main.js) above the executable and the working directory.</summary>
    private static string? FindConnector()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "connector", "dist", "main.js");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
