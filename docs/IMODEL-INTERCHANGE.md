# IMX — the DgnKit iModel interchange (v1)

IMX is the hand-off between a **format reader** (DgnKit for DGN today; DWG, IFC, LandXML, GIS readers later) and the
**iTwin.js loader/connector** in [`connector/`](../connector) that writes the iModel. Readers do the format semantics
(units, symbology resolution, cells, provenance); the loader owns BIS structure, schemas, ids, change detection and
iModelHub. One loader serves every format.

```
 file.dgn ─► DgnKit (C#, DgnImxExporter) ─► file.imx.jsonl ─► connector (TypeScript, @itwin/core-backend) ─► .bim / iModelHub
 file.dwg ─► (future reader) ───────────────┘
```

* Newline-delimited JSON (UTF-8), one record per line, `kind` first.
* Records appear in dependency order: `header`, `font`*, `lineStyle`*, `category`*, `subCategory`*, `model`*, `part`*,
  `element`*. A record only references keys of records that appeared before it (an element's `parent` precedes it).
* **All coordinates are metres** in the iModel's world frame (DGN: `(uor − globalOrigin) / uorPerMaster × metresPerMaster`).
* `key`s are unique within the file and are only used to link records; the loader maps them to element ids.
* `source` = `{ kind, id, info? }` is the stable identity in the source repository → `bis:ExternalSourceAspect`
  (`Kind`, `Identifier`, `JsonProperties` ← `info`). It must not change between runs for the same source object.
* `checksum` (content records) = first 32 hex digits of SHA-256 over the record's JSON without the checksum →
  `ExternalSourceAspect.Checksum`. An unchanged checksum means "skip on update".

## Records

### header (exactly one, first)

| field | |
|---|---|
| `format`, `version` | `"dgnkit-imx"`, `1` |
| `generator` | producing tool and version |
| `units` | always `"meter"` |
| `source` | `{ path, name, format ("DGN V8"…), formatVersion, size, modified (ISO-8601 UTC), sha256 }` → `bis:RepositoryLink` |
| `spatialExtents` | `{ low:[x,y,z], high:[x,y,z] }` of all spatial geometry → project extents |
| `gcs` | optional `{ epsg, csmapKey, description, unitMeters, wkt, source, verticalCRS? }` — the GCS of the spatial coordinates. `verticalCRS` is the iTwin.js vertical datum of the heights: `ELLIPSOID` (default), `NAVD88`, `NGVD29`, `GEOID` or `LOCAL_ELLIPSOID` |

### font

`{ key, name, type }` — `type` ∈ `truetype | rsc | shx` → iModel font table (`FontType` 1/2/3).

### lineStyle

`{ key, name, description?, linePixels?, strokes?, phase?, annotation?, approximation?, source }`

* `linePixels` 1..7: cosmetic pattern (MicroStation styles 1–7 = iTwin.js `LinePixels.Code1..7`).
* `strokes`: `[{ length (m), dash (bool), width? (m) }]`, alternating dash/gap, at style scale 1.
* Neither → drawn continuous (the loader creates no style element; elements using it draw solid).
* `approximation`: set by the producer when the record loses something the source style has — e.g. "point symbols
  not converted; drawn with its main dash pattern", or "no definition in the source file (external resource or
  dgnlib); drawn continuous". Absent when exact, including genuinely continuous styles. Loaders report it verbatim.
* Key `ls:0` is reserved for "explicitly continuous" (no record).

### category

`{ key, name, description?, usage:["spatial"|"drawing"…], appearance, source }` → `bis:SpatialCategory` and/or
`bis:DrawingCategory` (one per usage) with the appearance on the default `bis:SubCategory`.

`appearance` = `{ color (ColorDef 0xTTBBGGRR), weight (0–31), style? (lineStyle key), priority?, transparency?
(0–1), invisible?, dontPlot?, dontLocate? }`.

### subCategory

`{ key, category, name, appearance }` → additional `bis:SubCategory` of the category (reserved; DgnKit splits
multi-level cells into child elements instead).

### model

`{ key, name, description?, type, is3d, masterUnit?, metersPerMaster, extents?, sheetSize?, source }`

| `type` | BIS |
|---|---|
| `physical` | `bis:PhysicalPartition` → `bis:PhysicalModel` (3D) |
| `drawing` | `bis:Drawing` (in the job's `bis:DocumentListModel`) → `bis:DrawingModel` (2D) |
| `sheet` | `bis:Sheet` → `bis:SheetModel` (2D) |

### part (reserved in v1 writers)

`{ key, name, is3d, geometry, source }` → `bis:GeometryPart` (geometry in part-local metres).

### element

`{ key, model, category, role, parent?, userLabel?, origin, geometry, properties?, modified?, source }`

* `role`: `geometry` → `generic:PhysicalObject` (physical model) / `bis:DrawingGraphic` (drawing, sheet);
  `annotation` (text, dimensions, tags) → `generic:Graphic3d` / `bis:DrawingGraphic`.
* `parent`: key of the owning element (`bis:ElementOwnsChildElements`). DgnKit uses it when one DGN element (cell,
  shared cell, text node, dimension) has components on several levels: BIS allows one category per element, so the
  components on other levels become children (`source.kind = "ElementLevelPart"`, id `"<v8 id>/<level id>"`).
* `origin`: placement origin `[x,y,z]` (3D) or `[x,y]` (2D); rotation is always identity, geometry is relative to it.
* `geometry`: iTwin.js `GeometryStreamProps` with keys instead of ids (`font`, `style`, `part`, `subCategory`).
* `properties`: `[{ source, name, schema?, class?, values:{…} }]` — EC instances, item types, tags, string/database
  linkages, civil feature data.
* `modified`: source modification time → `ExternalSourceAspect.Version`.

## Geometry stream entries

Exactly the iTwin.js JSON forms (`@itwin/core-common` `GeometryStreamEntryProps`, `@itwin/core-geometry` IModelJson),
coordinates relative to the element origin:

| entry | DGN source |
|---|---|
| `{"appearance":{color?,weight?,style?,transparency?,displayPriority?,geometryClass?}}` | element symbology; omitted members = ByLevel (inherit from the category's sub-category). Every appearance entry restarts from the sub-category. |
| `{"fill":{display,color?,transparency?}}` | area fill (`display` 1 = by view, 2 = always); applies until the next appearance |
| `{"styleMod":{scale?,dashScale?,gapScale?,startWidth?,endWidth?,distPhase?,fractPhase?,centerPhase?}}` | line style modifiers (metres) |
| `{"lineSegment":[p0,p1]}`, `{"lineString":[…]}`, `{"pointString":[…]}` | line (3), line string (4), shape (6), curve (11, stroked), point string (22) |
| `{"arc":{center,vectorX,vectorY,sweepStartEnd:[deg,deg]}}` | arc (16), ellipse (15) |
| `{"bcurve":{points,knots,order}}` | open B-spline (27): full clamped knot vector; rational poles as `[wx,wy,wz,w]` |
| `{"path":[curves]}`, `{"loop":[curves]}` | complex chain (12) / complex shape (14); `loop` only when filled (a loop renders as a surface) |
| `{"indexedMesh":{point,pointIndex}}` | mesh (105): 1-based indices, 0 ends a face |
| `{"cone":{start,end,startRadius,endRadius,capped}}` | cone (23) |
| `{"textString":{text,font,height,widthFactor?,origin,rotation?}}` | text (17), text node (7) children, displayed tags (37), dimension text; `height` must be > 0 — iTwin.js rejects an element's whole GeometryStream when it holds a zero-height text, so producers drop (and report) such text |
| `{"geomPart":{part,origin,rotation?,scale?}}` | reserved for shared cells (v1 expands them) |

`geometryClass`: 0 primary, 1 construction, 2 dimension, 3 pattern. `displayPriority` is 2D only and adds to the
sub-category priority (iModel and MicroStation both sum level/sub-category and element priority).

## Not in v1

Shared cells as `bis:GeometryPart`s, reference attachments (each referenced file as its own `RepositoryLink` +
models; 3D → merged into spatial coordinates, 2D → drawing/sheet attachments), raster/point-cloud attachments, multiline
graphics, custom line style point symbols/compounds, saved views, materials, V7 dimensions.
