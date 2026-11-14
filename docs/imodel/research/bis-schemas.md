# BIS (Base Infrastructure Schemas): a source-verified brief for modelling DGN and other sources in iModels

Prepared 2026-11-13. The schema XML is treated as ground truth and the docs come second. Where the two disagree, or the docs are aspirational, this brief says so.

---

## 0. Scope, snapshot and citation keys

### 0.1 Snapshot of what was read

- **github.com/iTwin/bis-schemas**, branch `master`, last push 2026-11-13T17:52Z. The git tree lists 993 files, of which 598 are `*.ecschema.xml` (435 of those are under `Released/`). All work-in-progress (WIP) schema XML, `*.remarks.md`, `README.md`, `SchemaInventory.json` and docs were downloaded and parsed. Key released versions were fetched separately for comparison.
- **BisCore**: the WIP file is `01.00.28` and is tagged `ProductionStatus=NotForProduction`. The latest released version is `01.00.27`: `Production`, dated 11/2/2026 in the inventory [R-INV]. The set of entity and relationship class names is **identical** in 01.00.27 and the 01.00.28 WIP (diffed by script), so every BisCore class below is in released 01.00.27. Compared with 01.00.16, about 45 classes and relationships were added, listed in §2.13.
- **Docs**: `github.com/iTwin/itwinjs-core` `docs/bis/**`, which is the source of https://www.itwinjs.org/bis/ , plus `docs/learning/**` for connector guidance. Published URLs follow the pattern `docs/bis/guide/x/y.md` → `https://www.itwinjs.org/bis/guide/x/y/`. Those URLs were derived from that convention and not opened one by one.
- **Convention in every WIP file:** all non-released schema files in the repo carry `ProductionStatus=NotForProduction`. Released copies carry `Production`, `FieldTesting` or `Deprecated` (checked for about 30 schemas, §1.10).

### 0.2 Citation keys

**bis-schemas repo** (paths are relative to the repo root):

| Key | Path |
|---|---|
| R-README | `README.md` |
| R-INV | `SchemaInventory.json` |
| BC | `Domains/0-Core/BisCore.ecschema.xml` (WIP 01.00.28; same classes as released 01.00.27) |
| BC-27 | `Domains/0-Core/Released/BisCore.01.00.27.ecschema.xml` |
| BC-R | `Domains/0-Core/BisCore.remarks.md` |
| GEN / GEN-MD | `Domains/0-Core/Generic.ecschema.xml` / `Generic.ecschema.md` |
| FUNC / FUNC-R | `Domains/0-Core/Functional.ecschema.xml` / `.remarks.md` |
| ANLYT / ANLYT-R | `Domains/0-Core/Analytical.ecschema.xml` / `.remarks.md` |
| ASSET / ASSET-R | `Domains/0-Core/Asset.ecschema.xml` / `.remarks.md` |
| PMAT | `Domains/0-Core/PhysicalMaterial.ecschema.xml` |
| BCV | `Domains/0-Core/BisCoreViews.ecschema.xml` (+ remarks) |
| MARKUP | `Domains/0-Core/Markup.ecschema.xml` |
| BISCA / BISCA-R | `System/BisCustomAttributes.ecschema.xml` / `.remarks.md` |
| CORECA / CORECA-R | `System/CoreCustomAttributes.ecschema.xml` / `.remarks.md` |
| ECDBMAP | `System/ECDb/Released/ECDbMap.02.00.04.ecschema.xml` (+ `System/ECDb/ECDbMap.remarks.md`) |
| V2V3 | `System/ECv3ConversionAttributes.ecschema.xml` |
| LR / LR-R | `Domains/1-Common/LinearReferencing/LinearReferencing.ecschema.xml` / `.remarks.md` |
| SPCOMP | `Domains/1-Common/SpatialComposition/SpatialComposition.ecschema.xml` |
| CLSF / CLSF-R | `Domains/1-Common/ClassificationSystems/...ecschema.xml` / `.remarks.md` |
| GRIDS | `Domains/1-Common/Grids/Grids.ecschema.xml` |
| RRA / RRA-R | `Domains/2-DisciplinePhysical/Civil/RoadRailAlignment/RoadRailAlignment.ecschema.xml` / `.remarks.md` |
| RRP, RDP, RLP | `Domains/2-DisciplinePhysical/Civil/RoadRailPhysical/{RoadRailPhysical,RoadPhysical,RailPhysical}.ecschema.xml` |
| CVP / CVP-R | `Domains/2-DisciplinePhysical/Civil/CivilPhysical/CivilPhysical.ecschema.xml` / `.remarks.md` |
| CVSP, RDSP / RDSP-R, BRSP | `Domains/2-DisciplinePhysical/Civil/SpatialComposition/{CivilSpatial,RoadSpatial,BridgeSpatial}/...` |
| BRP | `Domains/2-DisciplinePhysical/Civil/BridgePhysical/BridgePhysical.ecschema.xml` |
| BSP | `Domains/2-DisciplinePhysical/Civil/BridgeStructuralPhysical/BridgeStructuralPhysical.ecschema.xml` |
| SP | `Domains/2-DisciplinePhysical/StructuralPhysical/StructuralPhysical.ecschema.xml` |
| BLDSP, ARCH, BLDPHYS | `Domains/2-DisciplinePhysical/Building/{BuildingSpatial,ArchitecturalPhysical,BuildingPhysical}/...` |
| TRRN / TRRN-R, EW | `Domains/2-DisciplinePhysical/{Terrain,Earthwork}/...` |
| CONN / CONN-R | `Domains/4-Application/Connectors/Connector.ecschema.xml` / `.remarks.md` |
| DGNC / DGNC-R | `Domains/4-Application/Connectors/DgnConnector/DgnConnector.ecschema.xml` / `.remarks.md` |
| ORD | `Domains/4-Application/Connectors/CivilInfrastructureFramework/Deprecated/DgnV8OpenRoadsDesigner/DgnV8OpenRoadsDesigner.ecschema.xml` (+ `Released/`) |
| CIF | `Domains/4-Application/Connectors/CivilInfrastructureFramework/{CifCommon,CifRoads,CifRail}.ecschema.xml` (+ `.remarks.md`) |
| NWD-R | `Domains/4-Application/Connectors/Navisworks/NwdStatic.remarks.md` |
| R-RELPROC | `docs/schema-release-process.md` |

**itwinjs-core docs.** Each path is under `docs/bis/` and is published at `https://www.itwinjs.org/bis/<path-without-.md>/`.

| Key | Path |
|---|---|
| D-ORG | `guide/intro/bis-organization.md` |
| D-OVR | `guide/intro/overview.md` |
| D-FAB | `guide/intro/fabric-of-the-universe.md` |
| D-MWB | `guide/intro/modeling-with-bis.md` |
| D-EL | `guide/fundamentals/element-fundamentals.md` |
| D-MOD | `guide/fundamentals/model-fundamentals.md` |
| D-CODE | `guide/fundamentals/codes.md` |
| D-FED | `guide/fundamentals/federationGuids.md` |
| D-CAT | `guide/fundamentals/categories.md` |
| D-ASP | `guide/fundamentals/elementaspect-fundamentals.md` |
| D-TYPE | `guide/fundamentals/type-definitions.md` |
| D-CLASS | `guide/fundamentals/data-classification.md` |
| D-REL | `guide/fundamentals/relationship-fundamentals.md` |
| D-MIX | `guide/fundamentals/mixins.md` |
| D-PROP | `guide/fundamentals/properties-guidelines.md` |
| D-DOM | `guide/fundamentals/schemas-domains.md` |
| D-TOW | `guide/data-organization/top-of-the-world.md` |
| D-IH | `guide/data-organization/information-hierarchy.md` |
| D-MP | `guide/data-organization/modeling-perspectives.md` |
| D-SRPP | `guide/data-organization/srpp.md` |
| D-OME | `guide/data-organization/organizing-models-and-elements.md` |
| D-ODE | `guide/data-organization/organizing-definition-elements.md` |
| D-SC | `guide/data-organization/spatial-composition.md` |
| D-SYS | `guide/data-organization/modeling-systems.md` |
| D-3D | `guide/physical-perspective/3d-guidance.md` |
| D-PHYS | `guide/physical-perspective/physical-models-and-elements.md` |
| D-PHIER | `guide/physical-perspective/physical-hierarchy-organization.md` |
| D-INFO | `guide/other-perspectives/information-models-and-elements.md` |
| D-FUNC | `guide/other-perspectives/functional-models-and-elements.md` |
| D-ANLYT | `guide/other-perspectives/analysis-models-and-elements.md` |
| D-VER | `guide/schema-evolution/schema-versioning-and-generations.md` |
| D-CUST | `guide/schema-evolution/schema-customization.md` |
| D-SDG | `guide/schema-evolution/schema-design-guidelines.md` |
| D-PS | `guide/schema-evolution/schema-production-status.md` |
| D-EVO | `guide/schema-evolution/data-evolution-across-time.md` |
| D-NAME | `guide/naming-guidelines/rules-and-recommendations.md` |
| D-VAL | `guide/other-topics/bis-schema-validation.md` |
| D-ECS | `ec/ec-schema.md` |
| D-PROV | `domains/Provenance-in-BIS.md` |
| D-DS | `domains/drawings-sheets.md` |
| D-DOMIDX | `domains/{index,core-domains,common-domains,discipline-physical-domains,...}.md` |

**itwinjs-core learning docs and code.** Learning docs are under `docs/learning/` and published at `https://www.itwinjs.org/learning/...`.

| Key | Path |
|---|---|
| L-CONN | `docs/learning/imodel-connectors.md` |
| L-WAC | `docs/learning/WriteAConnector.md` |
| L-DYN | `docs/learning/ImplementADynamicSchema.md` |
| L-CH | `docs/learning/backend/Channel.md` |
| C-CODE | `core/common/src/Code.ts` (`BisCodeSpec`, `CodeScopeSpec`, `Code.createEmpty`) |
| C-IMODEL | `core/common/src/IModel.ts` (well-known IDs) |
| X-PRES | `full-stack-tests/presentation/src/learning-snippets/customization/InstanceLabelOverride.test.ts` |
| X-SNAP | `full-stack-tests/presentation/src/backend/PresentationManager.test.snap` |
| X-DTA | `test-apps/display-test-app/src/frontend/IModelDisplayReferencePicker.ts` |
| X-SMART | `example-code/snippets/src/extensions/SmartDeviceDecorator.ts` |
| X-ECSQL | `docs/learning/ECSqlReference/InstanceQuery.md` |

**Other public code.**

| Key | Location |
|---|---|
| X-CK | github.com/ColinKerr/superqueryspatialisticaspecteledocious `aspect-queries.md` (a tutorial by a BIS maintainer that uses the Bentley "Stadium" sample iModel) |
| X-PCF | github.com/iTwin/pcf `core/src/DynamicSchema.ts` |
| X-NATIVE | github.com/iTwin/imodel-native `iModelCore/ECDb/Tests/Performance/PerformanceSchemaImportTests.cpp` |

### 0.3 Findings that may contradict common assumptions

1. **LinearReferencing is a Common-layer schema, not Core.** It lives in `Domains/1-Common/LinearReferencing/` and its `SchemaLayerInfo` is `Common` [LR]. The Core folder contains BisCore, Generic, Functional, Analytical, Asset, PhysicalMaterial, BisCoreViews and Markup. CoreCustomAttributes, BisCustomAttributes, Units, Formats and the ECDb* schemas are under `System/` [R-INV]. ECDbMap is not a BIS domain schema.
2. **"Generation" in the docs is not what the version numbers show.** The docs say every schema in a generation shares the first version digit [D-VER]. In practice read versions differ: BisCore is 01.xx, LinearReferencing 02.00.03, RoadRailAlignment 02.00.04, RoadRailPhysical 03.00.00, BridgeStructuralPhysical 02.00.01, IntegratedStructuralModel 02 (WIP 03), PowerSystemResourcesPhysical (WIP 02.00.00) [R-INV]. Treat the first digit as a per-schema read-compatibility version.
3. **Several civil schemas are released only as `FieldTesting`.** An iModel whose production status is `Production` rejects them [D-PS]. Affected: RoadRailAlignment **02.00.04** (02.00.03 was `Production`), CivilPhysical 01.00.03, CivilSpatial 01.00.04, RoadSpatial 01.00.00, ClassificationSystems 01.00.04, CivilUnits 01.00.00. RoadRailUnits 01.00.05 is **Deprecated** (superseded by CivilUnits). See §1.10.
4. **Some domains have never been released**, so no version is approved for use with data: BridgePhysical, BridgeSpatial, RoadAttributions, RoadNetwork, RailNetwork, GeotechnicalExploration, Rebar, Asset, Markup, BisCoreViews and others [R-INV].
5. **`SpatialLocationPartition` is now discouraged.** BisCore remarks say: *"Our current recommendation is to not instantiate a SpatialLocationPartition"*. Spatial locations should be organised under the physical backbone instead [BC-R]. 3D guidance agrees ("iModel authors should avoid usage of SpatialLocationPartition") [D-3D]. RoadRailAlignment remarks still describe Alignments in a `SpatialLocationModel` that sub-models a `SpatialLocationPartition` [RRA-R].
6. **`PhysicalModel` must not be subclassed** (validation rule BIS-607) [D-VAL][D-PHIER]. `StructuralPhysicalModel` and `BuildingPhysicalModel` exist only as legacy [SP][BLDPHYS].
7. **Bentley's own civil connector schemas are not the "aligned" BIS schemas.** `DgnV8OpenRoadsDesigner` is Deprecated [ORD]. CifCommon, CifRoads and CifRail are Application-layer aspect schemas whose remarks say *"should not be considered the aligned BIS schema"* [CIF].
8. **`Generic.ecschema.md` is stale.** The generated page says version 1.0.0; the XML is 01.00.06 WIP and 01.00.05 released [GEN-MD][GEN].
9. **The SpatialComposition classes `ZonalSystem`, `ZonalSystemGroupsMembers` and `SpatialOrganizerOrganizesSpatialElements` are WIP only.** They are not in released 01.00.01 (diffed) [SPCOMP].

---

## 1. Layering, schema rules, versioning, generations and dynamic schemas

### 1.1 The five layers

`BisCustomAttributes` defines the enumeration `SchemaLayer` and the custom attribute (CA) `SchemaLayerInfo` (applies to: Schema). The enumerator descriptions are verbatim from the XML [BISCA]:

| Layer | Definition (schema XML) | Examples (remarks and docs) |
|---|---|---|
| `Core` | "schemas that define the most fundamental concepts and key organizational strategies for all other BIS schemas" | BisCore, Analytical, Functional, plus Generic, PhysicalMaterial [BISCA-R][D-DOM] |
| `Common` | "abstract concepts and patterns used by multiple disciplines" | ClassificationSystems, NetworkTopology, SpatialComposition [BISCA-R]; grid lines, distribution systems, linear referencing [D-ORG] |
| `DisciplinePhysical` | "physical/spatial and closely associated concepts, in light of a specific discipline" | Earthwork, RoadSpatial, StructuralPhysical [BISCA-R] |
| `DisciplineOther` | "concepts from modeling perspectives other than physical, in light of a specific discipline" | StructuralAnalysis, IoTDeviceFunctional, GeologicalModel [BISCA-R] |
| `Application` | "concepts that no other schema would need or want to reference" | Static connector schemas (BuildingDataGroupBase, CifRoads) and **"all dynamically-generated iModel Connector schemas, such as IFCDynamic and RevitDynamic"**; also PresentationRules, PointCloud, ScalableMesh [BISCA-R] |

**Reference rule.** A schema may reference schemas at the same layer or any lower layer: *"A schema at a particular layer can only reference schemas at the same layer or lower."* [BISCA-R]. The docs add: *"Schemas in any layer may refer to (be dependent upon) schemas in the same layer or in any lower layer… intended to avoid circular dependencies"* [D-ORG][D-DOM]. EC itself forbids circular references [D-ECS].

**Every schema must declare its layer.** *"A BIS schema shall advertise its author's intended layer for it by applying the `SchemaLayerInfo` custom-attribute"* [D-ORG]. The remarks put it as "BIS schemas should be tagged…" [BISCA-R]. Older released schemas often lack the CA: RoadRailPhysical 03.00.00, LinearReferencing 02.00.03, Terrain and Earthwork 01.00.00 have none (§1.10).

**Other layer guidance:**
- Everything descends from BisCore, which lets software understand any BIS schema at a basic level [D-ORG].
- iTwin.js Core understands Core and Common [D-ORG].
- Discipline-Physical also hosts discipline-specific specialisations of SpatialComposition, such as Building, Road and Bridge [D-DOM].
- The Application layer is for schemas whose data no other application needs; product and connector schemas are the examples [D-ORG][D-DOM].

**Repo folder layout mirrors the layers:** `Domains/0-Core`, `1-Common`, `2-DisciplinePhysical`, `3-DisciplineOther`, `4-Application`, plus `System/` [R-README]. Released copies go in `…/Released/<Name>.RR.WW.mm.ecschema.xml` [R-README].

**Compatibility grades A–D** [D-ORG]:
- **A**: true BIS schemas carefully designed for editing and interoperability.
- **B**: legacy consensus schemas (such as ISM) intelligently converted to BIS, or new schemas meant for one-way conversion and not for native editing.
- **C**: legacy schemas with software-discoverable semantics, intelligently converted.
- **D**: legacy schemas with little or no software-discoverable semantics, usually auto-converted, that follow only basic BIS rules.

### 1.2 Schema identity: names, aliases and version format

- **Version.** The format is `RR.WW.mm` (Read.Write.Minor) [D-ECS]. Validation rule BIS-004 requires three parts, each zero-padded to two digits (e.g. `01.02.00`); BIS-005 applies the same to schema references [D-VAL]. A reference resolves by *"Latest compatible match where the read and write versions must match and the minor version of the located schema must be equal to or greater"* [D-ECS].
- **Name and alias registration.** *"All EcSchema/BIS Domain Names must be registered with BIS workgroup to avoid conflicts. This includes the aliases."* Aliases *"must be in lowercase"* and *"must be less than 7 characters long"* [D-NAME]. BIS-009 requires the alias in a schema reference to equal the alias the schema itself defines [D-VAL].
  - **Real-world violations exist** in Bentley's own schemas (from the parsed headers). Do not use these as precedent:
    - not lowercase: `AECU`, `ArchPhys`, `BldgPhys`, `GeoExp`, `SPR`, `physMat`, `bisViews`, `pipeNetAnlyt`;
    - 7 or more characters: `rralign`, `spatial`, `dgnconn`, `connector`, `geomodel`, `stmswrphys`, `wtrdistphys`.
- **General naming rules** [D-NAME]:
  - Alphanumeric only. **No underscores.** Must not start with a digit.
  - PascalCase and US spelling.
  - Avoid product, organisation and version names in names (e.g. avoid `Dgn`, `OpenPlant`, `V8iFile`).
  - Aspect class names should end in `Aspect`.
  - Relationship names follow Source-Verb-Target. `Owns`, `Contains` and `Aggregates` imply embedding; `Represents` and `Groups` imply referencing. Embedding relationships must not have `Has` in the name (also BIS-1505 [D-VAL]).
  - Dynamic connector schemas registered in the inventory break the no-underscore rule anyway, e.g. `DwgAttributeDefinitions_block` and `roads_shp_<sha1>` [R-INV].
- **Validation rules relevant to schema authors** [D-VAL]:
  - BIS-006: a schema whose name contains "dynamic" must apply `CoreCA:DynamicSchema`.
  - BIS-101: only BisCore, Functional and Generic may use `bis:ClassHasHandler`.
  - BIS-600: every entity class must derive from the BIS hierarchy.
  - BIS-601: only one base entity class (mixins are additional).
  - BIS-606: no class may implement both `IParentElement` and `ISubModeledElement`.
  - BIS-607: `PhysicalModel`, `SpatialLocationModel`, `GroupInformationModel`, `InformationRecordModel`, `DefinitionModel` (except DictionaryModel and RepositoryModel), `DocumentListModel` and `LinkModel` cannot be subclassed.
  - BIS-609: Model subclasses cannot add properties outside BisCore.
  - BIS-1001: KindOfQuantity persistence units must be SI.
  - BIS-1300: avoid `long` properties; use navigation properties for foreign keys.
  - BIS-1302: allowed extended types are only `BeGuid`, `GeometryStream`, `Json` and `URI`.
  - BIS-1500: no "holding" strength.
  - BIS-1504: relationships to aspects are only allowed via `ElementOwnsUniqueAspect` or `ElementOwnsMultiAspects`.
  - BIS-604 and BIS-605: every aspect class must be the target of a relationship derived from those two. These are only **warnings** in schemas carrying `DynamicSchema`.
- **iModel physical-mapping guards** declared on BisCore: `NoAdditionalRootEntityClasses`, `NoAdditionalForeignKeyConstraints` (with one exception, `Functional:FunctionalElement.TypeDefinition`) and `NoAdditionalLinkTables` (all from ECDbSchemaPolicies). There is also `ImportRequiresVersion ECDbRuntimeVersion=4.0.0.5` [BC]. In effect, domain schemas cannot add new root tables, foreign keys or link tables. BisCore remarks: *"Other schemas cannot alter the underlying database schema mapping without explicit permission from BisCore"* [BC-R].
- **ECDbMap CAs** used by BisCore: `SchemaMap`, `ClassMap`, `JoinedTablePerDirectSubclass`, `ShareColumns`, `DbIndexList`, `PropertyMap`, `ForeignKeyConstraint`, `LinkTableRelationshipMap`, `ImportRequiresVersion`, `UseRequiresVersion`, `QueryView`, `ForeignKeyView` [ECDBMAP].

### 1.3 Production status

The `CoreCustomAttributes:ProductionStatus` CA has a `SupportedUse` property [CORECA][CORECA-R][D-PS]:

- `Production`: data will be supported long-term.
- `FieldTesting`: data "may not be supported long-term and may not be upgradable".
- `NotForProduction`: "should never be used for production workflows".
- `Deprecated`: replaced by better alternatives; the `Deprecated` CA should also be applied.

**Load-time check.** The iModel stores its own status in `be_prop` (namespace `dgn_Db`, name `ProductionStatus`) and checks it against each schema when the schema loads [D-PS]:
- A `Production` iModel accepts {Production, Deprecated}.
- A `FieldTesting` iModel accepts {Production, FieldTesting, Deprecated}.
- A `NotForProduction` iModel accepts all four.

The README release flow copies the WIP file to `Released/` and adds an inventory entry with `verified`, `approved`, `sha1` and `dynamic` (Yes/No). Schemas are auto-published as npm packages; pre-releases get `-dev.x` versions [R-README][R-RELPROC].

### 1.4 Minor and major changes, and the middle (write) version

Source for all of this subsection: [D-VER].

**Minor upgrades may only:**
- add optional classes, a class in the middle of a hierarchy, optional properties or KindOfQuantities;
- add or delete PropertyCategories;
- loosen constraints (Sealed→None; make a constraint polymorphic, lower the minimum or raise the maximum cardinality);
- make presentation-only changes (labels, descriptions, presentation units and formats).

**Major changes include:** removing or renaming a class or property; changing a base class (*"currently any change in parent class is a major schema upgrade"*); changing a property type; changing relationship constraints or cardinality.

**Write-version (middle digit) changes** are for updates that break writers but not readers. Examples:
- adding a NOT NULL or UNIQUE constraint on a new property;
- adding a new navigation property with a foreign key.

Some changes are forbidden outright because they would alter database mapping: adding NOT NULL or UNIQUE to an existing property, adding a new unique index, or adding a foreign key to an existing navigation property.

**What an app can do when the repository's schema is newer (or the same):**
- first digit matches → it can read;
- first two digits match → it can write;
- no digits match → it can do nothing.

**When the repository's schema is older:**
- first two digits match → the app can upgrade without breaking others;
- only the first digit matches → upgrading blocks some older apps from writing;
- no digits match → nothing.

### 1.5 Generations

Source: [D-VER].

- *"A BIS Generation consists of a series of releases during which major schema upgrades are not allowed for any Domain."*
- The write version may change within a generation only as an *"emergency outlet"*, and OCTO review is recommended.
- *"Repositories can be updated from one Generation to the next Generation without loss of data. It is the individual applications that can only work with repositories from a single Generation."*
- Intended numbering is `Generation.Write.Minor`, "where all the *Generation* numbers in a BIS repository match". In practice this is not the case (§0.3 item 2).
- **Moving between generations** needs mapping code: 1:N, N:1 or N:N instance and property mapping, moving properties (for example from a Type to an Element), and rearranging the model hierarchy. The doc's TODO admits there is no default strategy yet for converting dynamic schemas and their data [D-CUST].
- Remarks note names that "cannot be changed in this generation", e.g. `ModelModelsElement` should have been `ModelSubModelsElement` [BC-R].

### 1.6 Dynamic schemas

**What the CA means.** `CoreCA:DynamicSchema` (applies to: Schema): *"Identifies a schema as dynamically generated by an application. Like versions of a schema may differ and are not guaranteed to merge without conflicts."* [CORECA]. iModelHub uses it to detect dynamic schemas, and they "require special handling since their name and version are typically duplicated between iModels from different work sets" [L-CONN][L-WAC].

**When connectors use them.** Connectors *"may dynamically create application-specific schemas whose classes descend from the most appropriate BIS domain classes"* [L-CONN]. *"In effect, each native source file has its unique schema"* when the source allows user-defined classes or properties [L-WAC].

**Evolving them across runs.** *"The Connectors add to the previously-defined schemas (creating new schema versions)"* and follow the versioning rules [L-CONN][L-WAC]. *"Classes and properties cannot be removed or significantly redefined. In general, only additions to schemas are allowed."* [D-CUST]. The pcf reference implementation compares the newly generated schema with the existing one and bumps only the **minor** version when it differs [X-PCF].

**Design guidance.** *"Only make use of dynamic schemas (generated at runtime) to capture concepts that truly vary per iModel. If a given connector has some connector-specific fixed concepts, capture them in an Application-layer schema and have any dynamic classes subclass those Application-layer classes."* [D-SDG]. All dynamic connector schemas sit at the Application layer [BISCA-R].

**Risk from BIS upgrades.** A minor BIS upgrade can add a property with the same name as one in a dynamic subclass. The doc literally leaves the consequence blank: "This class will cause ????????????? …" [D-CUST]. Same-named *classes* are not a technical problem because schema names scope them, but they can confuse users [D-CUST].

**Validation relaxations.** BIS-006 (naming) applies; BIS-604/605 become warnings [D-VAL].

**Registered dynamic schemas in the inventory** (entries with `"dynamic": "Yes"`, usually without a path, registered only by sha1) [R-INV]:
- `roads_shp_902C0D77F2EF39CD947A7F96650E81A2019AD59F` (a shapefile-derived schema);
- `DwgAttributeDefinitions_block`;
- `RevitDynamic`;
- `SP3DReview` 03.08.04;
- `BuildingDataGroupWS`;
- plant `ProcessFunctional` and `ProcessPhysical` (released copies flagged dynamic);
- the five Substation `*Custom` schemas.

### 1.7 Schema customisation strategy (official)

- *"Properties that vary per class or type should be modeled using property definitions, properties which vary per instance should be modeled using Json."* [D-CUST]. This sits in tension with the later properties guide, which prefers Aspects for non-essential per-instance properties (§6.1). Treat [D-PROP] as the more specific, newer guidance.
- **Vendor catalogs:** a vendor defines Element and Type subclasses (`BigCo:MonsterPump`, `BigCo:MonsterPumpType`) and ships Type instances [D-CUST].
- **Data imported via connectors:** *"It is rare that a BIS schema will work 'out of the box'. Typically a dynamic schema will need to be defined by the converter… (likely along with some instances of standard BIS classes)."* [D-CUST].

### 1.8 Inventory snapshot for schemas relevant to this brief

Columns: latest released version (status of that release), current WIP version, layer. From [R-INV]; release statuses were read from each released XML.

| Schema (alias) | Latest released (status) | WIP | Layer |
|---|---|---|---|
| BisCore (bis) | 01.00.27 (Production, 11/2/2026) | 01.00.28 | Core |
| Generic (generic) | 01.00.05 (Production) | 01.00.06 | Core |
| Functional (func) | 01.00.04 (Production) | 01.00.05 | Core |
| Analytical (anlyt) | 01.00.01 (Production) | 01.00.02 | Core |
| PhysicalMaterial (physMat) | 01.00.02 (Production) | 01.00.03 | Core |
| Asset (asset), BisCoreViews (bisViews), Markup | never released | 01.00.00 | Core |
| LinearReferencing (lr) | 02.00.03 (Production, 8/31/2021) | 02.00.04 | Common |
| SpatialComposition (spcomp) | 01.00.01 (Production, 8/14/2020) | 01.00.02 | Common |
| ClassificationSystems (clsf) | 01.00.04 (**FieldTesting**) | 01.00.05 | Common |
| Grids (grids) | no file in `Released/`. The inventory lists 01.00.00 with no path and the comment "consider it to be unapproved". | `Grids.ecschema.xml` is 02.00.00; `legacy/Grids.01.00.01` is the inventory's WIP | Common |
| NetworkTopology, DistributionSystems, Profiles, DocumentMetadata, AecUnits | released (various) | … | Common |
| RoadRailAlignment (rralign) | 02.00.04 (**FieldTesting**, 10/23/2025); 02.00.03 is Production | 02.00.05 | DisciplinePhysical |
| RoadRailPhysical (rrphys) | 03.00.00 (Production, 01/28/2020) | 03.00.01 | DisciplinePhysical |
| RoadPhysical (rdphys) / RailPhysical (rlphys) | 01.00.00 (Production, 01/28/2020) | 01.00.01 | DisciplinePhysical |
| CivilPhysical (cvphys) | 01.00.03 (**FieldTesting**, 6/12/2026) | 01.00.04 | DisciplinePhysical |
| CivilSpatial (cvsp) | 01.00.04 (**FieldTesting**, 6/11/2026) | 01.00.05 | DisciplinePhysical |
| RoadSpatial (rdsp) | 01.00.00 (**FieldTesting**, 1/29/2025) | 01.00.01 | DisciplinePhysical |
| BridgeSpatial (brsp), BridgePhysical (brphys), RoadAttributions (rdatt) | **never released** | 01.00.00 | DisciplinePhysical |
| BridgeStructuralPhysical (bphys) | 02.00.01 (Production, 9/16/2022) | 02.00.02 | DisciplinePhysical |
| StructuralPhysical (sp) | 01.00.01 (Production, 6/17/2022) | 01.00.02 | DisciplinePhysical |
| Terrain (trrn) | 01.00.00 (Production, 9/15/2021) | 01.00.01 | DisciplinePhysical |
| Earthwork (ew) | 01.00.00 (Production, 3/1/2022) | 01.00.01 | DisciplinePhysical |
| StormSewerPhysical / WaterDistributionPhysical | 01.00.02 / 01.00.01 (10/23/2025) | 01.00.03 / 01.00.02 | DisciplinePhysical |
| CivilUnits (cvu) | 01.00.00 (**FieldTesting**, 10/23/2025) | 01.00.01 | DisciplinePhysical |
| RoadRailUnits (rru) | 01.00.05 (**Deprecated**) | 01.00.06 | DisciplinePhysical |
| BuildingSpatial (spatial) | 01.00.01 (no ProductionStatus CA in the released file) | 01.00.02 | DisciplinePhysical |
| ArchitecturalPhysical (ArchPhys) | 01.00.03 (Production) | 01.00.04 | DisciplinePhysical |
| RoadNetwork / RailNetwork | **never released** | 01.00.00 | DisciplineOther |
| Connector (connector) | 01.01.00 (NotForProduction; carries DynamicSchema CA) | 01.01.00 | Application |
| DgnConnector (dgnconn) | 01.00.00 (Production, 10/22/2026) | 01.00.01 | Application |
| DgnV8OpenRoadsDesigner (orddgn) | 02.00.01 (Production when released; WIP 02.00.02 marked **Deprecated**) | — | (no layer CA) |
| CifCommon / CifRoads / CifRail | 01.00.16 / 01.00.04 / 01.00.12 (Production) | +1 | Application |

---

## 2. BisCore in full

### 2.1 Schema header

`schemaName="BisCore" alias="bis"`. References: CoreCustomAttributes 01.00.03, BisCustomAttributes 01.00.00, ECDbMap 02.00.02, ECDbSchemaPolicies 01.00.00. Description: *"The BIS core schema contains classes that all other domain schemas extend."* [BC].

**Schema-level CAs** [BC]:
- `SchemaLayerInfo=Core`;
- `SchemaHasBehavior` (a JavaScript library provides behaviour);
- the ECDb policies listed in §1.2.

**Class-level behaviour CAs** [BC][BC-R]:
- `ClassHasHandler`: a C++ handler supplies behaviour. Restricted to core schemas; other schemas use TypeScript domain handlers plus `SchemaHasBehavior`.
- `CustomHandledProperty` / `AutoHandledProperty`.
- `ReservedPropertyNames`. For example, Element reserves `Id`, `ClassName`, `ClassFullName` and `Code`; GeometricElement reserves `Geom`; GeometricElement2d/3d reserve `Placement`.

"Fabric of the universe": all information is Elements, ElementAspects, Models and relationships. *"BIS domain schemas (other than BisCore) can only define classes that (directly or indirectly) subclass classes defined in the BisCore domain"* [D-FAB].

### 2.2 `bis:Element`: the properties every element has

`bis:Element` is **Abstract**. Its CAs include `ClassMap(TablePerHierarchy)` and `NotSubclassableInReferencingSchemas`, so other schemas must subclass the core subclasses rather than Element itself [BC].

| Property | Type / mapping | Meaning |
|---|---|---|
| `Model` | nav → `ModelContainsElements` (backward), read-only, FK | the single containing Model |
| `CodeSpec` | nav → `CodeSpecSpecifiesCode`, FK | which CodeSpec governs the code |
| `CodeScope` | nav → `ElementScopesCode`, FK | element that scopes code uniqueness |
| `CodeValue` | string, nullable, collation NoCase | the code text |
| `UserLabel` | string, nullable, NoCase, non-unique, indexed | friendly name |
| `Parent` | nav → `ElementOwnsChildElements` (backward), FK | owning parent element |
| `FederationGuid` | binary, nullable, **unique** | GUID federating the element across repositories |
| `JsonProperties` | string (hidden) | ad hoc JSON |
| `LastMod` | dateTime UTC, read-only | maintained by the framework |

All rows are from [BC].

**Indexes:**
- **Unique** index `ix_bis_Element_Code` on (CodeSpec.Id, CodeScope.Id, CodeValue);
- unique `ix_bis_Element_FederationGuid` where not null;
- non-unique `ix_bis_Element_UserLabel` where not null [BC].

**Handler-enforced behaviour:** Element needs a valid Model and a valid or *empty* Code. *"If the Parent property is not NULL… the child Element be in the same Model as the parent Element."* [BC-R].

### 2.3 Element class tree (BisCore with Core-layer subclasses)

Legend: **[A]** abstract, **[C]** concrete (modifier None), **[S]** sealed; `+X` = mixin; key properties in `{}`. Generated from the XML of BisCore, Generic, Functional and Analytical [BC][GEN][FUNC][ANLYT].

```
bis:Element [A] {Model, CodeSpec, CodeScope, CodeValue, UserLabel, Parent, FederationGuid, JsonProperties, LastMod}
├─ bis:GeometricElement [A]                          (JoinedTablePerDirectSubclass; reserves 'Geom')
│  ├─ bis:GeometricElement2d [A] {Category→DrawingCategory, Origin(pt2d), Rotation, BBoxLow/High(pt2d), GeometryStream, TypeDefinition→GeometricElement2dHasTypeDefinition}
│  │  └─ bis:GraphicalElement2d [A]
│  │     ├─ bis:AnnotationElement2d [C]
│  │     │  ├─ bis:TextAnnotation2d [C] +ITextAnnotation {TextAnnotationData(json string), DefaultTextStyle}
│  │     │  ├─ bis:TableAnnotation2d [C] +ITextAnnotation {TableData(json)}
│  │     │  └─ bis:DimensionAnnotation2d [A] {DimensionData(json), DimensionStyle→AnnotationDimensionStyle}
│  │     │     ├─ bis:LinearDimensionAnnotation2d [C]   ├─ bis:AngularDimensionAnnotation2d [C]
│  │     │     ├─ bis:RadialDimensionAnnotation2d [C]   └─ bis:OrdinateDimensionAnnotation2d [C]
│  │     ├─ bis:DrawingGraphic [C]
│  │     │  └─ bis:SheetBorder [C] DEPRECATED
│  │     ├─ bis:ViewAttachment [C] {View→ViewDefinition}
│  │     └─ generic:DetailingSymbol [A]
│  │        ├─ generic:TitleText [S]
│  │        ├─ generic:ViewAttachmentLabel [S] {ViewAttachment, ClipGeometry}
│  │        └─ generic:Callout [A] {DrawingModel}
│  │           └─ generic:{SectionCallout, ElevationCallout, PlanCallout, DetailCallout} [S]
│  └─ bis:GeometricElement3d [A] {Category→SpatialCategory, InSpatialIndex, Origin(pt3d), Yaw, Pitch, Roll, BBoxLow/High(pt3d), GeometryStream, TypeDefinition→GeometricElement3dHasTypeDefinition}
│     ├─ bis:GraphicalElement3d [A]                       ("conveys information within 3D graphical presentations")
│     │  ├─ bis:TextAnnotation3d [C] +ITextAnnotation
│     │  └─ generic:Graphic3d [S]
│     ├─ bis:SpatialElement [A]                           ("occupies real world space")
│     │  ├─ bis:PhysicalElement [A] {PhysicalMaterial→PhysicalElementIsOfPhysicalMaterial}   ("has mass, can be 'touched'")
│     │  │  ├─ generic:PhysicalObject [S]
│     │  │  └─ bis:PhysicalPortion [A] +ISubModeledElement DEPRECATED
│     │  └─ bis:SpatialLocationElement [A]                ("'tracked' real world location… no mass")
│     │     ├─ generic:SpatialLocation [S]
│     │     ├─ bis:VolumeElement [C]
│     │     ├─ bis:LightLocation [S] {Enabled}
│     │     ├─ bis:SectionDrawingLocation [C] {SectionView→ViewDefinition}
│     │     ├─ bis:SectionLocation [C] DEPRECATED
│     │     └─ bis:SpatialLocationPortion [A] +ISubModeledElement DEPRECATED
│     └─ anlyt:AnalyticalElement [A]                      (in Analytical schema)
├─ bis:InformationContentElement [A]                 ("Only the core framework should directly subclass")
│  ├─ bis:DefinitionElement [A] {IsPrivate}
│  │  ├─ bis:Category [A] +IParentElement {Description, Rank}
│  │  │  ├─ bis:SpatialCategory [S]
│  │  │  └─ bis:DrawingCategory [S]
│  │  ├─ bis:SubCategory [S] {Description, Properties(display json), Rank}
│  │  ├─ bis:GeometryPart [C] {GeometryStream, BBoxLow, BBoxHigh}
│  │  ├─ bis:TypeDefinitionElement [A] {Recipe→TypeDefinitionHasRecipe}
│  │  │  ├─ bis:PhysicalType [A] {PhysicalMaterial}          └─ generic:PhysicalType [S]
│  │  │  ├─ bis:SpatialLocationType [A]                       └─ generic:SpatialLocationType [S]
│  │  │  ├─ bis:GraphicalType2d [A]                           └─ generic:GraphicalType2d [S]
│  │  │  ├─ func:FunctionalType [A]
│  │  │  └─ anlyt:AnalyticalType [A]
│  │  ├─ bis:RecipeDefinitionElement [A]
│  │  │  ├─ bis:TemplateRecipe2d [C] +ISubModeledElement
│  │  │  ├─ bis:TemplateRecipe3d [C] +ISubModeledElement
│  │  │  └─ bis:TemplateRecipe3dInPlan [C] +ISubModeledElement
│  │  ├─ bis:DefinitionSet [A] {Rank}
│  │  │  ├─ bis:DefinitionContainer [C] +ISubModeledElement   ("folder of definitions")
│  │  │  └─ bis:DefinitionGroup [C]
│  │  ├─ bis:PhysicalMaterial [A] {Density, RenderMaterial}  └─ generic:PhysicalMaterial [S]; physMat:{Aggregate,Aluminum,Asphalt,Concrete,Steel} [S]
│  │  ├─ bis:RenderMaterial [S] +IParentElement {PaletteName, Description}
│  │  ├─ bis:Texture [S] {Data, Format, Width, Height}
│  │  ├─ bis:LineStyle [S] {Description, Data}
│  │  ├─ bis:ColorBook [S]
│  │  ├─ bis:AnnotationTextStyle [S] {Settings}  ├─ bis:AnnotationFrameStyle [S]  ├─ bis:AnnotationLeaderStyle [S]
│  │  ├─ bis:AnnotationDimensionStyle [S] {TextStyle, Settings}  ├─ bis:TextAnnotationSeed [S]
│  │  ├─ bis:FormatSet [S] {Settings}
│  │  ├─ bis:DefinitionThumbnail [C] {Image, ImageFormat}
│  │  ├─ bis:SheetPrototype [C] +ISubModeledElement {Scale, Height, Width, Border}
│  │  ├─ bis:SheetBorderPrototype [C] +ISubModeledElement {Height, Width}
│  │  ├─ bis:ModelSelector [C]   ├─ bis:CategorySelector [C]
│  │  ├─ bis:DisplayStyle [A] → DisplayStyle2d [C], DisplayStyle3d [C]
│  │  ├─ bis:ViewDefinition [A] {Description, CategorySelector, DisplayStyle}
│  │  │  ├─ bis:ViewDefinition3d [A] {Origin, Extents, Yaw/Pitch/Roll, camera…}
│  │  │  │  ├─ bis:SpatialViewDefinition [C] {ModelSelector} └─ bis:OrthographicViewDefinition [C]
│  │  │  │  └─ bis:TemplateViewDefinition3d [C]
│  │  │  └─ bis:ViewDefinition2d [A] {BaseModel, Origin, Extents, RotationAngle}
│  │  │     └─ bis:DrawingViewDefinition [C], bis:SheetViewDefinition [C], bis:TemplateViewDefinition2d [C]
│  │  └─ bis:AuxCoordSystem [A] → AuxCoordSystem2d [C], AuxCoordSystem3d [C] → AuxCoordSystemSpatial [C]
│  ├─ bis:InformationPartitionElement [A] +ISubModeledElement {Description}
│  │  ├─ bis:PhysicalPartition [S]           ├─ bis:SpatialLocationPartition [S]
│  │  ├─ bis:DefinitionPartition [S]         ├─ bis:DocumentPartition [S]
│  │  ├─ bis:GroupInformationPartition [S]   ├─ bis:InformationRecordPartition [S]
│  │  ├─ bis:LinkPartition [S]               ├─ bis:GraphicalPartition3d [S]
│  │  ├─ bis:PhysicalSystemPartition [S]     ├─ bis:SheetIndexPartition [S]
│  │  ├─ func:FunctionalPartition [C] ("should be considered 'abstract'")
│  │  └─ anlyt:AnalyticalPartition [A]
│  ├─ bis:InformationReferenceElement [A]
│  │  ├─ bis:Subject [S] +IParentElement {Description}
│  │  ├─ bis:GroupInformationElement [A]
│  │  │  ├─ generic:Group [S]
│  │  │  └─ bis:PhysicalSystem [A] {AggregatingSystem}      (spcomp:ZonalSystem [C], WIP only)
│  │  ├─ bis:LinkElement [A]
│  │  │  ├─ bis:UrlLink [C] {Url, Description}
│  │  │  │  ├─ bis:RepositoryLink [C] +ISubModeledElement {RepositoryGuid, Format}
│  │  │  │  ├─ bis:SynchronizationConfigLink [C] {LastSuccessfulRun (deprecated)}
│  │  │  │  └─ bis:FolderLink [C]
│  │  │  └─ bis:EmbeddedFileLink [C] {Name, Description}
│  │  ├─ bis:ExternalSource [C] +IParentElement {Repository→RepositoryLink, ConnectorName, ConnectorVersion}
│  │  │  └─ bis:ExternalSourceGroup [C]
│  │  ├─ bis:ExternalSourceAttachment [C] {Attaches, Role(SpecifyContext|SpecifyPart), Translation, Yaw, Pitch, Roll, Scale}
│  │  ├─ bis:SheetIndex [C] +IParentElement
│  │  └─ bis:SheetIndexEntry [A] {EntryPriority} → SheetIndexFolder [C] +IParentElement, SheetReference [C], SheetIndexReference [C]
│  ├─ bis:InformationRecordElement [A]     ("the default choice if no other subclass… makes sense")
│  │  ├─ bis:ProjectInformationRecord [S] {ProjectName, ProjectNumber, Location}
│  │  └─ bis:RenderTimeline [C] {Description, Script}
│  ├─ bis:Document [A]
│  │  ├─ bis:Drawing [C] +ISubModeledElement {ScaleFactor}  └─ bis:SectionDrawing [C] {SectionType, SpatialView}
│  │  ├─ bis:Sheet [C] +ISubModeledElement {Scale, Height, Width, SheetPrototype, SheetTemplate(deprecated)}
│  │  ├─ generic:Document [S]
│  │  └─ bis:SheetTemplate [C] DEPRECATED, bis:SheetBorderTemplate [C] DEPRECATED
│  └─ bis:DriverBundleElement [A]
├─ bis:RoleElement [A]           (NotSubclassableInReferencingSchemas except Functional:FunctionalElement, Asset:AssetElement)
│  ├─ func:FunctionalElement [A] {TypeDefinition→FunctionalElementIsOfType}
│  │  ├─ func:FunctionalBreakdownElement [A] +IParentElement  └─ func:FunctionalComposite [C] DEPRECATED
│  │  └─ func:FunctionalComponentElement [A]                   └─ func:FunctionalPortion [C] +ISubModeledElement DEPRECATED
│  └─ asset:AssetElement [A]     (Asset schema, never released)
└─ bis:InformationCarrierElement [A] DEPRECATED → bis:DocumentCarrier [A] DEPRECATED

Other roots in BisCore:  bis:CodeSpec [S] {Name (unique, NoCase), JsonProperties} · bis:SpatialIndex [S] (maps dgn_SpatialIndex) ·
                         mixins bis:IParentElement, bis:ISubModeledElement, bis:ITextAnnotation (all [A], IsMixin)
```

**Notes from the docs on this hierarchy:**
- The three core Element branches are GeometricElement, InformationContentElement and RoleElement [D-EL].
- A steel section shape is a DefinitionElement, not a GeometricElement: "merely having a geometric property is not necessarily enough" [D-EL].
- `GraphicalElement3d` instances are *"typically positioned for whitespace reasons since they are not tied to real-world 3d space"* [D-EL].

### 2.4 Model hierarchy, permitted contents and sub-modelling

```
bis:Model [A] {ParentModel(nav, ModelOwnsSubModel), ModeledElement(nav, ModelModelsElement), IsPrivate, IsTemplate, JsonProperties, LastMod}
├─ bis:GeometricModel [A] {GeometryGuid}
│  ├─ bis:GeometricModel3d [A] {IsNotSpatiallyLocated, IsPlanProjection}
│  │  ├─ bis:SpatialModel [A]   ("the only two types of model for real physical space")
│  │  │  ├─ bis:PhysicalModel [C]          (must not be subclassed, BIS-607)
│  │  │  ├─ bis:SpatialLocationModel [C]   (must not be subclassed, BIS-607)
│  │  │  └─ bis:WebMercatorModel [C] DEPRECATED
│  │  ├─ bis:GraphicalModel3d [A]  └─ generic:GraphicalModel3d [S]
│  │  └─ anlyt:AnalyticalModel [A]
│  └─ bis:GeometricModel2d [A] {GlobalOrigin(pt2d)}
│     └─ bis:GraphicalModel2d [A]
│        ├─ bis:DrawingModel [C]  └─ bis:SectionDrawingModel [C]
│        └─ bis:SheetModel [C]
├─ bis:InformationModel [A]
│  ├─ bis:DefinitionModel [C]  ├─ bis:DictionaryModel [S] (singleton)  └─ bis:RepositoryModel [S] (singleton)
│  ├─ bis:DocumentListModel [C]
│  ├─ bis:GroupInformationModel [A]  └─ generic:GroupModel [S]
│  ├─ bis:InformationRecordModel [C]
│  ├─ bis:LinkModel [C]
│  ├─ bis:PhysicalSystemModel [C]
│  └─ bis:SheetIndexModel [C]
└─ bis:RoleModel [C] ("should be considered abstract")  └─ func:FunctionalModel [C]
```

The tree is from [BC][GEN][FUNC][ANLYT]. **Model identity:** a Model's Id equals its modelled Element's Id, and its name is that element's CodeValue [D-MOD].

**What each model may contain.** Handler rules are from [BC-R]; the doc table is from [D-MOD].

| Model | Allowed contents | Sub-models (relationship) |
|---|---|---|
| RepositoryModel (Id 0x1) | Acts like an InformationModel: any InformationContentElement **except DefinitionElements**. Holds Subjects, partitions and the root Subject. | the root Subject (0x1), the only self-sub-modelling case [D-TOW] |
| DictionaryModel (Id 0x10) | DefinitionElements and other InformationContentElements | DefinitionPartition `"BisCore.DictionaryModel"` |
| DefinitionModel | DefinitionElements (which may live **only** in DefinitionModels) plus other InformationContentElements | DefinitionPartition (`DefinitionModelBreaksDownDefinitionPartition`) or DefinitionContainer (`…BreaksDownDefinitionContainer`) |
| PhysicalModel | PhysicalElements, SpatialLocationElements, GraphicalElement3d, InformationContentElements (not Definition) | PhysicalPartition; TemplateRecipe3d; any `ISubModeledElement` physical element (e.g. `rrphys:Corridor`, `bphys:Bridge`) |
| SpatialLocationModel | SpatialLocationElements, GraphicalElement3d, InformationContentElements. **PhysicalElements are refused.** | SpatialLocationPartition; TemplateRecipe3dInPlan; spatial-location ISubModeledElements (e.g. `rralign:DesignAlignments`) |
| GraphicalModel3d (concrete: generic:GraphicalModel3d) | GraphicalElement3d | GraphicalPartition3d |
| DrawingModel | GeometricElement2d. The modelled element must be a `Drawing` or `TemplateRecipe2d`; `SheetBorderPrototype` is also allowed via the relationship. | `DrawingModelBreaksDownDrawing` (target `Drawing` with multiplicity **1..1**) |
| SheetModel | GeometricElement2d (ViewAttachments, annotations) | Sheet (`SheetModelBreaksDownSheet`), SheetPrototype |
| DocumentListModel | **only** Document elements | DocumentPartition |
| GroupInformationModel (concrete: generic:GroupModel) | **only** GroupInformationElements | GroupInformationPartition |
| InformationRecordModel | **only** InformationRecordElements | InformationRecordPartition |
| LinkModel | **only** LinkElements | LinkPartition |
| PhysicalSystemModel | PhysicalSystem elements [D-MOD] | PhysicalSystemPartition |
| SheetIndexModel | SheetIndex and SheetIndexEntry | SheetIndexPartition |
| RoleModel / FunctionalModel | RoleElements (FunctionalElements) plus InformationContentElements. **GeometricElements are refused.** | FunctionalPartition subclass |
| AnalyticalModel subclasses | AnalyticalElements plus InformationContentElements, "never contain PhysicalElements" [D-ANLYT] | AnalyticalPartition subclass |
| any Model | — | `ModelBreaksDownRepositoryLink` (reality-data pattern, "not… a pattern that we wish to promote" [D-3D]) |

**Generic handler rules** [BC-R]:
- GeometricModel3d refuses GeometricElement2d, and GeometricModel2d refuses GeometricElement3d.
- GeometricElement2d/3d need a placement whenever `GeometryStream` is set.
- 2D elements must use a `DrawingCategory`; 3D elements must use a `SpatialCategory`.

`IsNotSpatiallyLocated=true` lets a non-Spatial GeometricModel3d opt out of real-world coordinates and the spatial index. SpatialModels are always in real-world coordinates [D-3D][BC].

### 2.5 Core relationships (strength, direction, multiplicity)

Format: Source(multiplicity) → Target(multiplicity); the default direction is forward. From [BC][FUNC][ANLYT][GEN].

**Containment and hierarchy:**
- `ModelContainsElements` [S] embedding: Model(1..1) → Element(0..*). Stored as `Element.Model`.
- `ModelModelsElement` [C] **embedding, backward**: Model(0..1) → ISubModeledElement(0..1). Stored as `Model.ModeledElement`. Its specialisations are the `…BreaksDown…` relationships in the table above.
- `ModelOwnsSubModel` [S] embedding: Model(0..1) → Model(0..*). A redundant "shortcut" index [BC].
- `ElementOwnsChildElements` [C] embedding: Element(0..1) → Element(0..*). Stored as `Element.Parent`. Remarks say it *"should be treated as abstract"* and that the source should logically have been `IParentElement` [BC-R]. Subclasses:
  - `SubjectOwnsSubjects` (non-polymorphic Subject→Subject);
  - `SubjectOwnsPartitionElements`;
  - `SubjectOwnsProjectInformationRecord` (Subject 1..1 → 0..1);
  - `CategoryOwnsSubCategories`;
  - `PhysicalElementAssemblesElements` (PhysicalElement → PhysicalElement);
  - `ElementEncapsulatesElements` (hidden children);
  - `RenderMaterialOwnsRenderMaterials`;
  - `ExternalSourceOwnsAttachments`;
  - `FolderContainsFolders` and `FolderContainsRepositories` (FolderLink → RepositoryLink, **1..***);
  - `SheetIndexOwnsEntries` and `SheetIndexFolderOwnsEntries`;
  - in other schemas: `lr:LocatedElementOwnsLocatingElements`, `rralign:AlignmentOwnsReferents`, `rrphys:CorridorPortionOwnsAlignments`, and others.
- `ElementOwnsUniqueAspect` [C] embedding: Element(1..1) → ElementUniqueAspect(0..*). Subclasses: `ElementOwnsChannelRootAspect`, `RepositoryLinkOwnsInfoAspect`, `SheetOwnsSheetInformationAspect`, and two deprecated TextAnnotationData relationships.
- `ElementOwnsMultiAspects` [C] embedding: Element(1..1) → ElementMultiAspect(0..*). Subclass: `ElementOwnsExternalSourceAspects`.

**Categories and types (all sealed or navigation-backed):**
- `GeometricElement3dIsInCategory` [S] referencing, backward: GeometricElement3d(0..*) → **SpatialCategory(1..1)**.
- `GeometricElement2dIsInCategory` [S]: GeometricElement2d(0..*) → **DrawingCategory(1..1)**.
- `GeometricElement3dHasTypeDefinition` [A] referencing: GeometricElement3d(0..*) → TypeDefinitionElement(0..1). Subclasses:
  - `PhysicalElementIsOfType` (→ PhysicalType);
  - `SpatialLocationIsOfType` (→ SpatialLocationType);
  - `anlyt:AnalyticalElementIsOfType`;
  - domain specialisations (e.g. `cvphys:CurbIsOfType`, `rralign:AlignmentIsOfType`).
- `GeometricElement2dHasTypeDefinition` [A]: GeometricElement2d → TypeDefinition(0..1). Subclass: `GraphicalElement2dIsOfType` (→ GraphicalType2d).
- `func:FunctionalElementIsOfType` [C]: no core base; it is navigation-backed and is the one foreign-key exception in BisCore's policy.
- `TypeDefinitionHasRecipe` [A] → `PhysicalTypeHasTemplateRecipe`, `SpatialLocationTypeHasTemplateRecipe`, `GraphicalType2dHasTemplateRecipe`.
- `PhysicalElementIsOfPhysicalMaterial` [S] and `PhysicalTypeIsOfPhysicalMaterial` [S]: target (0..1). The element-level material overrides the type's [BC].

**Codes:**
- `CodeSpecSpecifiesCode` [S] referencing: CodeSpec(1..1) → Element(0..*).
- `ElementScopesCode` [S] referencing: Element(1..1) → Element(0..*).

**Link-table relationships**, all under `ElementRefersToElements` [A]: referencing, Element(0..*) → Element(0..*), property `MemberPriority` (int), unique index on (Source, Target, ClassId, MemberPriority).
- `ElementGroupsMembers` [C] has these subclasses:
  - `GroupImpartsToMembers` ("properties of the group should be considered as properties of its members");
  - `DefinitionGroupGroupsDefinitions`;
  - `PhysicalSystemGroupsMembers` (→ SpatialElement);
  - `ExternalSourceGroupGroupsSources` [S];
  - `PhysicalTypeComposesSubTypes` [A].
- `ElementHasLinks` [C]: Element(**1..***) → LinkElement(0..*). Used to link partitions and models to RepositoryLinks [L-WAC].
- `ElementRefersToDocuments` [C] → Document.
- `DrawingGraphicRepresentsElement` [C] (DrawingGraphic → Element). Subclass: `func:DrawingGraphicRepresentsFunctionalElement`.
- `GraphicalElement3dRepresentsElement` [C], `SpatialLocationElementRepresentsElement` [C] and `SpatialLocationTypeRepresentsTypeDefinition` [C].
- `func:PhysicalElementFulfillsFunction` [C] (PhysicalElement → FunctionalElement) and `anlyt:AnalyticalSimulatesSpatialElement` [C] (→ SpatialElement).
- `PhysicalSystemServicesSpatialElements` [C], `SubjectRefersToSubject` [C], `CategorySelectorRefersToCategories` [S] and `DefinitionElementHasThumbnails` [C].
- `SynchronizationConfigProcessesSources` [C] (forward), with sealed subclass `SynchronizationConfigSpecifiesRootSources`.
- `PartitionOriginatesFromRepository` [S] is **DEPRECATED** ("Use ElementHasLinks").
- `PhysicalSystemAggregatesSubSystems` [C] is referencing, PhysicalSystem(0..1) → (0..*), but is a navigation property (`AggregatingSystem`), not a link table.
- `ElementDrivesElement` [C] is a separate link table with properties `Status` and `Priority`. Subclass: `ElementDrivesTextAnnotation`. Used for change propagation, with `DriverBundleElement` [BC-R].

**Provenance relationships** (navigation-backed):
- `ElementIsFromSource` [S] (ExternalSourceAspect → ExternalSource 0..1);
- `ElementScopesExternalSourceIdentifier` [S] (Element 1..1 → aspects);
- `ExternalSourceIsInRepository` [S] (ExternalSource → RepositoryLink 0..1);
- `ExternalSourceAttachmentAttachesSource` [C] (→ ExternalSource 1..1).

**Views:**
- `ViewDefinitionUsesCategorySelector` [S] (→1..1), `ViewDefinitionUsesDisplayStyle` [S] (→1..1), `SpatialViewDefinitionUsesModelSelector` [S] (→1..1);
- `ModelSelectorRefersToModels` [S] (link table, Model target);
- `BaseModelForView2d` [S] (GeometricModel2d 1..1 → ViewDefinition2d);
- `ViewIsAttached` [S] (ViewDefinition 1..1 → ViewAttachment 0..*).

**Rules about relationships** [D-REL]:
- All relationships except those that only back navigation properties must derive from a BisCore relationship.
- Subclasses may only *narrow*: equal or more specific constraints, equal or tighter multiplicity, polymorphic may go true→false only, and strength and direction must stay the same.
- Each relationship has one base class and one constraint class per end.
- The link table `bis_ElementRefersToElements` holds every (*..*) relationship and every relationship with properties. Models and Aspects can never take part in link-table relationships.
- Navigation properties are defined only on the base relationship. Unsealed navigation relationships cost two columns (Id plus RelECClassId).

### 2.6 ElementAspects

```
bis:ElementAspect [A] (NotSubclassableInReferencingSchemas; reserves Id/ClassName/ClassFullName)
├─ bis:ElementUniqueAspect [A] {Element(nav, ElementOwnsUniqueAspect)}  (TablePerHierarchy, shared columns)
│  ├─ bis:ChannelRootAspect [S] {Owner (channel key), Version}
│  ├─ bis:RepositoryInfoAspect [C] {OriginatingSystem}
│  ├─ bis:SheetInformationAspect [C] {DesignedBy, DesignedDate, DrawnBy, CheckedBy}
│  └─ bis:TextAnnotationData [S] DEPRECATED
└─ bis:ElementMultiAspect [A] {Element(nav, ElementOwnsMultiAspects)}
   ├─ bis:ExternalSourceAspect [C] (HiddenClass) {Scope(nav), Identifier, Kind, Version, Checksum, JsonProperties, Source(nav→ExternalSource)}
   └─ lr:LinearlyReferencedLocation [A] → LinearlyReferencedAtLocation [S], LinearlyReferencedFromToLocation [S]   (LinearReferencing)
```

Sources: [BC][LR].

**Rules:**
- An aspect is owned by exactly one Element and deleted with it. It can't be the target of any relationship except its owner's. It may point outward only through navigation properties [D-FAB][D-ASP][D-REL].
- `ElementAspect` *"is not expected to ever have more than the two subclasses"* [D-ASP].
- A unique aspect is identified by its class plus the owning Element; multi-aspects also have a private Id [D-ASP].
- **"Mandatory" aspects:** use a base aspect class as the key class, e.g. a ShapeAspect hierarchy. If the data is essential, raise the target multiplicity of the owning relationship (1..1 or 1..*) [D-ASP][D-PROP].
- Rule of thumb: *"ElementAspects are for data owned by one Element, while TypeDefinitionElements are for values meant to be reused across many Elements."* [D-ASP].

### 2.7 Mixins

**BisCore mixins** [BC][BC-R][D-EL][D-VAL]:
- `IParentElement`: the element may have children.
- `ISubModeledElement`: the element may be sub-modelled. Both apply only to `bis:Element`. They are **mutually exclusive**: *"two techniques for that simultaneously could result in confusion and double-counting"* (enforced by BIS-606).
- `ITextAnnotation`: applies to GeometricElement and carries `DefaultTextStyle`.
- No element can have children or a sub-model unless its class carries the corresponding mixin [D-EL].

**Rules for mixins in general** [D-MIX]:
- abstract entity class with the `IsMixin(AppliesToEntityClass)` CA;
- zero or one base class, which must itself be a mixin;
- may not override inherited properties;
- never listed first among a class's base classes;
- implementing classes must derive from `AppliesToEntityClass`;
- the `I` prefix is conventional.

**Performance guidance (new):** prefer property-less mixins. Querying mixin properties produces `UNION ALL` branches; SQLite allows at most 500 compound-SELECT terms. A property-less mixin works as a relationship endpoint or capability marker, while optional cross-class property sets should be Aspects [D-MIX][D-SDG].

**Domain mixins relevant here:**
- LinearReferencing: `ILinearElement` (properties StartValue and LengthValue), `ILinearElementSource`, `ILinearlyLocated`, `ILinearlyLocatedAttribution`, `ILinearLocationElement`, `IReferent` [LR];
- SpatialComposition: `ISpatialOrganizer` (applies to SpatialLocationElement) [SPCOMP];
- Terrain: `ITerrain` [TRRN].

### 2.8 Codes, CodeSpecs and CodeScopes

**Model:**
- **Uniqueness:** the triple (CodeSpec, CodeScope, CodeValue) must be unique; *"All `null` values are considered to be unique"* [D-CODE][BC].
- **CodeValue** is nullable [BC]. The API trims leading and trailing whitespace and compares case-insensitively; `Code.createEmpty()` uses CodeSpec Id 0x1 and scope 0x1 with an empty value [C-CODE]. The schema collation is NoCase [BC].
- **CodeSpec** is a sealed `bis:CodeSpec` entity, not an Element. It has a unique `Name` and `JsonProperties` holding scope and fragment specs. *"A best practice is to incorporate the schema alias or domain name into the bis:CodeSpec name"* [BC][D-CODE].
- **CodeScope types** are `Repository`, `Model`, `ParentElement` and `RelatedElement` [D-CODE][C-CODE].

**Standard BisCore CodeSpec names** (`BisCodeSpec` enum, "match those specified by the native library") [C-CODE]:
- `bis:NullCodeSpec`, `bis:Subject`, `bis:InformationPartitionElement`;
- `bis:SpatialCategory`, `bis:DrawingCategory`, `bis:SubCategory`;
- `bis:GeometryPart`, `bis:PhysicalType`, `bis:SpatialLocationType`, `bis:GraphicalType2d`, `bis:PhysicalMaterial`;
- `bis:RenderMaterial`, `bis:Texture`, `bis:LineStyle`;
- `bis:ModelSelector`, `bis:CategorySelector`, `bis:DisplayStyle`, `bis:ViewDefinition`;
- `bis:Drawing`, `bis:Sheet`, `bis:SheetIndex`, `bis:SheetIndexEntry`;
- `bis:LinkElement`, `bis:ExternalSource`, `bis:ExternalSourceAttachment` (the last two are not created automatically);
- `bis:TemplateRecipe2d/3d`, `bis:AuxCoordSystem2d/3d/Spatial`, `bis:AnnotationTextStyle/FrameStyle/LeaderStyle`, `bis:TextAnnotationSeed`, `bis:ColorBook`.

**Misuse** [D-CODE]:
- GUIDs and hashes are not codes; FederationGuid exists for that purpose.
- Avoid very long codes; iModelHub's code service has a hard limit of 350 characters.
- A CodeSpec may dictate null codes for entities with no real-world identifier ("a piece of baseboard, a pile of dirt, an average bolt").

**Who creates CodeSpecs, and typical scopes** [D-CODE]:
- **Connectors:** *"Reproduce existing code/tag/name-uniqueness rules in the source format, if any."* Scope by model, by parent, or by related element. *"By-repository is typically invalid."* The scope element is referenced by ElementId.
- **Users and admins:** scope via FederationGuid; codes are typically unique across repositories.

**Codes of partitions:** CodeSpec `bis:InformationPartitionElement`, scoped per parent Subject. Default values [D-MP]:
- `Definitions`, `Documents`, `Graphics`, `Groups`, `InformationRecords`, `Links`, `Physical`, `PhysicalSystems`, `SpatialLocation`;
- `BisCore.DictionaryModel` and `BisCore.RealityDataSources` for the root Subject's global partitions.

**Category and SubCategory names:**
- The CodeValue is mandatory.
- Characters `<>\/."?*|,='&\n\t` are disallowed.
- Categories are scoped to their DefinitionModel; SubCategories are scoped to their parent Category [D-CAT][BC-R].

### 2.9 Categories (display classification)

- Every GeometricElement is in exactly one Category. Categories do not nest; SubCategories subdivide them [D-CAT].
- The analogy is spelled out: *"Categories are similar to levels in DGN, layers in DWG, and categories in RVT"* as geometry-classification constructs [BC][D-CAT]. Separately, *"SubCategories are similar to levels in DGN, layers in DWG and subcategories in RVT as being the construct directly involved in visibility and styling of geometry"* [D-CAT].
- Every Category automatically gets a default SubCategory [BC-R].
- A GeometryStream may reference only SubCategories of the element's own Category [D-CAT].
- `Rank`: System=0, Domain=1, Application=2, User=3 [BC][D-CAT].

**Guidance:**
- *"Each application synchronizing external data … shall store its Categories on its own Editing Channel."* [D-CAT].
- Domains may propose default categories but *"shall not rely on the usage of any particular Category for semantic classification"* [D-CLASS][D-CAT].
- Examples of domain-ranked defaults: RoadRailAlignment's `Alignment`, `Linear` and `Vertical Alignment` [RRA-R]; `cvphys:Curb` [CVP-R].

### 2.10 Provenance classes

**RepositoryLink** (a UrlLink with `RepositoryGuid`, `Format` and an optional `RepositoryInfoAspect.OriginatingSystem`) is the external file or repository [BC][D-PROV].

**ExternalSource** is an "information container" inside a repository, for example a DGN model within a DGN file. It carries `Repository`, `ConnectorName` and `ConnectorVersion` [BC][L-WAC].
- **ExternalSourceAttachment**, owned by an ExternalSource, models reference hierarchies and acyclic graphs. It includes the transform (Translation, Yaw/Pitch/Roll, Scale) and a Role of SpecifyContext or SpecifyPart [BC][D-PROV].
- **ExternalSourceGroup** handles duplicates across files, for example the same IFC wall in three files [D-PROV].

**ExternalSourceAspect** (multi) has these fields [BC][D-PROV]:
- `Identifier` (required);
- `Kind`;
- `Scope`, usually a RepositoryLink or a partition, within which Identifier plus Kind is *typically* unique (not enforced);
- `Source` (required);
- optional `Version` (*"must be guaranteed to change"* when content changes);
- optional `Checksum`;
- optional `JsonProperties`.

*"It is recommended that data-synchronizers assign not-null values to `Identifier`, `Kind` and `Scope` since they are targeted by aspect-finding-APIs"*. Several aspects per element are allowed, including duplicates [D-PROV].

**SynchronizationConfigLink:** by convention, `CodeValue` holds a unique job Id and `UserLabel` holds a name [BC-R].
- `SynchronizationConfigSpecifiesRootSources` points at the root sources.
- `SynchronizationConfigProcessesSources` points at every source processed.

**FolderLink** organises repositories into folders [BC].

### 2.11 Drawings, sheets and views

Source: [D-DS].

- `Drawing` and `Sheet` mix in `ISubModeledElement` and are contained in `DocumentListModel`s.
- A `Drawing` is sub-modelled by a `DrawingModel`, which holds `DrawingGraphic`s and annotations. A `Sheet` is sub-modelled by a `SheetModel`, which holds `ViewAttachment`s. The attachment's `BBoxLow/High` set its placement, and its `View` points to a ViewDefinition.
- `SectionDrawing` has `SectionType` (Section, Detail, Elevation, Plan) and `SpatialView`.
- `SectionDrawingLocation` is a SpatialLocationElement that refers to the section view.

**Newer classes** [BC-R]:
- `SheetPrototype` (a DefinitionElement sub-modelled by a SheetModel) and `SheetBorderPrototype` (sub-modelled by a DrawingModel) replace the deprecated SheetTemplate, SheetBorderTemplate and SheetBorder.
- The sheet-index classes live in a `SheetIndexModel` under a `SheetIndexPartition`.

**View rules** [BC-R]:
- A ViewDefinition needs a DisplayStyle and a CategorySelector.
- A SpatialViewDefinition also needs a ModelSelector.
- Definition elements referenced by views are deletion-protected.

**JsonProperties use:** the docs explicitly route extra settings for ViewAttachment, SectionDrawing and view details to `JsonProperties` [D-DS]. ColorBook stores its colours in JsonProperties [BC-R]. RenderMaterial is sealed so that JsonProperties can "morph" between a DgnV8 render material and a PBR material [BC-R].

### 2.12 Deprecated BisCore items: avoid writing them

Source: [BC].

- Classes: `InformationCarrierElement`, `DocumentCarrier`, `PhysicalPortion` (mix `ISubModeledElement` into a PhysicalElement subclass instead), `SpatialLocationPortion`, `SectionLocation` (use `SectionDrawingLocation`), `SheetTemplate`, `SheetBorderTemplate`, `SheetBorder`, `TextAnnotationData`, `WebMercatorModel`.
- Relationships: `PartitionOriginatesFromRepository`, `PhysicalModelBreaksDownPhysicalPortion`, `SectionLocationUses*` and `SectionLocationRefersToViewAttachment`, `SheetHasSheetTemplate`, `SheetTemplateHasSheetBorder`, `SheetBorderHasSheetBorderTemplate`.
- Properties: `Sheet.SheetTemplate`, `AnnotationTextStyle.Data`, `SynchronizationConfigLink.LastSuccessfulRun`.
- In other schemas: Functional `FunctionalComposite` and `FunctionalPortion` [FUNC]; SpatialComposition `CompositeElement`, `ICompositeBoundary`, `ICompositeVolume`, `CompositeComposesSubComposites` and `CompositeOverlapsSpatialElements` [SPCOMP].
  - BuildingSpatial still mixes in the deprecated `spcomp:ICompositeVolume` [BLDSP].

### 2.13 What was added to BisCore after 01.00.16 (present in 01.00.27)

Diff of class and relationship names [BC-27 vs released 01.00.16]:
- **Dimensions:** `DimensionAnnotation2d` plus the Linear, Angular, Radial and Ordinate variants; `AnnotationDimensionStyle`.
- **Tables and text:** `TableAnnotation2d`, `ITextAnnotation`, `TextAnnotationUsesTextStyleByDefault`.
- **Sheets:** `SheetPrototype`, `SheetBorderPrototype`, `SheetInformationAspect`.
- **Projects and repositories:** `ProjectInformationRecord`, `RepositoryInfoAspect`, `FolderContainsFolders`.
- **Definitions and formats:** `FormatSet`, `DefinitionThumbnail`.
- **Recipes and types:** `TemplateRecipe3dInPlan`, `SpatialLocationTypeRepresentsTypeDefinition`, `PhysicalTypeComposesSubTypes`.
- **Representation and materials:** `SpatialLocationElementRepresentsElement`, `RenderMaterialSupportsDisplayOfPhysicalMaterial`.
- **Sub-model relationships:** most explicit `*BreaksDown*Partition`, `ModelBreaksDownRepositoryLink` and the template-recipe sub-model relationships.

**Implication for writers:** if you target older iModels or runtimes, check the BisCore version present before using these classes. For example, iModels whose BisCore is older than 01.00.27 lack some of them.

---

## 3. "Top of the world" and the tree a connector job creates

### 3.1 The rules

**RepositoryModel** [D-TOW][BC-R][C-IMODEL]:
- Exactly one per repository; Id `0x1`.
- It sub-models the root Subject, which is also Id `0x1` and contained in the RepositoryModel itself.
- *"The RepositoryModel is the only Model in a BIS repository that sub-models an Element contained within itself."*
- It cannot be deleted.

**Subject** [D-TOW][BC-R]:
- Sealed; lives *only* in the RepositoryModel.
- Exactly one root Subject: it has no parent, can be updated but never deleted, and describes what the repository is about.
- Every other Subject's `Parent` must be a Subject (`SubjectOwnsSubjects`).
- *"The hierarchy of Subjects is not meant to model the root Object's structure, but is a way to express high-level structure that has not been modeled explicitly… All modeling happens using Elements in the other bis:Models."*
- A Subject may own one `ProjectInformationRecord` (ProjectName, ProjectNumber, Location) [BC].
- `SubjectRefersToSubject` links Subjects across branches [BC-R].

**InformationPartitionElement** [BC-R][D-TOW][D-MP]:
- Only in the RepositoryModel; its `Parent` must be a Subject.
- Each partition is sub-modelled by a Model of the same modelling perspective.
- *"it is usually expected for a Subject to have only one InformationPartitionElement instance of a given modeling perspective, but it is not enforced… better addressed via child Subjects"*.
- **Physical:** *"There is one and only one Physical modeling perspective for a given Subject instance."* [D-MP] and "There should not be more than one per bis:Subject" [D-3D].
- Partition names are not business-meaningful. The parent Subject's Code acts as the name of the partition's sub-model [D-TOW].

**Well-known global partitions and models** under the root Subject [D-ODE][D-MP][C-IMODEL]:
- DefinitionPartition `"BisCore.DictionaryModel"` → the **DictionaryModel**, Id `0x10`.
- LinkPartition `"BisCore.RealityDataSources"` → the global reality-data LinkModel. TerrainReference uses it for RepositoryLinks [TRRN-R].

**Definition organisation** [D-ODE]:
- The DictionaryModel directly holds **User**-rank definitions. Standardised definitions go under DefinitionContainers, each with `CodeScope` set to the containing DefinitionModel:
  - `bis:Definitions` for Core rank;
  - `{domain alias}:Definitions` for Discipline rank;
  - `{organization name}:{application name}:Definitions` for Application rank.
- **Subject-specific definitions**, "typically created by iModel Connectors", must be **User-rank only**. They go in a DefinitionPartition with CodeValue `"Definitions"`, scoped to its parent Subject.

**Editing channels** [D-TOW][L-CH][BC-R]:
- A channel is the tree below a *channel root* (a Subject or partition carrying `ChannelRootAspect.Owner = channelKey`).
- Channel keys follow `{organization-name}:{application-name}:{extension-name-if-any}`, for example `bentley:opensiteplus`.
- Everything outside any channel root is the "shared" channel.
- Channels do not nest.
- `ChannelRootAspect.Version` is a semantic version string in the form read.write.minor.

**Single responsible party (SRPP)** [D-SRPP]: *"A Model should have a single responsible-party at any given point in time"*, so *"one should not model multiple disciplines in a single Model."*

### 3.2 Connector-specific rules

From [L-CONN][L-WAC]:

- *"The resulting combined iModel is partitioned at the Subject level… each connector[']s job has its own Subject."*
- **Job subject:** the framework creates a uniquely coded Subject that is a child of the root. *"A Connector is required to scope all of the subjects, definitions, and their models under its job subject element."* Specifically:
  - Subjects and partitions the connector creates should be children of the job subject.
  - Models and other elements should be children of those, or inside those models.
- *"The only time a Connector should write to a common model, such as the dictionary model, is when it creates its job subject."*
- Codes are scoped by the job subject, so *"codes are not reserved"*.
- **Model provenance:** *"each Connector must create an ElementHasLinks ECRelationship from the InformationContentElement element representing the model to one or more RepositoryLink elements that describe the source document"*. Concretely, link the PhysicalPartition to the RepositoryLink. `PartitionOriginatesFromRepository` is deprecated in favour of `ElementHasLinks` [BC].
- **ExternalSourceAspect.Scope** should point to the RepositoryLink. Earlier samples pointed it at partitions, which forced a legacy "channel-based" deletion mode. *"Any new connector author/developer should choose file-based."*
- **DGN files** with several models: the RepositoryLink represents the file and an ExternalSource represents each DGN model. Use ExternalSourceAttachments to mirror the reference hierarchy.
- The docs' example connector layout: *"In the case of dgn files, both the OpenBuilding Designer and Bentley Civil iModel Connectors create child Subjects for every referenced dgn & model on the external dataset."* [D-IH]
- The physical hierarchy page: the Subject-hierarchy approach "is used by iModel connectors and legacy applications, because it more closely maps to legacy ways of modeling". A top PhysicalModel may hold many elements when that matches the source [D-PHIER].

### 3.3 Canonical tree for one connector job

This is a **synthesis**. Each line says whether the rule behind it is **[M]**andated by schema or handler, **[R]**ecommended in the docs, or **[O]**bserved in Bentley output.

```
RepositoryModel 0x1                                                              [M]
└─ Subject (root, 0x1) Code=<iModel name>                                        [M]
   ├─ DefinitionPartition "BisCore.DictionaryModel" → DictionaryModel 0x10       [M]
   │   ├─ CodeSpecs live in their own table (bis:CodeSpec is not an Element)      [M]
   │   ├─ DefinitionContainer "bis:Definitions" / "<alias>:Definitions" / "<org>:<app>:Definitions" → DefinitionModel   [R]
   │   └─ (User-rank global definitions)                                          [R]
   ├─ LinkPartition "BisCore.RealityDataSources" → LinkModel (reality RepositoryLinks)   [R][O]
   └─ Subject  <job subject, unique code; channel root if a channel key is used> [R]
      ├─ DefinitionPartition "Definitions" → DefinitionModel                      [R]
      │     SpatialCategory/DrawingCategory (+SubCategory) from levels and layers, PhysicalType/GraphicalType2d,
      │     GeometryPart (shared cell or block geometry), LineStyle, RenderMaterial, Texture, ...  (User-rank only)
      ├─ (RepositoryLink per source file; ExternalSource per source model; ExternalSourceAttachment per reference.
      │   The model they belong in is NOT specified by the docs read; see §9.)
      └─ Subject per source file and/or source model ("child Subjects for every referenced dgn & model")   [R][O]
         ├─ PhysicalPartition "Physical" → PhysicalModel  —ElementHasLinks→ RepositoryLink                 [R]
         │     PhysicalElements, SpatialLocationElements, GraphicalElement3d, information records
         │     (each element: ExternalSourceAspect{Identifier, Kind, Scope=RepositoryLink, Source=ExternalSource, Version/Checksum})
         ├─ DocumentPartition "Documents" → DocumentListModel                                            [M for docs]
         │     ├─ Drawing → DrawingModel (DrawingGraphic, AnnotationElement2d …)
         │     └─ Sheet → SheetModel (ViewAttachment …)
         ├─ GroupInformationPartition "Groups" → generic:GroupModel (generic:Group; members via ElementGroupsMembers)   [R][O]
         └─ (FunctionalPartition subclass / AnalyticalPartition subclass, if the source has those perspectives)
```

### 3.4 What Bentley's DGN connector actually produced (public evidence)

**Models in a test iModel.** An itwinjs-core presentation test lists *all* `bis.Model` instances of an iModel produced by the DGN ("DgnV8Bridge") connector from `Properties_60InstancesWithUrl2.dgn` [X-PRES]. There are eight:

1. `BisCore.DictionaryModel`
2. `BisCore.RealityDataSources`
3. `Converted Drawings`
4. `Converted Groups`
5. `Converted Sheets`
6. `Definition Model For DgnV8Bridge:D:\Temp\Properties_60InstancesWithUrl2.dgn, Default`
7. `DgnV8Bridge`. This is probably the RepositoryModel labelled by the root Subject; display-test-app notes *"iModel.name is often something useless like 'DgnV8Bridge'"* [X-DTA].
8. One `GeometricModel3d`, the converted DGN model.

**Inferred layout.** That points to:
- a per-file and per-model DefinitionModel holding converted definitions;
- separate document-list models for drawings and sheets;
- a group model for named groups. BisCore remarks: GroupInformationPartition is *"used primarily to hold generic:Group Elements generated from DgnV8 'Named Groups'"* [BC-R].

**A converted element.** A snapshot of a converted element's properties shows class "Physical Object", i.e. `generic:PhysicalObject` [X-SNAP], with:
- Category = `Uncategorized`;
- Code = `""` (empty);
- User Label = `""`;
- Model = `Properties_60InstancesWithUrl2`;
- "Source Information › Model Source › Repository Link" with Name `Properties_60InstancesWithUrl2.dgn` and Path `file:///d|/temp/...`;
- the DGN's EC instance data (`area`, `firstTry`) shown as arrays of property structs, i.e. aspect-like related instances.

This is an old test asset; current connector output may differ (§9).

---

## 4. Modelling perspectives, and when to use each

### 4.1 Perspectives and their classes

**Major perspectives** [D-MP][D-MOD]:

| Perspective | Partition (modifier) | Model | Element base | Typical use |
|---|---|---|---|---|
| Physical | PhysicalPartition [S] | PhysicalModel (never subclass) | PhysicalElement (+SpatialLocationElement) | real things with mass; "clash-detection, quantity takeoffs" |
| Functional | FunctionalPartition [C, "considered abstract"] | FunctionalModel subclass | FunctionalElement | process and functional systems (P&ID, electrical) |
| Analytical | AnalyticalPartition [A] | AnalyticalModel subclass | AnalyticalElement | structural or hydraulic analysis **inputs only** |
| Definition | DefinitionPartition [S] | DefinitionModel | DefinitionElement | catalogs, libraries, styles |
| Document | DocumentPartition [S] | DocumentListModel | Document (Drawing, Sheet) | drawings and sheets |

**Specialised perspectives** [D-MP][D-MOD]:

| Perspective | Partition | Model | Element base | Typical use |
|---|---|---|---|---|
| Graphics-only 3D | GraphicalPartition3d [S] | GraphicalModel3d subclass (generic:GraphicalModel3d) | GraphicalElement3d | "Pure graphics positioned in true world coordinates" |
| Group information | GroupInformationPartition [S] | GroupInformationModel subclass (generic:GroupModel) | GroupInformationElement | "Named Groups" |
| Information records | InformationRecordPartition [S] | InformationRecordModel | InformationRecordElement | "Non-graphical data records" |
| Links | LinkPartition [S] | LinkModel | LinkElement | links to reality data and repositories |
| Physical systems | PhysicalSystemPartition [S] | PhysicalSystemModel | PhysicalSystem | water, HVAC, electrical systems |
| Spatial location | SpatialLocationPartition [S] | SpatialLocationModel | SpatialLocationElement | "A Building's Grid, Roads' Alignments or Parcel boundaries. Subset of Physical." **Discouraged as a partition** (§0.3) |
| Sheet index | SheetIndexPartition [S] | SheetIndexModel | SheetIndex, SheetIndexEntry | sheet organisation [D-DS] |

**Consistency rule:** *"Each InformationPartitionElement has a sub-Model that is of the same modeling perspective. That sub-Model contains only Elements of the same modeling perspective"*. For example, a physical fire hydrant cannot go into a section-drawing model [D-MP].

**Physical backbone:** *"the physical perspective should be the 'touchstone'"*. Non-physical elements relate back to the PhysicalElement [D-MP][D-PHYS][D-DOM].

### 4.2 Decision guidance per perspective

**Physical**, `bis:PhysicalElement` [D-PHYS]:
- Use it when the object has mass, can be touched, and its spatial characteristics matter for construction, operation or maintenance.
- Examples of things that are *not* PhysicalElements: property lines, groundwater elevations, construction superintendents, and cranes used only for resource planning.
- *"Any object in the real world is modeled as a PhysicalElement a maximum of once within a BIS repository."*
- Each PhysicalElement relates to at most one PhysicalType.

**Spatial location**, `bis:SpatialLocationElement` [BC-R][D-3D]:
- Massless but real-world. Three kinds:
  - defined by physical things (air gaps, clearances, surface regions);
  - guides for placing physical things (gridlines);
  - abstractions with physical consequence (property or political boundaries).
- Put them in a PhysicalModel, or in a SpatialLocationModel that sub-models another SpatialLocationElement. Avoid the partition.

**Where to put a new 3D concept** [D-3D]:
- physical geometry with mass → PhysicalElement;
- a location in space → SpatialLocationElement;
- a 3D abstraction for analysis → specialise AnalyticalElement, AnalyticalModel and AnalyticalPartition together;
- anything else → consult Bentley's BIS experts.

**Graphical 3D**, `GraphicalElement3d`: graphics that hold information rather than business geometry, such as text annotations or 3D bar charts, *"typically positioned for whitespace reasons"* [D-EL]. They may sit in a PhysicalModel or SpatialLocationModel "for annotations and illustrations of information" [D-3D]. `GraphicalElement3dRepresentsElement` relates them to the element they depict [BC].

**Functional:** "form follows function". Functional elements are *roles* without geometry [FUNC-R][D-FUNC].
- `PhysicalElementFulfillsFunction` is an assignment, not a verification [D-FUNC].
- Use the `FunctionalBreakdownElement`/`FunctionalComponentElement` pair when not sub-modelling; specialise `FunctionalElement` directly when sub-modelling [FUNC-R].
- The Functional perspective is abstract: domains must specialise the partition, model and element classes [FUNC-R].

**Analytical:**
- An idealised parallel representation used as *input* to a solver.
- *"a BIS repository is not appropriate to store results or output data from an analysis"* [D-MP][D-ANLYT].
- Specialise all three classes; `AnalyticalSimulatesSpatialElement` links back to the physical elements [ANLYT-R][D-ANLYT].

**Information** [D-INFO][BC]:
- `InformationRecordElement` is *"the default choice if no other subclass of InformationContentElement makes sense"*, for example a spreadsheet import.
- `DefinitionElement` holds shared configuration and must be in a DefinitionModel.
- `Document` is a collection of information meant to be understood as a whole; Drawing and Sheet are subclasses.
- `InformationReferenceElement` covers Subject, LinkElement and GroupInformationElement. A DgnV8 named group maps to a GroupInformationElement subclass.
- Typical containers: InformationRecordElement in InformationRecordModel, DefinitionElement in DefinitionModel, partitions in RepositoryModel, LinkElement in LinkModel or another InformationModel, GroupInformationElement in GroupInformationModel, Document in DocumentListModel.

**Drawings and sheets:** `Drawing` + `DrawingModel` holds 2D content (DrawingGraphic, annotations); `Sheet` + `SheetModel` holds ViewAttachments [D-DS][D-MP]. `DrawingGraphicRepresentsElement` links 2D graphics to elements of other perspectives [D-MP].

**Physical systems:** a `PhysicalSystem` is a GroupInformationElement, *"information about the physical system"*, not a PhysicalElement. `PhysicalSystemGroupsMembers` lets one element belong to several systems; if the target element is sub-modelled, its sub-model elements count as members [D-SYS].

**Spatial composition** [SPCOMP][D-SC][D-CLASS]:
- Region ⊃ Site ⊃ Facility ⊃ FacilityPart ⊃ Space, plus Zone.
- Use it for containment hierarchies; spatial structure elements "hold" or "reference" SpatialElements.
- *"Deep hierarchies of physical-element instances typically model multiple levels of containment. Such cases are usually better modeled via Spatial Composition"* [D-CLASS].

**Whole and part: choosing a technique** [D-OME][D-MWB]:
- **atomic element**: geometry, properties and aspects only;
- **parent-child** (`IParentElement`): parent plus children add up to the whole, with no redundancy;
- **spatial composition**;
- **sub-modelling** (`ISubModeledElement`): the sub-model re-represents the whole at a finer granularity, intentionally redundant. Reasons to sub-model: delegating detail to another responsible party, or workflows that need both the whole and its parts.
- *"Software should not be too 'rigid' in its expectations of how Elements are organized in to Models, especially in the physical perspective"* [D-OME][D-SYS].

### 4.3 The Generic schema, and when "Generic" is acceptable

Generic: alias `generic`, Core layer, released 01.00.05 `Production` [GEN][§1.8]. Schema description: *"This schema contains classes that are completely generic. These classes should only be used when there is not enough context to pick something more specific."* [GEN]

| Class | Base | Modifier | XML description and notes |
|---|---|---|---|
| `PhysicalObject` | bis:PhysicalElement | Sealed | "used for bis:PhysicalElements which cannot be further classified. More-specific… should be used wherever possible." |
| `SpatialLocation` | bis:SpatialLocationElement | Sealed | same wording for spatial locations |
| `Graphic3d` | bis:GraphicalElement3d | Sealed | "generic 3D graphics which cannot be further classified" |
| `GraphicalModel3d` | bis:GraphicalModel3d | Sealed | the concrete container for GraphicalElement3d |
| `PhysicalType`, `SpatialLocationType`, `GraphicalType2d` | the bis type classes | Sealed | generic types |
| `PhysicalMaterial` | bis:PhysicalMaterial | Sealed | generic material |
| `Document` | bis:Document | Sealed | generic document |
| `Group` / `GroupModel` | GroupInformationElement / GroupInformationModel | Sealed | named-group target (§3.4) |
| `DetailingSymbol` | GraphicalElement2d | Abstract | parent of `TitleText`, `ViewAttachmentLabel` {ViewAttachment, ClipGeometry} and `Callout` {DrawingModel}; Callout has Section, Elevation, Plan and Detail subclasses |

**There is no `GenericGroup` class.** The names are `generic:Group` and `generic:GroupModel` [GEN].

**For 2D**, the concrete core classes are `bis:DrawingGraphic` and `bis:AnnotationElement2d` (and its subclasses), not Generic [BC]. The connector guidance names `DrawingGraphic` as the 2D fallback [D-SDG].

**Official rule for connectors facing concepts they don't understand** [D-SDG]:
1. *"Discern, or make a good guess, about the general classification of the concept in terms of BisCore base-classes. That is, choosing between PhysicalElement, SpatialLocationElement or DrawingGraphic."*
2. Then either **(a)** *"Directly subclass the chosen base-class from the BisCore schema if Standard-intrinsic properties need to be introduced"*, or **(b)** *"Target the appropriate class from the Generic schema - e.g. PhysicalObject or SpatialLocation."*
3. The cost of reclassifying later: the connector *"will have to recreate those elements… disruptive for existing consumers"*. To mitigate, *"Try to reuse the ElementId"* and *"Preserve… Code and FederationGuid"*.

**For concepts the connector does understand but BIS lacks:** subclass BisCore's `PhysicalElement` or `SpatialLocationElement` *directly*. *"Avoid introducing an intermediate base-class in the iModel Connector schema"*, because it blocks re-basing onto a future domain class [D-SDG].

**First choice:** *"Use an existing element-class or type-definition class from a schema at the Discipline-Physical or Discipline-Other layers… when possible"* [D-SDG]. *"Where clear BIS schema types exist, they should always be used"* [L-CONN].

---

## 5. Domain schemas for transportation and civil

All of these are at **Discipline-Physical** unless stated. Release status is from §1.8.

### 5.1 LinearReferencing (lr, Common, released 02.00.03 Production)

From [LR][LR-R].

**Mixins:**
- `ILinearElement` {StartValue, LengthValue, LinearElementSource}: the 1-D axis measurements are made along (cf. IfcLinearPositioningElement).
- `ILinearElementSource`.
- `ILinearlyLocated` (cf. IfcLinearPlacement), with sub-mixins `ILinearlyLocatedAttribution` {AttributedElement}, `ILinearLocationElement` and `IReferent` {ReferencedElement}.

**Concrete or abstract bases:**
- `LinearLocationElement` [A] (SpatialLocationElement), with `LinearLocation` [S];
- `LinearPhysicalElement` [A] (PhysicalElement + ILinearLocationElement);
- `LinearLocationRecordElement` [A] (InformationRecordElement), with `LinearLocationRecord` [S];
- `LinearlyLocatedAttribution` [A] (SpatialLocationElement);
- `ReferentElement` [A], with `Referent` [S].

**Locations are multi-aspects:**
- `LinearlyReferencedAtLocation` {AtPosition: DistanceExpression, FromReferent};
- `LinearlyReferencedFromToLocation` {FromPosition, ToPosition, plus referent navigations}.
- `DistanceExpression` = {DistanceAlongFromStart, LateralOffsetFromILinearElement (+ is right), VerticalOffsetFromILinearElement (+ is up), DistanceAlongFromReferent}.

**Relationships:**
- `ILinearlyLocatedAlongILinearElement` (ElementRefersToElements);
- `ILinearLocationLocatesElement`;
- `LocatedElementOwnsLocatingElements` (embedding, for attaching linear locations to elements that are not themselves linearly located).

`LinearlyLocatedAttribution` instances must be in a PhysicalModel or SpatialLocationModel. Curves are an implementation detail of each domain's `ILinearElement` [LR-R].

### 5.2 RoadRailAlignment (rralign)

Released **02.00.04 FieldTesting**; 02.00.03 Production. From [RRA][RRA-R].

**Classes:**
- `Alignment` [S]: SpatialLocationElement + ISubModeledElement + lr:ILinearElement. Properties {StartStation, LengthValue = horizontal length, Horizontal, MainVertical}. Its GeometryStream holds a stroked 3D path (≈ IfcGradientCurve). ≈ IfcAlignment.
- `AlignmentType`: a SpatialLocationType (≈ IfcAlignmentTypeEnum).
- `HorizontalAlignment` [C, hidden] {HorizontalGeometry: `Bentley.Geometry.Common.IGeometry`}. Lives in a *plan-projection* SpatialLocationModel. Segments are LineSegment3d, Arc3d or TransitionSpiral3d with Z = 0. Clothoid, cosine, sine, biquadratic, bloss and vienna spirals map to IFC; DirectSpiral3d has no IFC equivalent.
- `VerticalAlignment` [C]: a **GeometricElement2d** {VerticalGeometry: X = distance along, Y = elevation}. Lives in a `VerticalAlignmentModel` (GeometricModel2d) that sub-models the Alignment. One vertical is "Main"; others (e.g. a ditch-bottom profile) are allowed.
- `AlignmentStation` [S] (lr:ReferentElement) {Station}. Child of the Alignment via `AlignmentOwnsReferents`; carries an `AtLocation` aspect.
- `DesignAlignments` and `HorizontalAlignments`: hidden SpatialLocationElement + ISubModeledElement entry points for SpatialLocationModels. Code of the latter = "Horizontal Alignments".
- `HorizontalAlignmentOwnsSegmentGraphics` and `VerticalAlignmentOwnsSegmentGraphics`: child GraphicalElement3d/2d per segment for symbology control.

**Placement:** Alignments go in a SpatialLocationModel or PhysicalModel, typically a sub-model of a SpatialLocationPartition or PhysicalPartition. The suggested domain-ranked categories are `Alignment` (design), `Linear` (secondary) and `Vertical Alignment`.

**Units:** KindOfQuantities STATION, LENGTH, AREA, ANGLE, BEARING, SLOPE and VELOCITY, all in SI. The WIP references CivilUnits; 02.00.03 referenced RoadRailUnits.

### 5.3 RoadRailPhysical / RoadPhysical / RailPhysical (rrphys 03.00.00, rdphys and rlphys 01.00.00; Production, 01/28/2020)

From [RRP][RDP][RLP].

**RoadRailPhysical:**
- `TransportationNetwork` [A, hidden]: PhysicalElement + ISubModeledElement, an entry point.
- `Corridor` [S]: LinearPhysicalElement + ISubModeledElement + ILinearElementSource.
- `TransportationSystem` [S]: sub-modelled.
- `CorridorPortionElement` [A]: PhysicalElement + IParentElement + ILinearElementSource {MainAlignment}. Subclasses: `UndeterminedCorridorPortion` [S] and `PathwayElement` [A].
- `PathwayDesignCriteria` [S]: SpatialLocationElement, sub-modelled.
- `DesignSpeed` [S]: lr:LinearlyLocatedAttribution.
- `DesignSpeedDefinition` [S]: DefinitionElement.
- `CorridorPortionOwnsAlignments`.

**RoadPhysical:** `RoadNetwork`, `Roadway` [S] (a PathwayElement). **RailPhysical:** `RailNetwork`, `Railway` [S].

**Overlap:** these have not been revised since 2020 and overlap conceptually with the newer **RoadSpatial**. For example, `rdphys:Roadway` is a physical pathway while `rdsp:Roadway` is a spatial FacilityPart (§5.5). No reconciliation note was found; see §9.

### 5.4 CivilPhysical (cvphys, 01.00.03 FieldTesting)

From [CVP][CVP-R].

- `LayeredStructure` [A] (+IParentElement) and `LayeredStructureType` [A].
- `LayeredStructureAssemblesLayers` and `LayeredStructureTypeComposesSubTypes` [A], a link table carrying **SubTypeThickness**, with **MemberPriority** giving the layer order (0 = top).
- `Course`/`CourseType` (≈ IfcCourse; the volume is stored as a Polyface).
- `Curb`/`CurbType` (≈ IfcKerb; default category `cvphys:Curb`).
- `RetainingWall`/`RetainingWallType`.
- `SurfaceMarking`/`SurfaceMarkingType`.
- Course, Curb and RetainingWall are `lr:LinearPhysicalElement`, so they can be located along an Alignment.

### 5.5 Spatial composition for civil (CivilSpatial 01.00.04 FieldTesting; RoadSpatial 01.00.00 FieldTesting; BridgeSpatial never released)

From [CVSP][RDSP][RDSP-R][BRSP].

**CivilSpatial:**
- `Site`, `SiteType`;
- `Space` [A] {FootprintArea, SlopedTopArea};
- ParkingArea, Parcel, ParkingIsland, ParkingRow {space counts and dimensions}, Sidewalk (+ILinearLocationElement), PondArea, GenericArea [S]; each with Types.

**RoadSpatial:**
- `Road` (spcomp:Facility + ILinearLocationElement; ≈ IfcRoad);
- `RoadPart` [A];
- RoadwayPlateau, CentralReserve, CentralReservePart, RoadSide, RoadSidePart, Roadway, TrafficLane, Shoulder, JunctionElement [A] → Intersection;
- `RoadIncludesJunctions`.
- Placement: in SpatialLocationModels or PhysicalModels.
- The remarks still say *"Currently under development… should not be used for production workflows"*, even though the release is FieldTesting [RDSP-R].

**BridgeSpatial** (WIP): `Bridge` (Facility), Substructure, Superstructure, Deck, DeckSegment, Pier, Abutment, each with Types.

**Typical bus-terminal example** with Holds and References [D-SC]:
- Site → Building / Road / Bridge;
- a Roadway *holds* pavement courses;
- a Traffic Lane *references* lane markings;
- a Bridge Pier holds columns, footings and caps.

### 5.6 Bridges and structures

**BridgeStructuralPhysical** (bphys, 02.00.01 Production) [BSP]:
- `Bridge` [S]: LinearPhysicalElement + ILinearElement + ISubModeledElement, sub-modelled via `PhysicalModelBreaksDownBridgeElement`.
- `StructuralSystem` [hidden].
- `SubstructureElement` [A] {Skew} → `GenericSubstructureElement`.
- `SuperstructureElement` [A] → `GenericSuperstructureElement`.
- Both assemble `sp:StructuralMember`s.

**BridgePhysical** (brphys, never released) [BRP]: Bearing, BearingSeat, Pier, Abutment, WingWall, CrossFrame and Girder, each with a PhysicalType.

**StructuralPhysical** (sp, 01.00.01 Production) [SP]:
- `StructuralElement` [A] → `StructuralMember` → Slab, Wall, Beam, Column, Brace, FoundationMember (StripFooting, SpreadFooting, PileCap, Pile), each with a Type.
- Also a legacy `StructuralPhysicalModel`.

### 5.7 Other civil and transport-adjacent schemas

Descriptions are from the schema headers. **Discipline-Physical:**
- **Terrain** (trrn, 01.00.00 Production): `ITerrain`, `TerrainReference` (service-provided, with RepositoryLink in RealityDataSources), `Terrain`, `TerrainBoundary`, and source-feature SpatialLocationElements (breaklines, spots, contours, holes, voids, islands, drape boundaries/voids). *"Cuts, fills and modifications… are defined in the Earthwork schema"* [TRRN][TRRN-R].
- **Earthwork** (ew, 01.00.00 Production): Fill, Cut and SurfaceGrade, each with a PhysicalType [EW].
- **StormSewerPhysical** and **WaterDistributionPhysical**: storm, sewer and water network physical elements. **GeotechnicalExploration** (never released): boreholes etc.
- **RoadAttributions** (never released): road linearly-located attributions.
- **CivilUnits** (FieldTesting) replaces **RoadRailUnits** (Deprecated).

**Common:** **Profiles** (cross-section profiles, 01.00.04), **NetworkTopology** (01.00.00), **ClassificationSystems** (FieldTesting) [R-INV].
- ClassificationSystems pattern: `ClassificationSystem` → `ClassificationTable` (sub-modelled) → `Classification`, plus `ClassificationGroup`. Global systems go in the DictionaryModel with CodeSpec `clsf:ClassificationSystem` and CodeValue "name + ' ' + edition" (≈ IfcClassification) [CLSF-R].

**Discipline-Other:** **RoadNetwork** and **RailNetwork** (never released); **StructuralAnalysis**, **SewerHydraulicAnalysis**, **PipeNetworkHydraulicAnalysis** (released analytical domains) [R-INV].

**Building** [BLDSP][ARCH][BLDPHYS]:
- **BuildingSpatial** (01.00.01): Building, Space, Story/RegularStory, Zone.
- **ArchitecturalPhysical** (01.00.03 Production): Door, Window, Wall, Stair, and others, each with a Type.
- **BuildingPhysical** (01.00.00): only `BuildingPhysicalModel` and `BuildingTypeDefinitionModel`, i.e. legacy model subclasses.

**Grids** (Common; WIP 02.00.00; no released file): Grid, GridAxis, GridCurve and GridSurface families, all SpatialLocationElements [GRIDS].

The docs' road-and-rail layering example [D-DOM]:
- Common: LinearReferencing, NetworkTopology, Profiles, DistributionSystems.
- Discipline-Physical: Alignments, Earthwork, Terrain, Bridges, Tunnels, Drainage.
- Discipline-Other: Structural, Traffic and Hydraulic Analysis.
- Application: connector schemas.

---

## 6. Official guidance on properties, types, codes, labels, categories and schema evolution

### 6.1 Where a source property should go

Summary table from the properties guidelines, verbatim [D-PROP]:

| Situation | Approach |
|---|---|
| Independent essential properties | Element properties |
| Dependent essential properties | Mandatory ElementAspect |
| Non-essential or optional properties | ElementAspect |
| Association properties | Link-table relationship properties |
| Derived properties | Presentation rules or ECView |
| Authoring-focused data | Any of the above, or a separate JSON-based property (string with `extendedType="Json"`) |
| Ad hoc data | `Element.JsonProperties` |

**For connectors specifically** [D-SDG], sort each source property into one of three groups:
1. **Standard, intrinsic to the classification**: a first-class property on the Element or TypeDefinition class.
2. **Standard, not intrinsic**: an Aspect class.
3. **User-defined**: *"These should be captured by an Aspect class in a dynamic schema (generated at runtime)."*

Test: *"if a given set of properties would appear on many different classes"* with no single natural base class, prefer an Aspect.

**JsonProperties** [D-EL]:
- A dictionary of *namespaces* of at least 8 characters, case-sensitive.
- `UserProps` is reserved for users; applications must never write there.
- Disadvantages: no type safety, no required data, and **not shown in generic iTwin.js UI**.
- Queryable with `json_extract`.
- *"Ad hoc data stored in the JsonProperties member is assumed to be internal to the iModel."*
- JSON properties "shall not be included in any generic UX control by default" [D-PROP].

**ExternalSourceAspect.JsonProperties** is the intended place for extra provenance or sync data [BC][D-PROV].

**Practical consequence.** Use JsonProperties for data users must see, such as DGN item-type values or tags, and those values become invisible to generic tools. Bentley's own connectors put that data in dynamic Aspect classes (§7).

### 6.2 Type definitions

From [D-TYPE][D-CLASS][D-SDG].

- A `TypeDefinitionElement` is instance data that holds per-type values for properties defined by its class. It is analogous to IfcTypeObject and to a catalog entry, with the product model number as its Code.
- Instance properties with the same name override the type's.
- Use a Type when many elements share values; use an Aspect for data owned by one element; use an element-level override for a single exception.
- Connectors should *"Make use of Type-Definitions when appropriate in order to avoid an unnecessary large number of classes"*.
- Domain authors should *"consider to introduce a type-definition class for every concrete element-class defined under the bis:GeometricElement3d class-branch."*

**Classes versus Types:** classes cover specialisations known at design time; Types cover what varies per project or twin [D-CLASS].

### 6.3 Codes for elements from external sources

**CodeValue:**
- *"Connectors should set CodeValue when a unique, human-readable identifier exists."*
- Skipping CodeValue because of occasional duplicates *"is highly discouraged"*. Instead, clean the source data, or *"adjust the corresponding CodeScope and CodeSpec to ensure uniqueness."*
- *"For some elements… such an identifier may really not exist… it would generally be detrimental to generate a CodeValue solely for not leaving it blank."* [L-WAC]
- *"CodeValue should never be set for anything other than coding purposes"*. It is not a display-label mechanism [L-CONN][L-WAC].

**Codes are not GUIDs or hashes**; use FederationGuid for identity [D-CODE].

**Connector CodeSpecs** mirror the source's uniqueness rules, scoped by model, by parent or by related element. By-repository is "typically invalid" [D-CODE]. Partition codes follow §2.8. Codes under a job subject do not need reservation [L-WAC].

**Null codes** are legal and all null codes are unique [D-CODE][BC]. The handler requires a "valid or *empty*" code [BC-R]. Exceptions where a code is required: Category and SubCategory need a CodeValue [BC-R].

### 6.4 UserLabel and the display label

**Display label precedence:** UserLabel, then CodeValue, then a generated label from the class name and type name [L-CONN].

**What connectors should do** [L-CONN][L-WAC]:
- *"UserLabel is the appropriate property for a Connector to set to control the Display Label."*
- Priorities: (1) consistency with how the source application labels things; (2) the BIS domain's default labelling strategy.
- *"If the source application data has a property that conceptually matches the BIS UserLabel property, that value should always be transformed to UserLabel."*

**Element guidance:** *"With the exception of data conversion programs, the UserLabel is always left for the user to enter and is not programmatically generated."* UserLabel is not unique [D-EL].

### 6.5 FederationGuid

From [D-FED][L-WAC][D-SDG].

- It identifies the *real-world entity at a given perspective and granularity*. Physical, functional and analytical elements for the same object get **different** GUIDs, and so do aggregates and their parts.
- It is unique within a repository and assigned automatically if the writer doesn't supply one.
- It is a stable external reference across delete and recreate. The connector docs list it as optional for mapping ids; preserve it when reclassifying.

### 6.6 Categories for source layers and levels

- The analogy is explicit: categories ≈ DGN levels and DWG layers as a classification, and SubCategories ≈ levels and layers for visibility and styling [D-CAT][BC]. BIS does **not** prescribe a level-to-category mapping.
- Each data-writing application keeps its categories in its own channel, so names never collide across apps [D-CAT]. Categories are DefinitionElements scoped to their DefinitionModel. Domain-specific categories go in their own DefinitionModel [D-CAT].
- Connector definitions should be User-rank in the job's Subject-specific "Definitions" partition [D-ODE].
- Consider domain-proposed SpatialCategories where applicable, but categories are user-controlled and *not* semantic [D-CAT][D-CLASS].
- Observed: converted DGN elements show category names such as `Uncategorized` [X-SNAP].

### 6.7 Schema evolution across connector runs

- **Additive only:** classes and properties cannot be removed or significantly redefined. New versions are minor bumps [D-CUST][L-CONN][X-PCF].
- A BIS minor upgrade may introduce property-name clashes with dynamic subclasses [D-CUST].
- **Re-targeting:** when a shared BIS class for a concept appears, inject it as the base class of the connector's existing class. This is why an intermediate connector base class is discouraged [D-SDG]. Note that [D-VER] treats any base-class change as a major change (§9).
- **Reclassifying elements** (Generic → specific) means delete and recreate. Reuse the ElementId and preserve Code and FederationGuid [D-SDG].
- **Change detection:** use the ExternalSourceAspect Version or Checksum. Detect deletions by omission, preferably file-based. "Unmap" removes a source's data. Changeset groups are available [L-WAC].
- **Coordinates:** all stored in metres. Respect an existing GCS and global origin, or initialise them from the source [L-WAC].
- **Editing channels:** shared channel by default since connector-framework 2.1; channel keys are expected to become the default [L-WAC].

### 6.8 Anti-patterns collected from the sources

1. Generating CodeValues from GUIDs, hashes or counters, or making them hundreds of characters long [D-CODE][L-WAC].
2. Dropping CodeValues because some are duplicates [L-WAC].
3. Using CodeValue to control display text; use UserLabel [L-CONN].
4. Repository-wide code scope in connectors [D-CODE].
5. Subclassing `PhysicalModel` or any other model listed in BIS-607, or adding properties to Model subclasses (BIS-609) [D-VAL][D-PHIER].
6. Implementing both `IParentElement` and `ISubModeledElement` (BIS-606) [D-VAL][D-EL].
7. Modelling the same real-world object as more than one PhysicalElement [D-PHYS].
8. Putting more than one discipline or responsible party in one Model [D-SRPP].
9. Using dynamic schemas for fixed concepts, or adding an intermediate connector base class for concepts missing from BIS [D-SDG].
10. Removing or renaming classes or properties of a dynamic schema between runs [D-CUST][D-VER].
11. Relying on Category for semantics [D-CLASS][D-CAT].
12. Instantiating `SpatialLocationPartition`, or putting PhysicalElements in a SpatialLocationModel [BC-R][D-3D].
13. Storing analysis results [D-MP][D-ANLYT].
14. Writing outside the connector's job subject, for example into the DictionaryModel, apart from creating the job subject itself [L-WAC].
15. Pointing ExternalSourceAspect.Scope at partitions instead of RepositoryLinks [L-WAC].
16. Leaving ExternalSourceAspect Identifier, Kind or Scope null [D-PROV].
17. Putting user-visible data in JsonProperties [D-EL][D-PROP].
18. Property-bearing mixins for frequently filtered data [D-MIX].
19. `long` properties instead of navigation properties, unsupported extended types, or "holding" strength (BIS-1300, BIS-1302, BIS-1500) [D-VAL].
20. Writing deprecated classes (§2.12).
21. Analysis or "results" data and connector "rules" mixed into domain classes. Authoring helpers and recipe data belong in Application-layer schemas [D-SDG].

---

## 7. Schemas generated by Bentley's connectors: what is public

### 7.1 Static connector schemas (Application layer)

**`Connector`** (connector, 01.01.00; carries the DynamicSchema CA; NotForProduction) [CONN][CONN-R]:
- `RuleDefinition` [S] (DefinitionElement {Description, Violation, Solutions, Required}). Its CodeValue is the unique rule name, prefixed `${domain}/${name}`.
- `RuleViolation` (InformationRecordElement {Rule, UpdateId, Repository→RepositoryLink, Source→ExternalSource, Kind, Identifier, Properties, Details}).
- A rule violation refers to the **external** entity (Repository, Kind, Identifier), not to an iModel element, because the entity may not have been converted at all.

**`DgnConnector`** (dgnconn, 01.00.00 Production): the only content is the CA `ItemTypeCalculatedProperty {Expression}`, which marks a property that came from a DGN item-type calculated property. Remarks: *"captures concepts exclusive to such format, with no direct mapping into BisCore concepts"* [DGNC][DGNC-R]. It implies item-type properties are carried as EC properties on generated classes, keeping the expression as metadata.

**`DgnV8OpenRoadsDesigner`** (orddgn; released 01.00.00–02.00.01; WIP marked **Deprecated** and `HiddenSchema`) [ORD]:
- Sealed `ElementUniqueAspect`s owned by `bis:GeometricElement3d`: CorridorSurfaceAspect, FeatureAspect, TemplateDropAspect, StationRangeAspect, SuperelevationAspect, CorridorAspect, VolumetricQuantityAspect, LinearQuantityAspect, DiscreteQuantityAspect, AlignmentAspect.
- A link-table relationship `GeometricElementBoundsContentForSheet`.
- This is "civil semantics as aspects on generic geometry".

**CIF family** (CifCommon 01.00.16, CifRoads 01.00.04, CifRail 01.00.12, CifBridge, CifSubsurface, …; Production) [CIF]:
- About 96 aspect classes in CifCommon under the abstract `CivilPresentation` (ElementUniqueAspect). Examples: CorridorAspect, FeatureAspect, LinearElementAspect, ClothoidAspect, StationEquationAspect, DTMEntityAspect, MeshSurfaceEntity*Aspect.
- CifRoads: SuperElevation, SpeedLimit and SightVisibility aspects. CifRail: about 61 aspects (cant, turnouts, sleepers, …).
- Remarks: *"The aspect classes … aim to provide a similar user-experience with respect to properties in the source CIF application… This schema should not be considered the aligned BIS schema for any particular domain."*
- Navisworks' NwdStatic carries the same disclaimer [NWD-R].

### 7.2 Dynamic schemas (per iModel)

**`DgnCustomItemTypes_<ItemTypeLibraryName>`**, the DGN item-type mapping, observed publicly:
- In the "Stadium" sample, `Generic.PhysicalObject` elements own multi-aspects of class `DgnCustomItemTypes_SS_Steel_Structure.GlassElementAspect`, alongside `bis.ExternalSourceAspect`. The tutorial notes *"Generic.PhysicalObject has the most aspect classes"* [X-CK].
- In the iTwin.js SmartHouse sample, `DgnCustomItemTypes_HouseSchema.SmartDevice` is queried with `Origin.X/Y/Z`. That makes it a GeometricElement3d subclass, i.e. an item type mapped to an **element class** rather than an aspect [X-SMART].
- So both patterns occur: item type → `<ItemType>ElementAspect` on a generic element, and item type → element subclass. The rule that picks one or the other is **not** documented publicly.

**`RevitDynamic`**: queried in the ECSQL docs as `[RevitDynamic].[Computer]` with a `Model` navigation property, i.e. per-category element classes in a dynamic schema [X-ECSQL]. It is registered in the inventory [R-INV] and named as an Application-layer dynamic schema [BISCA-R].

**`IFCDynamic`**: named only in BisCustomAttributes remarks as a dynamically generated connector schema [BISCA-R]. No public content found.

**`DwgAttributeDefinitions_block`** is registered as dynamic, released 01.00.00, author Don.Fu [R-INV]. The name suggests DWG block attribute definitions become per-block classes. The `DwgAttributeDefinitions` schema in imodel-native is a **synthetic performance-test fixture**, not real connector output: it defines its own `ElementMultiAspect` stand-in and generates N aspect classes [X-NATIVE].

**Other registered dynamic names:**
- `roads_shp_<sha1>` (a shapefile/GIS connector schema named after the file plus a hash);
- `SP3DReview`;
- `BuildingDataGroupWS` [R-INV].

**Legacy EC2 → EC3 conversion.** `ECv3ConversionAttributes` (`V2ToV3`) provides CAs such as `IsFlattened`, `OldDerivedClasses`, `RenamedPropertiesMapping`, `OldPersistenceUnit`, `DropClass`, `ResolvePropertyNameConflicts`, `ResolveClassNameConflicts`, `ForceAbstract`/`ForceSealed`/`ForceEntityClass` [V2V3]. `IsFlattened` is described as *"All base class properties were merged into each class"*, with `OldDerivedClasses` preserving polymorphism. That is the lossy end of converting V8 ECSchemas.

### 7.3 Good and lossy outputs in the evidence

**Good** (aligned with the guidance):
- Real domain classes, e.g. `rralign:Alignment` with LinearReferencing aspects, or `cvphys:Course` with Types and layer thickness on `LayeredStructureTypeComposesSubTypes` [RRA][CVP].
- Source-specific but stable concepts in an Application-layer schema, with dynamic classes deriving from it [D-SDG].
- User-defined properties in dynamic **aspects** [D-SDG].
- ExternalSourceAspect with a non-null Identifier, Kind and Scope (RepositoryLink) [D-PROV].
- UserLabel taken from the source name; CodeValue only for real identifiers [L-CONN][L-WAC].
- `ElementHasLinks` from partitions to RepositoryLinks [L-WAC].

**Lossy or weakly aligned** (in the sources' own words or by inference):
- Everything as `Generic:PhysicalObject` plus aspects. The schema says to use it only "when there is not enough context" [GEN]. The Stadium sample is dominated by it [X-CK].
- Application aspect schemas that "should not be considered the aligned BIS schema" [CIF][ORD].
- Empty Code and UserLabel on converted elements [X-SNAP].
- Flattened EC2 conversions [V2V3].
- Deprecated per-product schemas (`DgnV8OpenRoadsDesigner`, `OpenBridgeModelerCE`) [ORD][R-INV].

---

## 8. Implications for DgnKit (synthesis, not official guidance)

These are mappings that follow from the rules above. Each needs checking against Bentley connector output if parity matters.

1. **Repository skeleton:**
   - one job Subject under the root, plus one child Subject per DGN file, or per file-and-model when there are several models [L-WAC][D-IH];
   - a `RepositoryLink` per file and an `ExternalSource` per DGN model; reference attachments become `ExternalSourceAttachment`s carrying the transform [D-PROV];
   - `ElementHasLinks` from each partition to its RepositoryLink [L-WAC].
2. **3D design model** → `PhysicalPartition "Physical"` → `PhysicalModel`.
   - Elements that really are physical but unclassified → `generic:PhysicalObject`.
   - Annotation, text or "pure graphics" in 3D → `generic:Graphic3d` (GraphicalElement3d) in the same PhysicalModel [D-3D][GEN].
   - Clearly spatial items (boundaries, alignments, grids) → SpatialLocationElement subclasses in the PhysicalModel, *not* in a SpatialLocationPartition [BC-R].
3. **2D design model** → `Drawing` in a DocumentListModel, plus a `DrawingModel` holding `DrawingGraphic` (geometry) and `TextAnnotation2d` / `AnnotationElement2d` / `DimensionAnnotation2d*` where structured data exists. **Sheet model** → `Sheet` + `SheetModel` with `ViewAttachment`s [D-DS][BC].
4. **Levels** → `SpatialCategory` (3D) or `DrawingCategory` (2D), each with an automatic default SubCategory, in the job's Subject-specific `Definitions` DefinitionModel, User rank. Strip the disallowed characters `<>\/."?*|,='&` [D-CAT][D-ODE].
5. **Shared cells and blocks** → `GeometryPart` (DefinitionElement) referenced from GeometryStreams [BC]. Cells with semantic parts → parent/child via `IParentElement` subclasses, or flatten into one element [D-OME].
6. **Named groups** → `generic:Group` in a `GroupInformationPartition "Groups"` → `generic:GroupModel`, with members via `ElementGroupsMembers` [BC-R][GEN].
7. **Item types and tags** → aspects in a per-iModel dynamic schema, matching Bentley's `DgnCustomItemTypes_<lib>` naming if parity matters [X-CK]. Fixed DGN concepts go in a static Application-layer schema that the dynamic classes derive from [D-SDG]. Calculated item-type properties can carry `dgnconn:ItemTypeCalculatedProperty` [DGNC].
8. **Identity:** `ExternalSourceAspect{Identifier = V8 element id, Kind = "Element" (or similar), Scope = RepositoryLink, Source = ExternalSource, Version/Checksum}` [D-PROV][L-WAC]. CodeValue only for real identifiers (named cells, sheet numbers, alignment names). UserLabel from the source name [L-CONN].
9. **OpenRoads data:** target `rralign` and `lr` rather than aspects where geometry can be decoded. Note RoadRailAlignment 02.00.04 is FieldTesting; 02.00.03 is Production with RoadRailUnits [§1.8].
10. **Units and coordinates:** metres; apply any GCS or global origin [L-WAC]. Plan-only models can set `GeometricModel3d.IsPlanProjection` [BC].

---

## 9. Not verified, gaps and contradictions

1. **Current DGN connector output.** The only concrete evidence is an old test iModel snapshot (DgnV8Bridge) [X-PRES][X-SNAP] and the Stadium tutorial [X-CK]. The exact present-day subject, partition and model layout and naming, the level → category mapping (category per level? per file?) and where categories live were **not verified**. The `.ibim` test assets in itwinjs-core (e.g. `Properties_60InstancesWithUrl2.ibim`) could be opened with SQLite to confirm, but binary files were not downloaded.
2. **When an item type becomes an aspect and when it becomes an element class** in `DgnCustomItemTypes_*`: both are observed (§7.2); the rule is not public.
3. **IFCDynamic, RevitDynamic and DwgAttributeDefinitions_block contents:** names only; no schema XML is public. The `DwgAttributeDefinitions` in imodel-native is a test fixture.
4. **Which model holds a connector's RepositoryLinks and ExternalSources** (the job LinkModel, `BisCore.RealityDataSources`, or another InformationModel) is not stated in the docs read. The connector-framework source (github.com/iTwin/connector-framework) was not inspected.
5. **Generations versus actual read versions** disagree (§0.3 item 2).
6. **Re-basing a connector class onto a new domain class** is recommended by [D-SDG], but [D-VER] says *"currently any change in parent class is a major schema upgrade"*. In practice this probably needs a read-version bump of the connector schema, or adding the base in the middle of a hierarchy, which [D-VER] allows as a minor change. Not verified how Bentley does it.
7. **[D-CUST] versus [D-PROP] on per-instance custom data:** [D-CUST] says "properties which vary per instance should be modeled using Json"; [D-PROP] and [D-SDG] route user-defined properties to Aspects. I treat the newer, more specific [D-PROP]/[D-SDG] as authoritative.
8. **The consequence of a BIS property-name clash with a dynamic subclass** is literally a blank ("?????????????") in [D-CUST].
9. **RoadRailPhysical (2020) versus RoadSpatial (2025)** semantic overlap: no reconciliation guidance found.
10. **itwinjs.org URLs** were derived from the repo path convention, not each fetched. The repo files themselves were read.
11. **Unread for this brief:** ECDbMeta/ECDbSchemaPolicies details, Units and Formats contents, Profiles, NetworkTopology, Plant and Substation schemas in depth, and IFC 4.3 equivalence beyond what the remarks cite.
