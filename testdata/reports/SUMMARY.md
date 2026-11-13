# Verification summary (2026-11-12)

## Corpus
108 public DGN files + 47 official plot PDFs fetched by `tools/fetch-corpus.ps1` (manifest `testdata/corpus.json`):
WSDOT contracts XE3736 / XE3691 / XE3771 (OpenRoads), XE3649 (V8i); Nebraska DOT lighting details + ORD plat border
(no public plan PDFs exist for Nebraska — lettings are behind Bid Express); TxDOT standard sheets V7 → MicroStation 2025;
NJDOT traffic details (V8 2004); FDOT standard plans FY2027-28 (CONNECT); GDAL test files.

**Read health:** 108/108 files read, 0 diagnostics, 2–340 ms per file.

## 1. Field-by-field vs MicroStation 2026 (COM oracle, 15 files incl. V7)
Full table: `oracle-report.md`.

| area | match |
|---|---|
| elements found / type / colour / weight / style / level | 100% |
| line/shape vertices, arcs (centre, radii, start point) | 100% |
| cell & shared-cell names and origins, shared-cell resolution | 100% |
| levels: name, code, colour, weight, style, displayed, plot | 99.7–100% |
| models: units (UOR/master), global origin, 2D/3D | 100% |
| tags: value, target element, tag set, origin | 100% (name 99.3%) |
| text strings (incl. RSC fraction glyphs) | 99.5% |
| text justification point | 98.7% (V7 has no stored extents) |
| element range | 97.8% (cell ranges computed differently by MicroStation) |
| text height | 96.4% (remaining = annotation-scaled text: we report model height, MicroStation reports paper height) |

## 2. vs official DOT plot PDFs (91 sheet comparisons)
Full table: `pdf-report.md` (+ overlay PNGs in `pdf/`).

| source | sheets | median text recall* | median ink F1 (text-registered) |
|---|---|---|---|
| NJDOT | 23 | 100% | 91% (23 registered) |
| WSDOT | 37 | 90% | 90% (33 registered) |
| TxDOT | 17 | 99% | 98% (5 registered; 12 PDFs have no text layer, all-sheet median 88%) |
| FDOT | 14 | 95% | 88% (14 registered) |
| **all** | 91 | **94.6%** (79 sheets with a text layer) | **90.5%** (75 registered; 59 ≥ 85%) |

\* share of the official page's words that DgnKit extracts from the DGN. Gaps come from content that lives in reference
files not published with the contract and custom line-style symbols. Dimension graphics/text are now synthesised
(V8 linear dims + notes; e.g. FDOT 110-100 recall 51.1 → 53.7% on the `Default` model, ink F1 74.4 → 78.7%).

**Baseline shift (2026-11-12, model selection).** `PdfComparer.PickModel` now compares the model whose display list
*with references* draws the most text (ties: most own text, then file order) instead of the model with the most own
text. Sheet models that reference their drawings now win: all 14 FDOT sheets read `Border Sheet` (self-references
`Text`, `Default`, `Details`, …), several TxDOT/WSDOT files read their `[Sheet]` model. Two fixes were needed for the
plotted model to export correctly: reference attachments (type 100) were decoded 32 bytes early (view flags are
12 bytes, not 8), which flattened every self-referenced model and dropped attachment scales such as FDOT's 1/24; and
text registration falls back to a consensus fit when the median fit misses (our narrower TrueType glyphs biased it).
Before → after on the same sources: FDOT recall 83 → 95%, ink F1 78% (6 registered) → 82% (14 registered); WSDOT 30
→ 33 registered (11_XL7089_PS_CS1 0.4 → 89%, 316251H_PS_VM 0.2 → 86%, 13_XL7089_PS_TC2 49 → 96%). The table also
includes a concurrent text-size fix (cap height = text size, `FontMetrics`), which took FDOT ink F1 82 → 88% and TxDOT
91 → 98%. Text precision dips by 1–5 points on sheet models, which also carry words outside the plot area (FDOT
model-scale bubbles, `$DATE$`/`$TIME$` fields). Reference clip boundaries are decoded but not applied. Applying them
would not help here: FDOT ink precision is 98–99.8%. On 102-600-5of11 the `Default` geometry beyond its decoded clip
box does appear in the official plot. The remaining FDOT loss is ink recall (66–83%), mostly the unpublished border
reference `..\ADMIN\SPBd001.dgn`.
Ink F1 for PDFs without a text layer uses bounding-box registration and is a lower bound.

## 3. Exporter round trips (`ExporterTests`, 18 cases)
DXF and DWG re-read with ACadSharp (entity counts match), PDFs re-read with PdfPig (searchable text), GeoJSON/KML/KMZ of a
WSDOT GIS model projected from WA State Plane South (EPSG:2927) land inside Washington State.
