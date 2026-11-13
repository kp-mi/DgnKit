# DgnKit — standalone MicroStation DGN reader & converter

DgnKit reads Bentley MicroStation **DGN** files — **V7 (IGDS/ISFF)**, **V8 / V8 2004 / XM / V8i**, **CONNECT Edition /
OpenRoads Designer**, and **MicroStation 2024–2026** — **without any Bentley runtime**, and exports the engineering data to
**DXF, DWG, KML, KMZ, GeoJSON, PDF** and a full **JSON** dump.

* geometry (lines, line strings, shapes, curves, arcs/ellipses, complex chains/shapes, B-splines, cones, text, text
  nodes, cells, shared cells, tags, point strings …) with **layer (level) names**
* symbology: colour table + extended (true) colours, weights, built-in & custom **line styles** (names/numbers),
  ByLevel/ByCell resolution, level overrides, display priority, fill
* **feature metadata**: EC instances (OpenRoads civil data, GIS feature classes, item-type style data from binary EC XML
  XAttributes), EC relationships, **tags** (title-block data) resolved to their target elements, string linkages,
  database (MSLINK) linkages
* models (design / sheet / definition), units (master/sub/storage), global origin, reference attachments (optional
  recursive loading)

```
src/DgnKit          core reader + display list (netstandard2.0, zero dependencies — usable from MicroStation .NET 4.8 add-ins)
src/DgnKit.Export   exporters (net8.0): ACadSharp (DXF/DWG), ProjNet (CRS), own MiniPdf writer, KML/KMZ/GeoJSON writers
src/DgnKit.Cli      command line tool
tests/DgnKit.Tests  corpus tests: MicroStation-oracle comparison, official-PDF comparison, exporter round trips
tools/              corpus fetcher, MicroStation COM oracle (MstnOracle)
docs/               FORMAT-V8.md (reverse-engineered format notes), ARCHITECTURE.md
testdata/           corpus manifest, oracle dumps, comparison reports
```

## Quick start

```bash
dotnet build DgnKit.slnx -c Release
```

```bash
dotnet run --project src/DgnKit.Cli -- info drawing.dgn
```

```bash
dotnet run --project src/DgnKit.Cli -- export drawing.dgn -f dwg -o drawing.dwg
```

```bash
dotnet run --project src/DgnKit.Cli -- export design.dgn -f kmz -o design.kmz --crs EPSG:2927
```

```bash
dotnet run --project src/DgnKit.Cli -- dump drawing.dgn -o drawing.json
```

CLI commands: `info`, `scan <dir>`, `dump`, `export -f dxf|dwg|kml|kmz|geojson|pdf|json [--model name] [--crs EPSG:xxxx|file.prj|WKT|local] [--refs] [--paper A1|ANSI-D|WxHmm]`.

KML/KMZ/GeoJSON need no `--crs` when the DGN carries a geographic coordinate system: the model's MicroStation GCS
(`DgnModel.GeoCoordinateSystem`, e.g. `EPSG:2927` or CS-MAP key `HARN.WA-NF`) is used, or, as a checked fallback, the
spatial reference of imported GIS features (`DgnFile.GeoCoordinateSystem`). `--crs` overrides it; `--crs local` keeps
master units in GeoJSON. See `docs/FORMAT-V8.md` §7.1.

### Library

```csharp
using DgnKit.Model;
using DgnKit.Graphics;
using DgnKit.Export;

var file = DgnFile.Open("sheet.dgn", new DgnReadOptions { LoadReferences = true });
foreach (var level in file.Levels.All) Console.WriteLine($"{level.Id} {level.Name} color={level.Color}");

var model = file.PrimaryModel!;
foreach (var p in DisplayListBuilder.Build(file, model, new DisplayListOptions()))
    Console.WriteLine($"{p.Kind} on '{p.LevelName}' {p.Symbology.Color} style={p.Symbology.StyleName}");

foreach (var e in model.AllElements())
    foreach (var g in e.PropertyGroups)          // EC / Tag / StringLinkage / Database metadata
        Console.WriteLine($"{e.Id} {g.Source}:{g.Name} {string.Join(", ", g.Properties)}");

Exporters.Get("dxf").Export(file, "sheet.dxf", new ExportOptions());
```

Geographic exports (KML/KMZ, projected GeoJSON) need the source CRS: built-in WKT covers NAD83 State Plane zones for
WA, NE, TX, FL, OH, NJ (metres and US survey feet, NAD83 / HARN / 2011 codes) and UTM; anything else can be supplied as
WKT or a `.prj` file.

## Test corpus & verification

`tools/fetch-corpus.ps1` downloads the public corpus described in `testdata/corpus.json` (108 DGNs, 47 official plot
PDFs): **WSDOT** contract plan sets (ORD & V8i), **Nebraska DOT** (former Department of Roads) details, **TxDOT**
standard sheets (V7 → MicroStation 2025), **NJDOT** traffic details (V8 2004), **FDOT** standard plans (CONNECT), GDAL
test files.

Three independent checks run in `dotnet test`:

1. **MicroStation oracle** — `tools/oracle/MstnOracle` drives the locally licensed MicroStation 2026 through COM and dumps
   levels/models/elements; `OracleComparer` diffs DgnKit field-by-field → `testdata/reports/oracle-report.md`.
2. **Official plot PDFs** — `PdfComparer` extracts the text layer of the DOT-published PDF (PdfPig), measures how much of
   it DgnKit recovers from the DGN, then rasterises the official page and DgnKit's own PDF export, registers them using
   matched words, and scores ink overlap → `testdata/reports/pdf-report.md` (+ overlay PNGs).
3. **Exporter round trips** — DXF/DWG re-read with ACadSharp, PDFs with PdfPig, GeoJSON/KML coordinates checked to fall
   inside Washington State after State Plane → WGS84 projection.

Latest results (`testdata/reports/SUMMARY.md`): all 108 corpus files read with zero diagnostics; against MicroStation
itself, element type/colour/weight/style/level, vertices, arcs, cell origins, level tables, units, global origin and tag
values match **100%** (text strings 99.5%); against the official DOT PDFs, DgnKit recovers a median **92.9%** of the
plotted words and its own PDF plots overlap the official sheets with a median ink F1 of **90.9%**.

## Status / known gaps

Decoded and verified: container, element headers, display headers, all common geometry, complex/cell/shared-cell
structure, text, levels (incl. symbology & flags), fonts, line style names/numbers, units & global origin, extended
colours, XAttributes, binary EC XML, tags, reference attachments, V7.

Dimensions (V8): definition points, type, values, unit formatting, override/text-block texts decode (100% vs the
MicroStation oracle on 4 files); linear dimensions and notes are drawn (extension/dimension lines, arrows/dots, text) —
angular/radial kinds and V7 dimensions only partially (`docs/FORMAT-V8.md §4.2`).

Not yet: custom line style **stroke definitions** (custom styles currently plot continuous), multiline
graphics (MicroStation regenerates them from definitions), polyface mesh payloads,
raster attachments, GCS auto-detection from the file, level "frozen" flag, sheet vs design model type. See
`docs/FORMAT-V8.md §9`.
