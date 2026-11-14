# DgnKit and iModels

How iModels work on disk, how connectors and BIS shape what goes into them, how DgnKit is connected, and what iModel
structure and schema we should use going forward. Source-cited detail lives in the three research briefs:

| brief | what it covers |
|---|---|
| [imodel/research/imodel-file-format.md](imodel/research/imodel-file-format.md) | the `.bim` file: SQLite container, every table, ECDb class mapping, element ids, GeometryStream bytes, txns and changesets, writing without iTwin.js |
| [imodel/research/connectors.md](imodel/research/connectors.md) | the connector framework, Synchronizer, running modes, channels/locks, REST APIs, and how Bentley's DGN/DWG connectors actually map data (from real Bentley-produced iModels) |
| [imodel/research/bis-schemas.md](imodel/research/bis-schemas.md) | BIS layering and versioning, the BisCore class trees, "top of the world", modelling perspectives, Generic, the civil domains, property/code/label guidance, anti-patterns |

(Commit pins: imodel-native `5d613e6a`, itwinjs-core `79aa5374`, bis-schemas `60478e86`, as of 2026-11-13.)

---

## 1. What an iModel is, physically

An iModel is **one SQLite database file** (`.bim`), opened by Bentley's native library (`iTwin/imodel-native`, exposed
only through the `@itwin/core-backend` Node addon). Three layers sit on SQLite:

```
 ┌─────────────────────────────────────────────────────────────────────────────────────────────┐
 │ DgnDb   (iModelPlatform)  BIS rules: elements, models, codes, categories, geometry, txns     │  dgn_*  + bis_* data
 ├─────────────────────────────────────────────────────────────────────────────────────────────┤
 │ ECDb    EC schemas stored in the file and mapped to tables; ECSQL                            │  ec_*   metadata
 ├─────────────────────────────────────────────────────────────────────────────────────────────┤
 │ BeSQLite  SQLite + properties, local values, embedded files, changeset sessions             │  be_*
 └─────────────────────────────────────────────────────────────────────────────────────────────┘
```

* **Container:** `application_id = 'BeDb'`, page size 4096, UTF-8, WAL journal. No page compression; values are
  compressed individually (zlib for property blobs, Snappy for geometry and txns, LZMA2 for changeset files).
* **Identity:** iModel id = `be_Prop('be_Db','DbGuid')`, iTwin id = `ProjectGuid`, briefcase id and parent changeset
  in `be_Local` (never part of a changeset). Profile versions (BeSQLite / ECDb / DgnDb) gate who may open the file.
* **Self-describing:** every schema (BisCore, Generic, domains, dynamic ones) is stored in `ec_Schema`, `ec_Class`,
  `ec_Property` …, and `ec_ClassMap` / `ec_PropertyMap` / `ec_Column` say where each property physically lives.
* **Data tables:** `bis_Element` (every element: id, class, model, code, parent, label, federation GUID, JSON),
  joined tables `bis_GeometricElement3d/2d` (category, placement origin/angles/bbox, `GeometryStream` blob, plus shared
  columns `js1…js32` and an overflow table for subclass properties — so *any* subclass, including a dynamic one, needs
  no new table), `bis_Model`, `bis_ElementUniqueAspect` / `bis_ElementMultiAspect`, link tables for relationships,
  `bis_CodeSpec`, fonts, an R*-tree spatial index and full-text tables.
* **Element ids:** 64-bit `(briefcaseId << 40) | sequence`. Snapshot/standalone files use briefcase 0. Fixed rows:
  root Subject and RepositoryModel `0x1`, RealityDataSources `0xe`, DictionaryModel `0x10`.
* **Geometry:** `GeometryStream` = Snappy-compressed op stream (`{opcode, size, flatbuffer}`) of symbology changes
  (BasicSymbology: sub-category, colour `0xTTBBGGRR`, weight 0–31, line style id, transparency, display priority,
  geometry class) and primitives (lines, arcs, curve vectors, B-splines, polyfaces, solids, text, part instances,
  B-reps). Coordinates are **metres**, local to the element's placement (origin + yaw/pitch/roll, or origin + angle in 2D).
  Writers use the documented JSON form (`GeometryStreamProps` + IModelJson), which is what DgnKit emits.
* **Georeference:** `dgn_Db/Extents` (project extents), `dgn_Db/Units` (global origin, ECEF location) and
  `dgn_Db/DgnGCS` — **the GCS is stored as MicroStation's binary type-66 GCS block**, the structure DgnKit already
  decodes from DGN files.
* **Change tracking:** edits are captured with the SQLite session extension into `dgn_Txns`; pushing turns them into an
  LZMA2 changeset file whose id is a SHA-1 chained to its parent. Snapshot, standalone and briefcase files are the same
  format; only flags, briefcase id and txn handling differ. Locks live on iModelHub; codes are enforced by a unique index.

**Can we write `.bim` from C# directly?** Technically for plain rows, practically no: we would have to re-implement
ECDb's schema mapping, both id sequences, relationship class ids, byte-exact GeometryStream encoding, and the
spatial-index triggers that call native-only SQL functions; and a briefcase would also need hand-made changesets. The
engine source is public (Apache-2.0) but cannot be built without a private repo, and there is no public .NET API.
**The only supported writer is `@itwin/core-backend`** — hence the design below.

## 2. BIS in one page

BIS (Base Infrastructure Schemas) is the schema family every iModel uses.

* **Layers:** Core (BisCore, Generic, Functional, Analytical …) → Common (LinearReferencing, SpatialComposition,
  BuildingSpatial …) → Discipline-Physical (RoadRailAlignment, RoadRailPhysical, CivilPhysical, StructuralPhysical …) →
  Discipline-Other → **Application** (connector schemas, dynamic schemas). A schema may only reference its own layer or
  lower. Versions are `RR.WW.mm`; a *Production* iModel accepts only Production/Deprecated schemas — several civil
  schemas are still FieldTesting (CivilPhysical 1.0.3, RoadRailAlignment 2.0.4; 2.0.3 is Production).
* **Elements** are the unit of everything. Main branches: `PhysicalElement` (real, has mass), `SpatialLocationElement`
  (real place, no mass), `GraphicalElement3d` (3D annotation/illustration), `GraphicalElement2d`/`DrawingGraphic`
  (drawings and sheets), `InformationContentElement` (definitions, documents, links, groups, records, partitions,
  Subjects). Each element lives in exactly one **Model**; a model *sub-models* one element (partition, Drawing, Sheet).
* **Perspectives:** each `InformationPartitionElement` under a `Subject` opens a perspective (Physical, Definition,
  Document, Group, Link …) and is sub-modelled by a model of the same perspective. One physical partition per Subject.
* **Display classification:** every geometric element has one **Category** (≈ a DGN level / DWG layer); its
  **SubCategories** carry appearance (colour, weight, style, priority, visibility). Categories are not semantics.
* **Codes:** `(CodeSpec, CodeScope, CodeValue)` is unique; set CodeValue only for real identifiers, never invent one.
  `UserLabel` is the display label.
* **Properties:** essential ones on the element class; optional / source-specific ones on **ElementAspects**;
  user-defined ones on aspects of a **dynamic** schema; never user-visible data in `JsonProperties` (generic UI hides it).
* **Provenance:** `ExternalSourceAspect {Scope, Kind, Identifier, Version, Checksum, Source}` on every synchronised
  element, `RepositoryLink` per source document, `ExternalSource` per source model, `ElementHasLinks` from partitions
  to the RepositoryLink.
* **Generic** classes (`generic:PhysicalObject`, `Graphic3d`, `SpatialLocation`, `Group` …) are for data without enough
  context to classify. They are where faithful CAD conversion lands; domain classes are where it should end up.

## 3. How connectors work

A connector is a program that **synchronises** an external source into an iModel, repeatedly: it maps source data to
BIS, records provenance, and on every later run writes only what changed (that is what makes compact changesets and a
useful iModel timeline).

```
 source file ──► extract ──► align/transform to BIS ──► detect changes (ExternalSourceAspect Kind/Identifier,
                                                         Version, Checksum) ──► insert / update / delete ──► changeset
```

* **Bentley's framework** (`@itwin/connector-framework`): a `BaseConnector` subclass implements `openSourceData`,
  `importDomainSchema`, `importDynamicSchema`, `importDefinitions`, `updateExistingData`; the `ConnectorRunner` handles
  auth, briefcases, locks, channels and pushes (~5 changesets per run); the `Synchronizer` does change detection and
  file-based deletion. **Status (Nov 2026): 2.4.0 still requires iTwin.js 4.x, which reached end of life on
  2026-06-13; no 5.x release exists and the samples repos are archived.** The useful logic is ~1,600 lines.
* **Running modes:** *snapshot* (immutable, rebuilt per run — the only mode the native addon's right-to-run licence
  plainly covers), *standalone* (local, editable, incremental — good for development; licence wording needs review),
  *briefcase* (iModelHub: iTwin/iModel ids, OAuth client, locks, pushes).
* **Hosting:** Bentley's Synchronization API only runs Bentley's own connector types; a third-party connector runs on
  our machine or server and pushes to iModelHub (or uploads a baseline file through the iModels API).
* **Bentley's DGN connector** (reconstructed from Bentley-made iModels and Bentley staff answers): job Subject per
  master file; Subjects mirror the reference-attachment tree; one PhysicalPartition per V8 3D model; levels →
  `SpatialCategory` per level name; plain graphics → `generic:PhysicalObject` with no CodeValue; normal cells →
  assemblies (header without geometry, components as children, to keep their levels); shared cells → one element with
  `GeometryPart`s if single-level, else an assembly; item types → `DgnCustomItemTypes_<lib>:<Type>ElementAspect`;
  V8 EC data → converted schemas; provenance `Kind = Element / Model / Level / ViewDefinition`, `Identifier` = decimal
  V8 id, `Version` = last-modified ms. Drawings/sheets are off by default.
* **MicroStation 2026 on this machine ships that converter** (`iModel02Export.exe`, `DgnV8BridgeM02.dll`,
  `ImportConfig.xml`), invoked by MicroStation's "Save As iModel" file handler (`BimIO.dll`). It does not start
  standalone (it expects MicroStation's process environment); driving it through the COM oracle would give us a
  Bentley-made reference iModel per corpus file — the iModel equivalent of the existing MicroStation oracle.

## 4. How DgnKit connects to iModels

```
 file.dgn ─► DgnKit (C#)                 ─► file.imx.jsonl ─► connector/ (TypeScript, iTwin.js 5.15) ─► file.bim
            DgnImxExporter: DGN → BIS          IMX: format-neutral    ImxLoader: BIS tree, ids, schemas,      (snapshot, or
            semantics, metres, exact           BIS-shaped records      provenance, change detection,           standalone updated
            geometry, provenance, checksums    (docs/IMODEL-INTERCHANGE.md)  views (EditTxn)                   in place)
```

* **DgnKit does the format semantics** (`src/DgnKit.IModel`): units → metres with the global origin applied, ByLevel
  kept as "inherit from sub-category", ByCell resolved, cells and shared cells flattened, complex chains as exact
  paths, arcs and B-splines exact, dimensions regenerated, properties gathered, V8 ids as provenance, a content
  checksum per record. Output is deterministic, so checksums change only when the source does.
* **The connector does BIS and the iModel** (`connector/`): our own runner on `@itwin/core-backend` 5.15 using the
  explicit `EditTxn` API (the official framework is stuck on 4.x). It is format-agnostic: any reader that emits IMX
  produces an iModel.

```bash
dotnet run --project src/DgnKit.Cli -- imodel drawing.dgn -o drawing.imx.jsonl
```

```bash
cd connector && npm install && npm run build
```

```bash
node connector/dist/main.js drawing.imx.jsonl drawing.bim
```

```bash
node connector/dist/main.js drawing.imx.jsonl drawing.bim --standalone
```

```bash
node connector/dist/inspect.js drawing.bim
```

```bash
node connector/dist/verify.js drawing.imx.jsonl drawing.bim
```

(`dgnkit imodel <file> --bim out.bim` runs both steps.) Requires Node.js 22, 24 or 26.

### 4.1 Mapping

| DGN | iModel (BIS) | notes |
|---|---|---|
| file | `RepositoryLink` (code = path) + job `Subject` "DgnKit:<path>" | provenance scope for everything below |
| model | `ExternalSource`; 3D → child `Subject` + `PhysicalPartition`/`PhysicalModel`; 2D → `Drawing`/`DrawingModel`; sheet → `Sheet`/`SheetModel` (in "Documents") | `--2d-spatial` makes 2D design models physical (z = 0), for georeferenced plan models |
| level | `SpatialCategory` and/or `DrawingCategory` in the job's "Definitions" model; level symbology on the default `SubCategory` (colour, weight, style, priority, transparency, hidden, no-plot) | names cleaned of `<>\/."?*\|,='&` |
| element (line, line string, shape, arc, ellipse, curve, B-spline, complex chain/shape, point string, mesh, cone, text, text node) | `generic:PhysicalObject` (3D geometry), `generic:Graphic3d` (3D text/dimensions/tags), `bis:DrawingGraphic` (2D) with one exact GeometryStream | colour/weight/style only written when not ByLevel; construction/dimension/pattern class → GeometryClass; 2D priority → display priority |
| cell / shared cell / text node / dimension with components on several levels | parent element (header level) + child elements per other level (`ElementOwnsChildElements`) | BIS allows one category per element; matches Bentley's assembly approach |
| shared cell definition | expanded into each instance (v1) | `GeometryPart` support is in the IMX contract (`part`, `geomPart`) |
| dimension | regenerated graphics (lines, terminators, text) | as in the PDF export |
| displayed tag | text element; tag values → properties of the target | |
| EC instances, item types, tags, string/DB linkages | `DgnKitDynamic:<Source>_<Class>` `ElementMultiAspect`s with typed properties (dynamic schema, grows between runs) | same pattern as Bentley's `DgnCustomItemTypes_<lib>` |
| line styles 1–7 | `LineStyle` with LinePixels Code1–7 | exact equivalents |
| custom line style (stroke pattern in the file) | `LineStyle` with a stroke-pattern component (metres) + element `styleMod` scale | point symbols, parallel compound strokes and external (dgnlib) styles: v2 — each is reported as a warning naming what was approximated |
| fonts | iModel font table (TrueType / RSC / SHX by name) | glyphs are not embedded yet |
| GCS (EPSG / CS-MAP key) | iModel geographic coordinate system | coordinates stay metres in the projection |
| V8 element id, last-modified | `ExternalSourceAspect` Kind `Element`, Identifier = id, Version = ms, Checksum = IMX checksum, Scope = RepositoryLink, Source = model's ExternalSource | Kinds also `ElementLevelPart`, `Model`, `Level`, `ModelSource` |
| (views) | one ortho 3D view over the physical models; a drawing/sheet view per 2D model | |

### 4.2 Verified so far

* `dotnet test --filter IModelInterchangeTests`: all 108 corpus files map to IMX (153,723 elements + 2,879
  level-split children; unconverted: V7 dimensions, 1 multiline, 1 raster frame); records ordered with resolvable
  keys; output byte-identical across runs; model extents in metres match the (PDF-verified) display list.
* The connector builds and runs (iTwin.js 5.15.1 on Node.js 26.13.1 — newer than the 20/22/24 that iTwin.js 5.15
  lists, but its native addon loads and works). Checked on the corpus:
  * **every corpus file converts** to a snapshot iModel; `verify.js` confirms that every curve, point, mesh and text
    primitive sent is stored (iTwin.js silently drops GeometryStream JSON it rejects, so this is the check that
    matters), and that stored model extents match the IMX extents (exact for geometry; text-bound edges differ by a
    few % because glyphs are not embedded and the backend measures text with a fallback font);
  * **standalone update**: re-running unchanged input touches nothing (4,456 unchanged in 75 ms); an input with 5
    elements removed, 1 edited and 1 added gives exactly deleted 5 / updated 1 (same ElementId, new label and
    checksum) / inserted 1, and re-applying the original restores it; views, categories and models are not duplicated;
  * **georeferencing**: the GCS lands (EPSG:2927, HARN.WA-NF); with `--2d-spatial` a State Plane plan model gets real
    project extents and iTwin.js derives the ECEF location (its origin sits at the projection's false origin, as it
    should).
* Running the connector against the corpus found three DgnKit bugs, fixed in the reader / exporters (so DXF, PDF and
  KML output improve too) and now covered by tests:
  * **V8 shared cells always use the 3D body layout** (rotation/scale at +56, origin at +128), also when flagged 2D;
    2D-flagged instances (6 corpus files) were read at the wrong offsets. Origins now match the MicroStation oracle
    (and its element ranges: 1113 → 1123 of 1154 in 12_XL7089_PS_TC1); `sharedcell.origin` is now asserted.
  * **DISCONNECT vertices** (`DBL_MAX` in UORs, MicroStation's run separator — detailing-symbol leaders) were read as
    points 1e303 m away; line strings, point strings and complex chains now split at them. Against the official
    plot of CD-RAC-20, ink precision rose from 97.0% to 99.3% (the stray leader lines across the sheet are gone).
  * **Zero-height text** made iTwin.js discard the whole element (including its other geometry); it is now skipped and
    reported.
* `IModelHost` locks a per-user profile directory for the life of the process, so two connector processes cannot
  run at once on the same profile; parallel runs pass distinct `--profile` names.

## 5. A better iModel structure and schema going forward

What Bentley's own DGN conversion produces is faithful but semantically thin: everything is `generic:PhysicalObject`
on level categories with item-type aspects (BIS itself calls that the "lossy" end). The design we should grow into has
**two tiers in one iModel**, plus a few structural rules.

### 5.1 Structure

1. **One job Subject per source document**, everything that document produces below it; one RepositoryLink per
   document, one ExternalSource per source model, `ElementHasLinks` from every partition. This gives clean per-file
   update, deletion detection and "unmap".
2. **File-based provenance**: `Scope` = RepositoryLink (not the partition, which forces legacy channel-based deletion);
   stable `Kind`/`Identifier`; `Version` + `Checksum` for cheap change detection. Keep Bentley's Kind names so Bentley
   tooling recognises the data.
3. **Job-private definitions** (User rank, "Definitions" partition): categories, line styles, parts, views. Never write
   to the DictionaryModel. Accept that the same level in two jobs gives two categories (Bentley merges by name in
   the dictionary; BIS guidance now says per job/channel). A view can still select both.
4. **Perspectives honestly:** 3D design → Physical; true 2D design and details → Drawings; sheets → Sheets with view
   attachments (once DGN references are converted); georeferenced 2D plan models → spatial (`--2d-spatial`), so they
   sit on the map with the 3D data.
5. **References mirror the attachment tree** (child Subjects per referenced file/model, `ExternalSourceAttachment` with
   the transform) instead of copying referenced geometry into the master.
6. **An editing channel** (`<org>:dgnkit`) per job once several writers share an iModel.
7. **Georeference every spatial iModel** (GCS + ECEF) — DOT data is State Plane; without it the iModel cannot be
   combined with reality data or GIS.

### 5.2 Schema: two tiers

* **Tier 1 — faithful layer (now):** generic classes, level categories, exact geometry, and every source property in
  the `DgnKitDynamic` aspects. Lossless, format-independent, always produced. This is the "nothing is lost" guarantee.
* **Tier 2 — semantic layer (next):** recognise what the data *is* and use real BIS classes:
  * OpenRoads civil data that DgnKit already decodes (alignments, corridors, features) → `RoadRailAlignment:Alignment`
    with `LinearReferencing`, civil courses/surfaces → `CivilPhysical` / `RoadRailPhysical` classes — mind the
    Production/FieldTesting status of each schema version.
  * Sheet title blocks (tags) → `bis:Sheet` code (sheet number) and properties; project data → `ProjectInformationRecord`.
  * GIS feature classes (EC feature data, shapefiles) → `SpatialLocationElement` subclasses with typed properties.
  * Item types that *are* the kind of thing (e.g. "Sign", "Light Pole") → element classes in a dynamic schema that
    subclass the BisCore base directly (`PhysicalElement` / `SpatialLocationElement`), never an intermediate
    DgnKit base class (it would block re-basing onto future domain classes).
  * A small **static Application-layer schema `DgnKit`** only for concepts we understand across all formats but BIS
    lacks (e.g. a source-layer aspect for 2D/3D CAD round-tripping). Version it properly; keep dynamic schemas additive.
* **Codes:** CodeValue only for real identifiers (sheet numbers, alignment names, named cells that are catalogue
  items); `UserLabel` from the source's own label.
* **Types:** repeated cells/blocks with the same properties → `PhysicalType`/`GraphicalType2d` (+ `GeometryPart`), so
  thousands of identical signs share one definition.

### 5.3 Other formats into the same iModel

IMX is the seam: a new format needs only a reader that emits IMX; the connector, provenance, change detection and
schemas are shared.

| format | reader (C#) | BIS mapping notes |
|---|---|---|
| DWG/DXF | ACadSharp (already a DgnKit dependency, reads both) | layers → categories, blocks → `GeometryPart` + types, layouts → sheets, block attributes / XData → dynamic aspects, handles → provenance (Bentley uses `Kind=Element`, Identifier = hex handle) |
| IFC 2x3/4/4.3 | xBIM or IfcOpenShell | GlobalId → provenance; IfcProduct classes → BIS domain classes (Building*, Structural*, RoadRail*); spatial structure → SpatialComposition; property sets → aspects |
| LandXML | XML | alignments → `RoadRailAlignment`, surfaces → `Terrain`/meshes, parcels → spatial locations |
| Shapefile / GeoJSON / GeoPackage | NetTopologySuite | features → `SpatialLocation` subclasses, attributes → typed properties, CRS → GCS |
| point clouds / meshes / rasters | — | not copied into the iModel: `RepositoryLink`s in RealityDataSources + Reality Data API |

## 6. Next steps

1. Look at the iModels in an iTwin viewer (the checks so far are structural and numeric, not visual). Decide whether
   2D design models that carry a GCS should default to spatial (today they need `--2d-spatial`).
2. Bentley oracle: drive MicroStation 2026's "Save As iModel" through `tools/oracle/MstnOracle` and compare class,
   category, provenance and geometry statistics with ours (an `inspect.js --json` diff).
3. GeometryParts for shared cells; reference attachments as Subjects + `ExternalSourceAttachment`; multilines; custom
   line style point symbols; embedded fonts.
4. Tier-2 semantics, starting with OpenRoads alignments and sheet title blocks.
5. iModelHub briefcase mode (OAuth client, locks, channel) on the same loader.
