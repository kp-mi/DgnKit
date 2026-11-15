# DgnKit engineering logic layer

The logic layer turns drawing and GIS content into an **engineering knowledge graph**:

* **Nodes** are typed engineering objects: a wall, a window, a sewer lateral, a gas main, a sign, a sign mount.
* **Edges** are relationships: `hostedBy`, `connectsTo`, `flowsInto`, `feeds`, `supportedBy`, `locatedAlong` and so on.

Declarative rules do four jobs:

1. **Classify** source objects from levels, cells, EC classes, tags and attributes.
2. **Infer** relationships from geometry and attributes. These are confidence-weighted heuristics, and every edge carries an explanation.
3. **Derive** relationships by composing others. Example: gas main → services → meters → premises gives `feeds`.
4. **Validate** the graph:
   * hard requirements: a window is hosted by exactly one wall;
   * soft expectations with priors: a roof usually sits on the walls, but not always;
   * prohibitions: gas never connects to water;
   * attribute checks: a lateral is smaller than its main; gravity pipes slope downhill;
   * acyclic flow, and default-deny connectivity.

Results go to three places:
* **iModels:** BIS link-table relationships and classification aspects, through IMX and the connector.
* **Graph tools:** Neo4j CSV, Cypher (Neo4j, Memgraph, Apache AGE, LadybugDB) and GraphML (yEd, Gephi, NetworkX).
* **Reports:** JSON and Markdown.

```
 DGN ─► DgnFeatureExtractor ─┐                    ┌─► JSON / Markdown report
 GeoJSON ─► GeoJsonFeatures ─┼─► FeatureSet ─► LogicEngine(RuleSet) ─► LogicResult ─┼─► GraphML / Neo4j CSV / Cypher
 (IFC, IMX … later) ─────────┘   (metres)       classify → compute →  graph +       └─► LogicImx ─► IMX relationship records
                                                relate → derive →     findings +            ─► connector ─► DgnKitLogic:* in .bim
                                                validate              rule stats
```

## 1. Analysis (three parallel studies, 2026-11-14)

| Study | Key findings |
|---|---|
| **Domain rules**: IFC 4.3, CityGML 3.0, Esri Utility Network, INSPIRE/MUDDI, BIS, Ten States Standards, 49 CFR 192, HEC-22, MUTCD | **Ten rule types:** existence, cardinality, topology, hosting, containment, spatial heuristic, flow, prohibition, attribute, spacing.<br>**Seed catalogue:** 91 rules. The packs in `/rules` implement them in executable form.<br>**Confidence model:** `logit P = logit prior + Σ log LR`, multiplied by the class confidences.<br>**Two policies:** default-deny connectivity, following Esri ("no rule ⇒ not allowed"); and an open world, where a missing counterpart may be *unresolved* or *implicit* rather than an error.<br>**BIS gaps:** no hosting, attachment, gas, sign or guardrail classes. That means dynamic `bis:ElementRefersToElements` subclasses. |
| **Technology**: graph stores, rule engines, spatial indexes, licences, status in November 2026 | **The bottleneck is spatial candidate generation.** No embedded graph database or rule engine uses a spatial index, so we need our own whichever we pick. After that, scoring, assignment, closure and validation are small algorithms. |
| **Corpus**: 108 DGNs, the full signal inventory | **52% of elements sit on generic levels.** Semantics come from cell names, WSDOT levels, GIS EC classes and title-block tags.<br>**What the corpus contains:** work-zone traffic control, WSDOT LRS mileposts and routes, streams and title blocks.<br>**What it lacks:** drainage, sewer, water, gas and buildings, and real 3D. Those packs are verified on synthetic fixtures. |

### Technology options considered

| Option | Verdict | Why |
|---|---|---|
| Own in-memory property graph + R-tree + JSON rule DSL (netstandard2.0, 0 deps) | **chosen** | Meets every constraint; spatial-aware; explainable; byte-stable output |
| Neo4j / Memgraph / FalkorDB / ArangoDB | export target only | Servers; GPLv3 / BSL / SSPL licences |
| Kùzu | rejected | Archived October 2025 |
| LadybugDB (Kùzu fork) | export target; revisit 2027 | Young; native + OpenSSL 3; diverged file format |
| SQLite (+ recursive CTE, R*Tree) | optional adapter later | Fine for persistence, but native and not needed. iModels themselves are SQLite + ECSQL `WITH RECURSIVE` |
| DuckDB + DuckPGQ | rejected | Research extension downloaded at runtime |
| NRules (Rete) | rejected | Rules are compiled C#, not data; Rete joins ignore spatial indexes |
| Datalog (Soufflé, Nemo, DDlog) | semantics borrowed | No .NET runtime; no geometry; DDlog archived. We keep stratified evaluation (positive recursion in `derive`, negation only in validation) |
| RDF/OWL + SHACL (dotNetRDF) | rejected as runtime | OWL is open-world, wrong for compliance checks. `require` maps 1:1 to SHACL Core, so shapes can be exported later |
| ProbLog / Markov logic | rejected | Research-grade and intractable at scale. Log-odds evidence is explainable and O(candidates) |
| QuikGraph | rejected | Last release 2022; no properties; we need about five algorithms |
| NetTopologySuite | optional adapter later | BSD-3, but brings System.Memory into the zero-dependency core |

## 2. Decision

* **`src/DgnKit.Logic`** (netstandard2.0, zero dependencies, usable inside MicroStation .NET 4.8 add-ins) is format-neutral. It contains:
  * `Json/` — reader with comments and trailing commas, plus a writer;
  * `Geometry/` — metres, AABBs, exact 2D/3D distances, crossings, rings;
  * `Spatial/` — STR-packed R-tree;
  * `Model/` — Feature, Ontology (classes with traits, relations), LogicGraph;
  * `Rules/` — expression language, rule packs;
  * `Engine/` — the stratified run, scopes and functions;
  * `Query/` — traces, components, anti-joins;
  * `Interop/` — GeoJSON in; JSON, Markdown, GraphML, Neo4j and Cypher out.
* **`src/DgnKit.Logic.Dgn`** (netstandard2.0) is the DGN adapter.
* **`src/DgnKit.IModel/Logic/LogicImx.cs`** maps a result onto IMX enrichment. The connector writes it as `DgnKitLogic:*` relationships.
* **Rule packs** are JSON files in `/rules`, embedded in the assembly (`RuleSet.Builtin()`).
* **Not in v1** (each would be an additive adapter):
  * SQLite persistence, NetTopologySuite, an RDF/SHACL export, a Cypher parser;
  * run-based spacing rules (spacing is checked per pipe), learned weights;
  * IFC/IMX input adapters, and incremental or tiled re-inference.

## 3. Execution plan and status

| # | Step | Status |
|---|---|---|
| 1 | Core: JSON, geometry, R-tree, ontology, graph, expression language, rule loader | done — unit tests (parser, expressions, R-tree vs brute force, predicates) |
| 2 | Engine: classify / compute / relate / derive / require, expect, forbid, check, acyclic, default-deny; statuses violated, unresolved, implicit, unusual, anomaly; per-rule statistics | done — fixtures |
| 3 | Queries and writers | done — round-trip tests |
| 4 | Adapters: DGN feature extractor, GeoJSON reader | done — 108 corpus files, no errors |
| 5 | Rule packs (9 domain packs + core) | done — 201 rules, 125 classes, 32 relations |
| 6 | CLI `dgnkit logic …`, `dgnkit imodel --logic` | done |
| 7 | IMX relationship records + connector (dynamic relationship schema, insert/update/delete) | done — verified on a corpus .bim, incremental update tested |
| 8 | Corpus verification | done — [`testdata/reports/logic-report.md`](../testdata/reports/logic-report.md) |
| 9 | Documentation | this file, README, ARCHITECTURE, IMODEL-INTERCHANGE |

## 4. Concepts

**Feature** (input). One source object:
* identity: `Key`, `Source`, `Container`, and a `Frame` (only features in the same frame relate spatially);
* naming signals: `Kind` (element type), `Layer` and all `Layers`, `Cell`, `Text`;
* data: `Properties` (EC, item types, tags, GIS attributes; names are case-insensitive) and `Context` (e.g. `sheetTitle`, `planReference`, `units`);
* `Group` (graphic group);
* geometry in metres: points, polylines and rings, plus an optional explicit 3D box (e.g. a wall centre line extruded by `base` + `height`, grown by `thickness`).

**Class.** A dotted name in a taxonomy rooted at the abstract `Object`: `Sewer.Pipe.Lateral` → `Sewer.Pipe` → `Sewer` → `Object`.
* The parent is the dotted prefix unless `parent` is given. Missing ancestors are created as abstract classes.
* **Traits** add roles across domains. `Gas.Pipe` has the trait `Util.Pipe`, so "no pipe may touch a pipe of another system" is a single rule. `Bldg.Wall` has `Bldg.OpeningHost` and `Bldg.Support`.
* `IsA` follows parents and traits. Any pack may extend any class: definitions merge.

**Relation.** A named edge type with an optional inverse label. A relation can be:
* `symmetric` (stored once, traversed both ways);
* `transitive` (a hint for traces);
* `defaultDeny` (only granted class pairs are legitimate);
* `persist: false` (spatial facts such as `crosses`, `nearParallel` and `duplicates`, which stay in reports and graph exports but are not written into iModels).

**Edge.** `from -relation-> to` with:
* `Status`: asserted (from the source), inferred (relate) or derived (derive);
* `Confidence`: for inferred edges, the rule posterior × the two class confidences; for derived edges, the product along the best path;
* `Rule`: the rule that created it. A second derivation of the same edge merges into one edge (noisy-OR, both rules recorded);
* `Evidence`: measured distance, tolerance, contact end, the evidence terms that fired, and the runner-up candidate;
* `Ambiguous`: set when a single-slot assignment had a close runner-up.

**Finding.** A validation result: rule, kind, severity (error / warning / info), status, subject, object or edge, and message.

| Status | Meaning | Severity |
|---|---|---|
| `violated` | the data breaks the rule | the rule's |
| `unresolved` | open world: the counterpart may be elsewhere. Three cases: none of the required class exists in this container at all; the subject is within `boundaryMargin` of the data's edge; or the rule says `absent: unresolved` | info |
| `implicit` | drafting convention omits the counterpart (`absent: implicit`), e.g. a hydrant drawn without its lead | info |
| `unusual` | a soft expectation (`expect`) does not hold for this object | info by default |
| `anomaly` | across a container, an expectation holds far less often than its prior (binomial z < −3, at least 5 objects): likely misclassification, missing data or an unusual design | warning |

## 5. Rule language

Rule packs are JSON files (`*.rules.json`). Comments (`//`, `/* */`) and trailing commas are allowed, so packs can be annotated. Units are metres. Rules run in strata:
1. classify;
2. compute;
3. relate (ordered by `stage`, then load order);
4. derive (ordered the same way);
5. require/expect, forbid, check, acyclic and default-deny, in that order.

Rule ids are unique across packs. `RuleSet.Seal()` rejects a pack that names an unknown class, relation or trait. Loading any pack also loads `core`.

```jsonc
{
  "dgnkit-rules": 1, "name": "gas", "version": "1.0.0",
  "minClassConfidence": 0.5,                          // optional, threshold for assigning a class
  "classes":   { "Gas.Pipe.Main": { "traits": ["Util.Pipe"], "bis": "…", "ifc": "…", "description": "…" } },
  "relations": { "tapsInto": { "inverse": "tappedBy" }, "connectsTo": { "symmetric": true, "defaultDeny": true } },
  "classify": [ … ], "compute": [ … ], "relate": [ … ], "derive": [ … ],
  "require": [ … ], "expect": [ … ], "forbid": [ … ], "check": [ … ], "acyclic": [ … ], "allow": [ … ],
  "queries": [ … ]
}
```

### 5.1 Expressions

Expressions are side-effect-free and never throw at run time: an error just makes the rule not fire.
* **Values:** numbers, strings, booleans, null, lists, objects.
* **Operators:** `?:`, `or`/`||`, `and`/`&&`, `not`/`!`, `==` `!=` `<` `<=` `>` `>=`, `~` / `!~` (case-insensitive regex), `in`, `+ - * / %`, members (`a.b`, `a["name"]`), calls, list literals.
* **Null semantics:** missing members are null; comparisons with null are false (except `==`/`!=`); arithmetic with null gives null.
* **Number parsing:** numeric strings compare as numbers (`"300 mm"` → 300).

**Members of an object**, in lookup order (property names are case-insensitive):
1. pseudo-members: `key class domain layer layers cell text kind source container group length size` (plan diagonal) `width depth height zmin zmax x y z area startZ endZ confidence degree isLinear isPoint isAreal`;
2. values set by `compute` rules;
3. source properties;
4. context (`sheetTitle`, `model`, `file`, `units` …).

**Built-in functions:**

| Group | Functions |
|---|---|
| Text | `lower upper trim str number len/length` |
| Arithmetic | `abs sqrt pow round min max` |
| Control and null handling | `exists/has coalesce if` |
| Patterns and lists | `matches capture(text, regex, group) contains startsWith endsWith anyMatch(list, regex)` |
| Objects | `isa(obj, 'Class') prop(obj, 'name')` |

**Graph functions** (engine-backed):

| Function | Returns |
|---|---|
| `peers(obj?, 'Class')` | objects of that class in the same container |
| `peersWhere(obj?, 'Class', 'condition')` | the same, filtered; inside the condition the peer's members are bare and `self` is the subject |
| `degree(obj, 'relation', 'Class'?)` | number of edges of that relation |
| `hasEdge(obj, 'relation', 'Class'?)` | whether there is such an edge |
| `linked(a, b)` | any edge between a and b |
| `common(a, b, 'Class'?)` | shared neighbours (e.g. a shared manhole) |

**Scopes:**
* **Node rules** (classify, compute, gates, node checks) see the object's members bare (`layer ~ '^C-SSWR'`), plus `self`.
* **Pair rules** (relate `where`/`evidence`/`tolerance`/`reverseWhen`, edge checks, forbid) see:
  * `subject`, `object`, `edge`;
  * test variables: `distance`, `tolerance`, `centerDistance`, `angle` (acute, degrees), and `contactEnd` / `contactZ` / `otherEndZ` / `objectEnd` for endpoint tests, `dz` / `overlap` for above/below, `offset` for along;
  * on edges, the edge's evidence.
* **Messages** interpolate expressions: `"lateral {key} connects to {count} sewers"` (`count`, `min`, `max` are available in cardinality messages).

### 5.2 Rule kinds

| Kind | Fields | Semantics |
|---|---|---|
| `classify` | `class`, `when`, `confidence`, `veto` | Proposals per class are combined by noisy-OR. Evidence for a subclass also supports its ancestors. The engine then descends the primary taxonomy, taking at each level the child branch with the most support while it stays at or above `minClassConfidence`; the deepest concrete class on that path wins. A sign cell whose components sit on a mount level therefore stays a sign. A source-asserted `class` (GeoJSON) counts as confidence 1. `veto` removes a class and its subclasses. |
| `compute` | `for`, `set: {name: expr}` | Derived values (sign code and panel size, slope, upstream end), readable as members afterwards |
| `relate` | see below | Inference of `subject -relation-> object` |
| `derive` | `relation`, `subject`, `object`, `path`, `through`, `maxDepth` | Best-product path search. Path steps are `rel`, `^rel` (inverse), `rel+` and `rel*`. Intermediate nodes must satisfy `through`. Creates derived edges with the path in the evidence. |
| `require` / `expect` | `subject`, `relation`, `object?`, `direction` (out/in/both), `min`/`max`/`exactly`, `when`, `severity`, `absent` (error/unresolved/implicit), `boundaryMargin`, `minConfidence`, `probability` (expect), `message` | Closed-world counting with the open-world statuses above. `expect` reports `unusual` per object plus model-level `anomaly`. |
| `forbid` | `subject`, `relation`, `object`, `when` | Every matching edge is a violation |
| `check` | `subject`, `relation?`, `object?`, `assert`, `when` | Node-level or edge-level assertion. A null result (missing data) counts as *unknown*, not violated. |
| `acyclic` | `relation`, `subject` | Tarjan strongly connected components; each cycle is one finding |
| `allow` | `subject`, `relation`, `object` | Permits a pair under a `defaultDeny` relation. Granting relate rules permit their own pair; detector rules set `grants: false`. Edges already reported by a `forbid` are not reported again. |
| `queries` | `name`, `relations` (`rel`, `^rel` walk backwards, `~rel` either way), `direction`, `through`, `stopAt` (with `start` = the origin) | Named traces for the CLI and API |

**`relate` in detail.**

* **Tests.** Each sets the pair variables listed in §5.1.

  | Test | Matches when |
  |---|---|
  | `near` | geometry distance ≤ tolerance |
  | `endpointNear` | an end of the subject is near the object |
  | `endpointsMeet` | an end of the subject meets an end of the object |
  | `intersects` | the geometries touch or overlap |
  | `crosses` | proper plan crossing (needs no tolerance) |
  | `within` / `contains` | an anchor is inside the other's ring or box |
  | `above` / `below` | vertical gap within `maxGap`, plan overlap ≥ `minOverlap` |
  | `overlaps` | boxes intersect |
  | `along` | the subject's anchor is within tolerance of the object line |
  | `sameGroup` | same graphic group |
  | `attribute` | hash join on `subjectKey` / `objectKey` expressions (e.g. GIS foreign keys) |

* **Tolerance.**
  * `tolerance` is a number (metres) or an expression over subject/object (for example `coalesce(object.thickness, 0.3)/2 + 0.05`, which then needs `searchRadius`).
  * `toleranceFactor` scales with the subject's plan size, for sheet symbols drawn at symbol scale.
* **Candidates.**
  * Window query on the R-tree of the object class, capped at `MaxCandidatesPerSubject`, nearest first.
  * Objects in a different `Frame` are never candidates.
  * `skipIfRelated` (default true) skips subjects that already have the relation to the object class. Given a class name, it skips subjects related to any object of that class (fallback rules).
* **Score.**
  * `L = logit(prior) + Σ weights of the evidence that holds − distanceWeight·(d/tol)²`, then `p = σ(L)`.
  * `where` is a hard veto. Hard constraints are vetoes, not large weights.
* **Assignment.**
  * Candidates are sorted by p (ties broken by distance, then index) and accepted greedily while `p ≥ threshold` (default 0.5) and the per-subject limit (`cardinality`: `one`, `many` or n) and per-object limit (`objectCardinality`) allow.
  * A single-slot assignment is `ambiguous` when the runner-up is within `ambiguityMargin` logits (default 1) — unless the runner-up is an exact twin of the winner (the same symbol in the same place), which is an equivalent answer.
* **Orientation.** With `reverseWhen` the edge is stored object → subject. Flow direction, for example: a pipe flows into the structure at its downstream end, and the structure flows into the pipe at its upstream end. The upstream end comes from `upstreamEnd`, else the higher end of a 3D line, else the digitising direction.
* **Confidence** = p × class confidence of the subject × class confidence of the object.

### 5.3 Built-in packs

201 rules in total: 125 classes, 32 relations, 5 named queries.

| Pack | Content | Precedents |
|---|---|---|
| `core` | Relation vocabulary; `Util.*` roles; `Site.Premise`; upstream end and slope for every link; cross-system contact detector and prohibition; default-deny detector for unexplained same-system pipe contacts; duplicate-symbol QA; queries `downstream`, `upstream`, `feeds`, `network`, `supply` | IFC ports (same SystemType), Esri default deny |
| `buildings` | Wall, window, door, opening, roof, slab, column, beam, foundation (IFC names, NCS `A-`/`S-` layers). Windows and doors hosted by exactly one host (vertical containment as a veto); skylights in roofs; cover over walls (expect 85%); roofs and slabs on supports; columns on footings; wall joins | IFC 4.3 RelVoids/RelFills/RelConnectsPathElements, CityGML 3.0 |
| `storm` | Pipes structure to structure with flow direction; inlet outlets; feedsPipe; drains to an outfall (open world); adverse slope; Manning self-cleansing slope; access spacing; outlet below inlets; size not decreasing; acyclic | HEC-22 §6.2.7, Table 6-1, §7.2.4, §7.1.5 |
| `sewer` | Mains manhole to manhole; laterals ("feeders") flow into exactly one main or manhole (smaller, roughly square); laterals serve premises; force mains; drains to a lift station or plant; minimum 200 mm; minimum slopes; spacing; outlet below inlets; manhole 1.22 m; acyclic | Ten States Standards (wastewater 2014) §§33–34, 48 |
| `water` | Services tap one main and are metered; meters serve one premise; hydrants via a lead (or the nearest main within 15 m); fire mains ≥ 150 mm; leads ≥ 150 mm; 3.05 m separation from sewers; derived main feeds premise | Ten States Standards (water works 2018) §§8.1–8.8 |
| `gas` | Service lines tap one main or a branched service and end at a customer meter; meters serve one premise (address evidence); derived **main feeds premise** through services and meters; excess flow valve after 2017-04-14; a main supplies several services (expect) | 49 CFR 192.3, 192.383, 192.385 |
| `electric` | Spans structure to structure; transformers and luminaires attached to one structure; conduits between pull points; no orphan pull boxes; meters serve one premise; conduit run length | Esri structural attachment, NEC / TxDOT guidance |
| `roadway` | Signs supported (gated by drawing convention); signal heads on supports; guardrail end terminals; markings on pavement; curbs beside pavement; channelizing devices along lines | MUTCD §2A, §4D, Part 6; IFC 4.3; FHWA |
| `wsdot-workzone` | WSDOT TC sheet symbology: sign cells (MUTCD codes, sizes), portable mounts, channelizers, TMAs (vehicle + attenuator in one symbol), PCMS, arrow boards, flaggers | measured on the corpus |
| `lrs-gis` | Mileposts along their route: same RouteID, ARM in the half-open interval [BARM, EARM), same direction (i/d); ARM consistency; stream crossings of state routes need a culvert or bridge (open world) | BIS LinearReferencing, WSDOT LRS |

## 6. Inputs

**DGN** (`DgnFeatureExtractor`). One feature per top-level element.
* **Geometry** comes from the display list, so cells, shared cells and references arrive instanced and in world coordinates. It is converted to metres (`MetersPerMaster`).
* **Key** = the IMX key `el:<id>`. Elements from references get `ref:<path>/el:<id>`.
* **Layer:**
  * cell headers usually sit on `Default` / `Level 0`, so the primary layer is the most used component level, and `layers` lists all of them;
  * `headerLevel` keeps the header's own level.
* **Kind** is the element type (`Cell`, `SharedCell`, `LineString` …).
* **Cell** is the cell name.
* **Text** concatenates the texts in the element.
* **Properties:** EC instances flattened, first value wins, with `ecClass`, `ecClasses` and `ecSchema`; tags, with `tagSet`; database linkages; `stringLinkage`.
* **Context:** `sheetTitle`, `planReference`, `sheetNumber` … from the model's title-block tags; also `file`, `model`, `units`, `is3d`.
* **Frame** = file|model.

**GeoJSON** (`GeoJsonFeatures`). Coordinates are metres in a projected or local frame; project lon/lat first. All GeoJSON layers share one frame, so layers of one project relate to each other.
* `id` gives the key; `class` asserts the class; `layer`, `cell` and `text` are naming signals.
* 3D extents:
  * `zmin`/`zmax` (or `base` + `height`), and `thickness` for wall centre lines;
  * a 2D footprint is lifted to `zmin`.
* `relations: [{relation, to, …}]` gives asserted edges.
* Every other property becomes an attribute (`diameter`, `invertUp`, `invertDown`, `upstreamEnd`, `installed`, `address` …).

## 7. Outputs

| Output | How | Content |
|---|---|---|
| `*.logic.json` | `LogicWriters.WriteJson` (`--props` adds attributes, `--all` adds unclassified objects) | classified nodes (class, confidence, rule, candidates, derived values, box), edges with evidence, findings, per-rule statistics, warnings |
| Markdown | `WriteMarkdown` | classes, relationship table (inferred / derived / asserted / ambiguous / mean confidence), rule table (prior and observed rate), findings by rule |
| GraphML | `WriteGraphMl` | classified nodes plus every edge end, with class, layer and position; edge relation, confidence, status, rule |
| Neo4j CSV | `WriteNeo4jCsv` | `nodes.csv` (`key:ID`, labels = class + ancestors + traits + `Object`) and `relationships.csv` (`:TYPE` = `FLOWS_INTO` …), for `neo4j-admin database import` |
| Cypher | `WriteCypher` | uniqueness constraint + batched `UNWIND … MERGE` |
| IMX / iModel | `LogicImx.ToEnrichment` → `DgnImxOptions.Enrichment` | see §8 |

## 8. iModel write-back

`dgnkit imodel <file.dgn> --logic [rules] [--bim out.bim]` runs the logic layer and writes its results into the same IMX stream (spec: [IMODEL-INTERCHANGE.md](IMODEL-INTERCHANGE.md)).

**What gets written:**
* **Classifications.** Each classified element gets a `Logic / Classification` property group: `Class`, `Domain`, `Confidence`, `Rule`, `BisClass`, `IfcClass`. It becomes a `DgnKitDynamic:Logic_Classification` aspect. Derived values (e.g. the sign `code` and `panel`) become `Logic_Derived`.
* **Relationships.**
  * Each persisted relation becomes a `relationshipClass` record. The connector turns it into an `ECRelationshipClass` in the dynamic schema **`DgnKitLogic`**: base `bis:ElementRefersToElements` (link table, shared columns), referencing strength, polymorphic `bis:Element` ends.
  * Properties: `Confidence`, `Rule`, `Status`, `Evidence` (JSON) and `Ambiguous`, plus a `Checksum`.
  * Each edge becomes a `relationship` record (`HostedBy`, `FlowsInto`, `SupportedBy` …) with the two element keys. Edges below confidence 0.5 and non-persistent relations are not written.
  * Edges whose ends are not elements of the stream (references) are dropped and counted.
* **Change detection.**
  * Relationships have no ExternalSourceAspect, so the connector identifies them by (class, source, target) — one row per triple, the link table's own rule — and compares `Checksum`.
  * On a re-run against a standalone iModel, unchanged relationships are skipped, changed ones updated, and ones no longer inferred deleted. Only relationships whose source element belongs to the job are considered.

**Verified.** `SR24 & SR240 PAVER_PS_TC.dgn`:
* 149 relationships inserted; `verify.js` confirms all stored by class, and geometry still matches;
* re-run: 149 unchanged; with the `AlignedWith` class removed: 113 deleted, 36 unchanged; restored: 113 inserted;
* 298 classification and 37 derived aspects.

ECSQL can then trace the graph inside the iModel, e.g.
`SELECT SourceECInstanceId, TargetECInstanceId, Confidence FROM DgnKitLogic.SupportedBy`.

## 9. CLI and library

```bash
dotnet run --project src/DgnKit.Cli -- logic testdata/logic/utilities.geojson --report utilities.md --graphml utilities.graphml --neo4j neo4j/
```

```bash
dotnet run --project src/DgnKit.Cli -- logic query testdata/logic/utilities.geojson --from GAS-M2 --query feeds
```

```bash
dotnet run --project src/DgnKit.Cli -- logic scan testdata/corpus --csv logic.csv --report logic.md
```

```bash
dotnet run --project src/DgnKit.Cli -- logic rules --rules rules/gas.rules.json
```

```bash
dotnet run --project src/DgnKit.Cli -- imodel sheet.dgn --logic --bim sheet.bim
```

Options:
* `--rules` (repeatable): file, directory, `builtin` or `builtin:<pack>`. Default: all built-in packs.
* `--model`, `--no-refs`, `-o`, `--report`, `--graphml`, `--cypher`, `--neo4j`.
* The exit code is 4 when there are errors.

```csharp
var rules = RuleSet.Builtin();                                    // or RuleSet.FromPaths(new[] { "my-rules/", "builtin:gas" })
var features = DgnFeatureExtractor.Extract(DgnFile.Open("sheet.dgn"));
features.AddRange(GeoJsonFeatures.ReadFile("premises.geojson"));
var result = LogicEngine.Run(features, rules);
foreach (var f in result.Findings.Where(f => f.Severity == Severity.Error)) Console.WriteLine(f.Message);
var downstream = GraphQuery.Run(result, "downstream", result.Graph.Find("el:1234")!);
var bareWalls = GraphQuery.Without(result.Graph, "Bldg.Wall", "hostedBy", Direction.In);   // walls hosting no window
```

## 10. Verification

`dotnet test --filter LogicTests`: 22 tests. The whole suite (89 tests) passes.

* **Building blocks.**
  * Parser, expression semantics (null handling, regex, functions), taxonomy descent;
  * the R-tree against brute force on 5,000 boxes;
  * exact predicates;
  * determinism (byte-identical JSON across runs);
  * GraphML / Neo4j / Cypher / Markdown output well-formed;
  * IMX ordering: elements before relationships, and every relationship end resolves to an element key.
* **`testdata/logic/building.geojson`** — a 10 × 8 m room, a screen wall, a canopy and columns. Exactly the planted defects are found:
  * window off the walls, and window above the wall top (both errors);
  * unsupported canopy, and column without a footing (errors);
  * roofless screen wall (*unusual*, info — "usually, but not always").

  The skylight is hosted by the roof. One window is classified from its `A-GLAZ` layer and the door from `ifcClass`.
* **`testdata/logic/utilities.geojson`** — a street with sanitary, gas, water, storm and electric networks. The 14 planted errors and 4 warnings are found, and nothing else:
  * a lateral 5 m short of its main;
  * an undersized main without an upstream manhole;
  * an uphill pipe;
  * a flow cycle;
  * a missing EFV;
  * gas → water and storm → sewer cross-connections;
  * an undersized fire main;
  * a blind storm junction (default deny);
  * an outlet above its inlet;
  * a flat pipe;
  * a water main 2 m from a sewer;
  * a dangling span.

  Correct inferences:
  * feeders → mains;
  * a branched gas service;
  * `GAS-M2 feeds {P-3, P-4, P-6, P-7}`;
  * a symbol-only hydrant → nearest main;
  * `downstream(LAT-1)` = SAN-1 → MH-2 → SAN-2 → MH-3 → SAN-3 → LS-1;
  * `supply(P-6)` = meter → branch → service → main;
  * the gas network trace stops at the cross-connection.
* **Corpus** ([`testdata/reports/logic-report.md`](../testdata/reports/logic-report.md)): 108 files and 141,313 objects in about 8 s; 16,728 classified (most of the corpus is title blocks, text and detail linework).

  | Rule | Result |
  |---|---|
  | Sign → support, where the drawing draws supports | **120 of 131 (91.6%)** — the corpus study's baseline was 81% |
  | Milepost → route, where the route data covers the ARM | **3,164 of 3,164**, all with ARM inside their segment, 0 ambiguous |
  | Stream crossings of state routes | 38, all *unresolved* (no culvert or bridge data in these files) |
  | Guardrail terminals, orphan pull boxes | *unresolved*: nothing of the counterpart class is in those drawings |
  | Duplicate symbols | **210 found** (copy-pasted cells exactly on top of each other) — checked against the DGN: distinct element ids, same model, same origin |

  How the rules improved during verification:
  * **Ambiguity pointed at domain knowledge.** Route lines exist once per direction (i/d), and milepost ARMs sit exactly on segment boundaries. Direction agreement and half-open intervals took ambiguity from 5,443 to 0.
  * **A mount centred on the sign is its own,** not the neighbour's. The `centerDistance` evidence term now captures that.
  * **WSDOT draws a truck-mounted attenuator and its vehicle as one symbol,** so they are not two objects to pair.
  * **Two-leg sign stands** still show as ambiguous ties: either leg is a correct support.

## 11. Limitations and next steps

* **Calibration.** Priors and weights are engineering estimates; the catalogue's `p` values have not been fitted. Next: hand-label sign/mount and milepost pairs and fit the weights by logistic regression (an offline tool, not a runtime dependency).
* **Coverage.** The corpus has no utilities or buildings, so those packs are proven only on synthetic data. Next: a public GIS utility dataset (GeoJSON adapter) and IFC via an IFC → feature adapter.
* **Elevations.** Most 2D plan rules (inverts, mounting heights, clearances) need attributes. Without them they are *unknown*, not violated.
* **Not implemented:**
  * run-based spacing (valves every 244 m, manholes at every grade change): spacing is checked per pipe;
  * terminals and ports;
  * spatial containment (storeys);
  * persistent and incremental graphs (SQLite);
  * tiling for 10⁷ features;
  * a Cypher parser;
  * SHACL export;
  * relationships across reference files (IMX does not export references yet).
* **BIS.** Relationships use dynamic ERE subclasses on generic elements. Re-basing onto released domain classes (StormSewerPhysical, PipeworkPhysical port connections, NetworkTopology) is the Tier-2 step in [IMODEL.md §5](IMODEL.md).
