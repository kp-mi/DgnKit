# DgnKit architecture

```
                 ┌──────────────────────────── DgnKit (netstandard2.0, no dependencies) ───────────────────────────┐
  file.dgn ──►   │ Cfb/CompoundFile ─► Formats/V8 (V8Container, V8ElementDecoder, V8Reader, V8Tables, …)         │
                 │ (raw bytes)       Formats/V7 (V7Reader: ISFF words, VAX doubles)                             │
                 │                          │                                                                   │
                 │                          ▼                                                                   │
                 │   Model: DgnFile ─ Models ─ Elements (typed: Line, LineString, Arc, Text, Cell, SharedCell…) │
                 │          LevelTable, LineStyleTable, FontTable, ColorTable (+ extended colours), TagSets      │
                 │          Properties: Linkages, XAttributes → PropertyGroups (EC/BECXML, strings, DB links)   │
                 │                          │                                                                   │
                 │                          ▼                                                                   │
                 │   Graphics: DisplayListBuilder → Primitive (Polyline | Arc | Text | Point | Mesh)            │
                 │             · master units of the root model    · ByLevel/ByCell resolved to RGB/weight/style│
                 │             · cells & shared cells instanced     · reference attachments transformed          │
                 └──────────────────────────────────────────────────────────────────────────────────────────────┘
                                            │ primitives + metadata
                 ┌───────────────── DgnKit.Export (net8.0) ──────────────────┐
                 │ CadExporter  → DXF / DWG (ACadSharp)                       │
                 │ GeoJsonExporter, KmlExporter (KML/KMZ) + GeoProjector (ProjNet, built-in State Plane/UTM WKT) │
                 │ PdfExporter  → MiniPdf (own dependency-free vector PDF writer) │
                 │ JsonDump     → full engineering-data JSON                  │
                 └────────────────────────────────────────────────────────────┘
                 ┌───────────── DgnKit.IModel (net8.0) ─────────────┐        ┌──────── connector/ (TypeScript) ────────┐
  DgnFile ──►    │ DgnImxExporter: DGN → BIS (exact geometry, metres,│ IMX ──►│ ImxLoader on @itwin/core-backend 5.x:    │──► .bim
                 │ provenance, checksums) → ImxWriter (NDJSON)       │        │ BIS tree, schemas, sync, views (EditTxn) │
                 └───────────────────────────────────────────────────┘        └──────────────────────────────────────────┘
                 DgnKit.Cli: info | scan | dump | export | imodel
```

The iModel path deliberately does **not** go through the display list: iModels keep exact curves, ByLevel symbology
(inherited from sub-categories) and element identity, so `DgnImxExporter` walks the element tree itself and reuses only
the shared pieces (colour table, dimension regeneration, curve stroking for curve strings). See docs/IMODEL.md.

## Design choices (and the iModel parallels)

| DgnKit | iModel (imodel-native) | notes |
|---|---|---|
| `DgnFile` | `DgnDb` | one file, many models, file-level dictionaries |
| `DgnModel` | `DgnModel` (Physical/Drawing/Sheet) | units + global origin per model (V8 keeps UORs; iModels store metres) |
| `Level` | `DgnCategory` + default `DgnSubCategory::Appearance` | ByLevel symbology = "appearance not overridden" |
| `Element` + `Children` | `GeometricElement` + `GeometryStream` | V8 complex elements are trees; iModels flatten into one stream |
| `SharedCellDefinition` | `DgnGeometryPart` | instanced geometry referenced by name (V8) / id (iModel) |
| `PropertyGroup` (EC instances, tags, linkages) | `ElementAspect` / EC properties | feature metadata travels with the element |
| `Primitive` (display list) | `GeometricPrimitive` + `GeometryParams` | neutral, exporter-friendly geometry with resolved symbology |
| `ReferenceAttachment` | `ExternalSourceAttachment` + separate model | transform = rotation·scale about ref/master origins |

* **Readers decode, builders interpret.** `Formats/*` only decode records into typed elements (UORs, raw symbology). All
  interpretation — unit conversion, ByLevel/ByCell, cell instancing, curve stroking — happens once in
  `DisplayListBuilder`, so every exporter gets identical geometry.
* **Robust by construction.** Every reader path tolerates truncated/corrupt data and records `DgnFile.Diagnostics`
  instead of throwing; the whole 108-file corpus reads with zero diagnostics.
* **Ground-truth driven.** Format knowledge is verified three ways: SDK headers, MicroStation COM oracle dumps
  (`tools/oracle`), and the official plot PDFs (`tests/…/PdfComparer`).
* **No Bentley runtime.** The core library has no dependencies and targets netstandard2.0 (usable from .NET Framework
  4.8 MicroStation add-ins and from .NET 8+ services/Linux containers).
