# The iModel (.bim) file format on disk — source-verified brief

Prepared for the DgnKit → iModel bridge design (C# reader of MicroStation V7/V8 DGN, no Bentley runtime).
Date of research: 2026-11-13.

---

## 0. Sources, citation scheme, method

**Ground truth = source code at these exact commits** (all claims cite a file + line):

| Tag | Repository @ commit | Base URL for citations |
|---|---|---|
| `[N]` | iTwin/imodel-native @ `5d613e6a096ddba1e476e56326452312cd9bc174` (main, 2026-11-13) | `https://github.com/iTwin/imodel-native/blob/5d613e6a096ddba1e476e56326452312cd9bc174/<path>#L<line>` |
| `[C]` | iTwin/itwinjs-core @ `79aa53740927653f6fbb21fbd86b51f76c4d75db` (master, 2026-11-13) | `https://github.com/iTwin/itwinjs-core/blob/79aa53740927653f6fbb21fbd86b51f76c4d75db/<path>#L<line>` |
| `[B]` | iTwin/bis-schemas @ `60478e86daf1ee900c2c1350bddf02fcbac2c2db` (master) | `https://github.com/iTwin/bis-schemas/blob/60478e86daf1ee900c2c1350bddf02fcbac2c2db/<path>#L<line>` |
| `[S]` | Observed in a real sample file (see §9) | — |

Citations are written as `[N]iModelCore/BeSQLite/BeSQLite.cpp:2717`. Docs under `[C]docs/learning/...` are the sources of the pages published at `https://www.itwinjs.org/learning/...` (same path, lower-case, no `.md`), e.g. `[C]docs/learning/common/GeometryStream.md` = https://www.itwinjs.org/learning/common/geometrystream/.

Note: `[B]` master currently carries BisCore **01.00.28** marked `NotForProduction` ([B]Domains/0-Core/BisCore.ecschema.xml:6-19). The BisCore actually embedded in a new file is whatever ships with the `@bentley/imodeljs-native` addon; class/property *names* and mapping CAs quoted here are stable across 1.0.x, but don't rely on the exact version number.

**Sample files inspected** (downloaded into fresh empty directories, opened read-only):
- `core/backend/src/test/assets/test.bim` from `[C]` (git blob `d568db9e…`, 1,216,512 bytes) → `scratchpad/imodel-sample-dl/test.bim`
- `core/backend/src/test/assets/test_ec_4003.bim` from `[C]` (git blob `5b416558…`, 4,235,264 bytes) → `scratchpad/imodel-sample-dl2/test_ec_4003.bim`

Both are the *same* iModel (same DbGuid, created 2017 by the DgnV8 converter) at two profile levels; `test_ec_4003.bim` was upgraded (ECDb 4.0.0.3 / DgnDb 2.0.0.5) and has 5 Txns. Python is **not installed** on this machine, so instead of `python -I -c "import sqlite3"` I used Windows' built-in `C:\Windows\System32\winsqlite3.dll` (SQLite 3.53.4) via a small P/Invoke helper, opening with `file:...?immutable=1` + `SQLITE_OPEN_READONLY` so nothing (not even `-wal`/`-shm`) was written. Nothing from the downloaded repos was executed. Raw query output is saved in `scratchpad/research/q1.out.txt`, `q1b.out.txt`, `q2.out.txt`, `q3.out.txt`.

---

## 1. Physical container: SQLite specifics

### 1.1 File-level SQLite settings

| Item | Value | Source |
|---|---|---|
| Extension | `.bim` is the default extension supplied by the native code (`s_dotBim = L".bim"`, used by `CreateNewIModel`/open) | [N]iModelCore/iModelPlatform/DgnCore/DgnDb.cpp:13, 672, 722 |
| `.ibim` | No code path treats `.ibim` specially; it only appears in test-asset names and an old comment about extracting an "ibim" from a package. Treat `.ibim` as a legacy name for the same format. | [N]iModelCore/iModelPlatform/PublicAPI/DgnPlatform/DgnProperties.h:68 (comment); `gh search code ibim` hits only tests. *(Content of an .ibim not inspected.)* |
| Page size | `PRAGMA page_size` from `CreateParams.m_pagesize`, default `PAGESIZE_4K` (4096); iTwin.js `createIModel` does not override it. `Db::Vacuum(newPageSize)` can change it later. | [N]iModelCore/BeSQLite/PublicAPI/BeSQLite/BeSQLite.h:2516-2526, 2657; [N]iModelCore/BeSQLite/BeSQLite.cpp:2717, 6842-6847; [N]iModelJsNodeAddon/JsInterop.cpp:605-638; [S] page_size=4096 |
| Encoding | UTF-8 (`PRAGMA encoding="UTF-8"`) | BeSQLite.cpp:2717; [S] |
| `user_version` | `DbUserVersion = 10` | BeSQLite.h:357; BeSQLite.cpp:2717-2719; [S] =10 |
| `application_id` | `APPLICATION_ID_BeSQLiteDb = 'BeDb'` (multi-char literal) → **0x42654462 = 1113932898**; DgnDb does not change it | BeSQLite.h:2645, 2660; [S] `PRAGMA application_id` = 1113932898, header bytes 68-71 = `42 65 44 62` |
| Journal mode | **WAL**: "iModels should always use WAL journal mode" → `EnableWalMode(true)` right after `CreateNewDb` | DgnDb.cpp:737-743; BeSQLite.cpp:3825; [S] header bytes 18/19 = 2/2 (WAL) |
| Locking | `PRAGMA locking_mode` EXCLUSIVE or NORMAL depending on `DefaultTxn` param | BeSQLite.cpp:2717-2719 |
| Default txn | BeSQLite keeps an always-open "default transaction"; `SaveChanges()` commits it | BeSQLite.cpp:2714, 2766-2767 |
| sqlite_stat1 | Created by `analyze;` then emptied at creation ("create sqlite_stat1 so it can be tracked") | BeSQLite.cpp:2751-2759; [S] (empty in 4003 file) |
| Bloom filter | Disabled per connection (SQLite ≥3.38 issue) — runtime only | BeSQLite.cpp:2704-2707 |
| Page/file compression | **None at SQLite page level.** Compression is per value: zlib for `be_Prop` blobs >100 bytes when it shrinks them; Snappy for GeometryStream and `dgn_Txns.Change`; LZMA2 for changeset files. (A separate `LzmaUtility::CompressDb` exists for compressing a whole Db into an archive; it is not the .bim format.) | BeSQLite.cpp:2113-2135 (zlib in be_Prop); [N]…/DgnCore/DgnElement.cpp:3808-3840 (Snappy geom); [N]…/DgnCore/TxnManager.cpp:655-697 (Snappy txns); [N]iModelCore/BeSQLite/ChangesetFile.cpp:92-131 (LZMA2); BeSQLite.h:3593-3600 (LzmaUtility) |
| SQLite extensions required to *read everything* | R*Tree (`dgn_SpatialIndex`), FTS5 (`dgn_fts_idx`), session/changeset (for Txns) | §2.4 |

### 1.2 Where the identities and versions live

| Datum | Storage | Encoding | Source |
|---|---|---|---|
| **iModelId** (= "DbGuid") | `be_Prop(Namespace='be_Db', Name='DbGuid', Id=0, SubId=0).Data` | 16 raw bytes. **Byte order = string order**: `BeGuid::ToString` prints `b[0]..b[15]` straight (so in .NET use `new Guid(bytes, bigEndian:true)` / RFC order, **not** `new Guid(byte[])`) | BeSQLite.cpp:2437-2449 (SaveBeDbGuid), 352-360 (ToString); BeSQLite.h:3580-3584; `getIModelId`/`setIModelId` → `GetDbGuid`/`ChangeDbGuid`: [N]iModelJsNodeAddon/IModelJsNative.cpp:2972-2985; [S] `dbd4826b4f72423cb4faff8205f2bae7` |
| **iTwinId** (project) | `be_Prop('be_Db','ProjectGuid')` | 16 raw bytes (`SaveProjectGuid`); StandaloneDb sets it to the empty GUID | BeSQLite.cpp:2623-2637; IModelJsNative.cpp:2987-2993; [C]core/backend/src/IModelDb.ts:4958, 5031 |
| **BriefcaseId** | `be_Local(Name='be_repositoryid').Val` | BLOB of 8 bytes = little-endian int64 | BeSQLite.cpp:47, 2455-2471; [S] `0000000000000000` (=0) |
| **Current/parent changeset** | `be_Local('ParentChangeSetId')` = 40-hex SHA1 string; `be_Local('parentChangeset')` = JSON `{"id":…, "index":n}` | text | [N]…/DgnCore/ChangesetTxns.cpp:14-16, 777-797, 806-825; read by `getCurrentChangeset`: IModelJsNative.cpp:2960-2970 |
| BeSQLite profile | `be_Prop('be_Db','SchemaVersion').StrData` = JSON `{"major":3,"minor":1,"sub1":0,"sub2":2}` (current 3.1.0.2, oldest supported 3.1.0.0) | JSON text | BeSQLite.h:368-379; [S] |
| ECDb profile | `be_Prop('ec_Db','SchemaVersion')` and `('ec_Db','InitialSchemaVersion')`; current **4.0.0.5**, minimum upgradable 4.0.0.0 | JSON text | [N]iModelCore/ECDb/ECDb/ProfileManager.h:12, 35-36; ProfileManager.cpp:177-192; [N]iModelCore/ECDb/PublicAPI/ECDb/ECDb.h:337-340; [S] 4.0.0.3 |
| DgnDb profile | `be_Prop('dgn_Db','SchemaVersion')`; current **2.0.0.9**, supported ≥2.0 | JSON text | [N]iModelCore/iModelPlatform/PublicAPI/DgnPlatform/DgnDb.h:63-70; DgnDbSchema.cpp:441-445; [S] 2.0.0.5 |
| Creation info | `be_Db/CreationDate`, `be_Db/BeSQLiteBuild`, `dgn_Db/LastEditor` (="iTwin.js" at creation), `dgn_Db/Client` | text | BeSQLite.h:3586-3590; DgnDbSchema.cpp:504-512; [S] |

Profile-version semantics on open (`Db::CheckProfileVersion`): file *major* newer → cannot open; *minor* newer → read-only; older major → cannot open; older minor → read-only unless upgraded; same major.minor, different sub → read/write ([N]iModelCore/BeSQLite/BeSQLite.cpp:3952-3997). DgnDb merges BeSQLite+ECDb+DgnDb states ([N]…/DgnCore/DgnDbSchema.cpp:535-548).

---

## 2. Table inventory

What a **fresh** iModel contains is produced by four layers in this order: BeSQLite `CreateNewDb` → ECDb profile → DgnDb tables → domain schema import (BisCore + Generic) → bootstrap rows ([N]…/DgnCore/DgnDb.cpp:719-750; DgnDbSchema.cpp:359-439, 504-525).

### 2.1 BeSQLite layer

| Table | DDL / key columns | Purpose | Source |
|---|---|---|---|
| `be_Prop` | `Namespace CHAR NOT NULL, Name CHAR NOT NULL, Id INT NOT NULL, SubId INT NOT NULL, TxnMode INT NOT NULL, StrData CHAR, RawSize INT, Data BLOB, PRIMARY KEY(Namespace,Name,Id,SubId)` | Name/id/value property store. If `Data` was zlib-compressed, `RawSize` = uncompressed size, else NULL. **Tracked in changesets** (has PK). | BeSQLite.cpp:2082-2140 |
| `be_Local` | `Name CHAR NOT NULL COLLATE NOCASE UNIQUE, Val BLOB` | Briefcase-local values; "purposely has no primary key so it won't be tracked / merged" | BeSQLite.cpp:2724-2730 |
| `be_EmbedFile` | `Id INTEGER PRIMARY KEY, Name CHAR NOT NULL COLLATE NOCASE, Descr, Type, Size INT, Chunk INT, LastModified TIMESTAMP, UNIQUE(Name,Type)` + trigger `delete_embeddedFiles` (deletes the chunks from be_Prop `be_Db/EmbdBlob`) | Embedded files stored as chunked blobs in be_Prop | BeSQLite.cpp:5913-5925; BeSQLite.h:3562-3563, 3585 |
| `sqlite_stat1` | (SQLite) | created empty | BeSQLite.cpp:2751-2759 |

Known **be_Prop namespaces**:

| Namespace | Names | Source |
|---|---|---|
| `be_Db` | `DbGuid`, `SchemaVersion`, `ProjectGuid`, `EmbdBlob`, `CreationDate`, `ExpirationDate`, `BeSQLiteBuild` | BeSQLite.h:3562-3591 |
| `ec_Db` | `SchemaVersion`, `InitialSchemaVersion` | ProfileManager.h:12, 35-36 |
| `dgn_Db` | `SchemaVersion`, `Name`, `Description`, `Client`, `SeedProjectId`, `ProjectType`, `LastEditor`, `Extents`, `Units`, `DgnGCS`, `DgnGCSVerticalCRS`, `LastSchemaUpgradeInPhase` | [N]…/PublicAPI/DgnPlatform/DgnProperties.h:13, 24-45 |
| `dgn_View` | `DefaultView`, `Thumbnail` (Id = view element id) | DgnProperties.h:11, 76-87; [S] Thumbnail Id=52 PNG |
| `dgn_LStyle` | `CompoundV1`, `LineCodeV1`, `LinePointV1`, `PointSymV1`, `RasterComponentV1`, `RasterImageV1` (line-style components) | DgnProperties.h:10, 92-107 |
| `dgn_Font` | `EmbeddedFaceData` (Id = n, embedded font file bytes; StrData = face JSON) | [N]…/DgnCore/FontManager.cpp:7-8, 152-200 |
| `pkge_dgnDb` | legacy packaged-db properties | DgnProperties.h:14, 50-71 |

Known **be_Local** names: `be_repositoryid`, `bis_elementidsequence`, `ec_instanceidsequence`, `ParentChangeSetId`, `parentChangeset`, `CurrentChangeSetEndTxnId`, `ReversedChangeSetId`, `pull_merge_conf`, `fileBasedTxns`, `StandaloneEdit`, `NoLocking`, `DebugAllowFkViolations` (BeSQLite.cpp:47; DgnDb.cpp:33; [N]iModelCore/ECDb/ECDb/ECDbImpl.cpp:54; ChangesetTxns.cpp:14-16, 520, 770-771; TxnManager.cpp:101, 303; [C]core/backend/src/IModelDb.ts:323-326). [S] has `be_repositoryid`, `bis_elementidsequence`, `ec_instanceidsequence`, `StandaloneEdit` = `{ "txns": true }`.

### 2.2 ECDb profile ("ec_*") tables — schema metadata and the O/R map

Created by `ProfileManager::CreateProfileTables` ([N]iModelCore/ECDb/ECDb/ProfileManager.cpp:221-590; units DDL in [N]iModelCore/ECDb/ECDb/ProfileUpgrader.h:11-67; names in [N]iModelCore/ECDb/ECDb/SqlNames.h:17-41). Profile upgraders 4.0.0.3-4.0.0.5 change no tables ([N]iModelCore/ECDb/ECDb/ProfileUpgrader.cpp:16-35), so the 4.0.0.3 sample shows the current table shapes.

| Table | Key columns | Holds |
|---|---|---|
| `ec_Schema` | Id, Name (unique, NOCASE), DisplayLabel, Description, Alias (unique), VersionDigit1..3, OriginalECXmlVersionMajor/Minor | One row per ECSchema |
| `ec_SchemaReference` | Id, SchemaId, ReferencedSchemaId | Schema references |
| `ec_Class` | Id, SchemaId, Name, DisplayLabel, Description, **Type** (0 Entity,1 Relationship,2 Struct,3 CustomAttribute), **Modifier** (0 None,1 Abstract,2 Sealed), RelationshipStrength, RelationshipStrengthDirection, CustomAttributeContainerType | Classes. **`ec_Class.Id` is the `ECClassId` stored in every data row** |
| `ec_ClassHasBaseClasses` | ClassId, BaseClassId, Ordinal | Direct base classes / mixins |
| `ec_Enumeration` | SchemaId, Name, UnderlyingPrimitiveType, IsStrict, EnumValues (JSON) | Enumerations |
| `ec_KindOfQuantity` | SchemaId, Name, PersistenceUnit, RelativeError, PresentationUnits | KOQs |
| `ec_UnitSystem`, `ec_Phenomenon`, `ec_Unit`, `ec_Format`, `ec_FormatCompositeUnit` | (see ProfileUpgrader.h:11-67) | Units schema content |
| `ec_PropertyCategory` | SchemaId, Name, Priority | Property categories |
| `ec_Property` | ClassId, Name, IsReadonly, Ordinal, **Kind** (0 Primitive,1 Struct,2 PrimArray,3 StructArray,4 Navigation), PrimitiveType, ExtendedTypeName, EnumerationId, StructClassId, KindOfQuantityId, CategoryId, ArrayMin/MaxOccurs, NavigationRelationshipClassId, NavigationDirection | Local properties of each class |
| `ec_PropertyPath` | RootPropertyId, AccessString | Access strings (e.g. `Origin.X`, `Model.Id`) |
| `ec_RelationshipConstraint` / `ec_RelationshipConstraintClass` | RelationshipClassId, RelationshipEnd (0 Source/1 Target), multiplicity, IsPolymorphic, RoleLabel, AbstractConstraintClassId / ConstraintId, ClassId | Relationship endpoints |
| `ec_CustomAttribute` | ClassId (CA class), ContainerId, ContainerType, Ordinal, **Instance (ECXML text)** | Custom-attribute instances on schemas/classes/properties |
| `ec_ClassMap` | ClassId PK, **MapStrategy**, ShareColumnsMode, MaxSharedColumnsBeforeOverflow, JoinedTableInfo | Resolved mapping per class (§3) |
| `ec_PropertyMap` | ClassId, PropertyPathId, ColumnId | **property-access-string → physical column**, per class |
| `ec_Table` | Id, ParentTableId, Name (unique), **Type** (0 Primary,1 Joined,2 Existing,3 Overflow,4 Virtual), ExclusiveRootClassId, UpdatableViewName (unused) | Tables known to ECDb (incl. virtual ones) |
| `ec_Column` | TableId, Name, **Type** (0 Any,1 Bool,2 Blob,3 TimeStamp,4 Real,5 Integer,6 Text), **IsVirtual**, Ordinal, NotNull/Unique/Check/Default/Collation constraints, OrdinalInPrimaryKey, **ColumnKind** (0 Default,1 ECInstanceId,2 ECClassId,4 SharedData) | Columns (incl. virtual ones that do not exist in SQLite) |
| `ec_Index`, `ec_IndexColumn` | Name, TableId, IsUnique, AddNotNullWhereExp, IsAutoGenerated, ClassId, AppliesToSubclassesIfPartial / IndexId, ColumnId, Ordinal | Index definitions ECDb owns |
| `ec_cache_ClassHasTables` | ClassId, TableId | Denormalized cache: which tables hold a class |
| `ec_cache_ClassHierarchy` | ClassId, BaseClassId | Transitive closure of inheritance |

Enum values: [N]iModelCore/ECDb/ECDb/SqlNames.h:79-140; [N]iModelCore/ECDb/ECDb/DbSchema.h:82-100, 401-406.

ECDb's own system schemas are imported at profile creation (`ProfileSchemaUpgrader::ImportProfileSchemas`, ProfileManager.cpp:44): [S] shows ECDbMap, ECDbSystem, ECDbMeta, CoreCustomAttributes, ECDbFileInfo, ECDbSchemaPolicies. Of these only **ECDbFileInfo** creates real tables: `ecdbf_ExternalFileInfo` and `ecdbf_FileInfoOwnership`; ECDbMeta's classes are mapped onto the `ec_*` tables themselves (ec_Table Type 2 "Existing"), plus virtual `meta_*` link tables ([S] ec_Table rows 1-28).

### 2.3 DgnDb ("dgn_*") tables

Created by `DgnDb::CreateDgnDbTables` ([N]iModelCore/iModelPlatform/DgnCore/DgnDbSchema.cpp:359-411):

| Table | DDL | Purpose |
|---|---|---|
| `dgn_Domain` | `Name TEXT PK NOCASE, Description TEXT, Version INTEGER` | Registered domains ([S]: BisCore, Functional, Generic, …) |
| `dgn_Handler` | `ClassId INTEGER PK, Domain TEXT REFERENCES dgn_Domain, Name TEXT, Permissions INTEGER, UNIQUE(Domain,Name)` | ECClassId → C++ handler registry ([S] 116 rows) |
| `dgn_Txns` | `Id INTEGER PK, Deleted BOOLEAN, Grouped BOOLEAN, Operation TEXT, IsSchemaChange BOOLEAN, Time TIMESTAMP DEFAULT(julianday('now')), Change BLOB` | Local undoable transactions (§7) |
| `dgn_Font` | `Id INTEGER PK, Type INT, Name CHAR NOT NULL NOCASE, Metadata BLOB, UNIQUE(Type,Name)` | Font table: Type 1 TrueType, 2 Rsc, 3 Shx ([N]…/DgnCore/DbFont.cpp:58-70, 125-136; [S] Arial/Font0/ShxFont0/Calibri) |
| `dgn_Rebase` | `Id INTEGER PRIMARY KEY AUTOINCREMENT, Rebase BLOB` (⇒ also `sqlite_sequence`) | Rebase data for pull/merge. *Absent in both samples (created before this table existed).* (DgnDbSchema.cpp:351-354, 384) |
| `dgn_SpatialIndex` | `CREATE VIRTUAL TABLE … USING rtree(ElementId,MinX,MaxX,MinY,MaxY,MinZ,MaxZ)` + shadow tables `_node/_parent/_rowid` | World-space AABB of each 3d element with `InSpatialIndex=1` (R*Tree stores 32-bit floats, rounded outward — [S] values) |
| `dgn_fts_content` + `dgn_fts_idx` (fts5, external content) + shadow tables `_config/_data/_docsize/_idx` + triggers `dgn_fts_ai/ad/au` | `dgn_fts_content(Type NOT NULL, Id INTEGER NOT NULL, Text NOT NULL, PRIMARY KEY(Type,Id))` | Searchable text ([N]…/DgnCore/SearchableText.cpp:10-11, 267-287) |

Triggers on `bis_GeometricElement3d` maintain the R-tree: `dgn_prjrange_del`, `dgn_rtree_ins`, `dgn_rtree_upd`, `dgn_rtree_upd1` (DgnDbSchema.cpp:182-206, 397-406). They call **custom SQL functions** `DGN_bbox`, `DGN_point`, `DGN_angles`, `DGN_placement`, `DGN_placement_aabb`, `DGN_bbox_value`, registered only by the native platform when a DgnDb is opened ([N]…/DgnCore/DgnSqlFuncs.cpp:810-850). ECDb also creates `bis_Element_CurrentTimeStamp` (sets `LastMod=julianday('now')` on update) ([S] sqlite_master).

Not in the .bim: the Txn helper tables are **temp** tables `temp.txn_Elements`, `txn_Depend`, `txn_Models`, `txn_RelationshipLinkTables` ([N]…/PublicAPI/DgnPlatform/TxnManager.h:17-22); the lock cache is a separate SQLite file `<base>-locks` ([C]core/backend/src/internal/ServerBasedLocks.ts:50-74); file-based txns live in `txns/<hex>.txn` beside the .bim ([N]…/DgnCore/TxnManager.cpp:323-338); changesets are `<base>.changeset` files ([N]…/DgnCore/ChangesetTxns.cpp:1103-1156).

### 2.4 BIS data tables (BisCore + Generic)

A new iModel imports only the **required** domains, BisCore and Generic; Functional is registered as optional and is *not* imported unless asked ([N]iModelCore/iModelPlatform/DgnCore/DgnCore.cpp:257-258; [N]iModelJsNodeAddon/JsInterop.cpp:448-451; [N]…/DgnCore/DgnDomain.cpp:517-531). The sample also has Functional/Raster/PointCloud/… because the 2017 converter imported them.

Physical tables produced by BisCore's mapping custom attributes ([S] test_ec_4003.bim, verified with `PRAGMA table_info` and `sqlite_master.sql`):

| Table | Physical columns ([S]) | Maps |
|---|---|---|
| `bis_Element` | `Id INTEGER PK, ECClassId NOT NULL, ModelId NOT NULL FK→bis_Model, LastMod TIMESTAMP NOT NULL DEFAULT(julianday('now')), CodeSpecId NOT NULL FK→bis_CodeSpec, CodeScopeId NOT NULL FK→bis_Element, CodeValue TEXT NOCASE, UserLabel TEXT NOCASE, ParentId FK→bis_Element, ParentRelECClassId, FederationGuid BLOB UNIQUE, JsonProperties TEXT` | Root of all elements (TablePerHierarchy). Indexes: `ix_bis_Element_Code` UNIQUE(CodeSpecId,CodeScopeId,CodeValue), `ix_bis_Element_FederationGuid` UNIQUE WHERE NOT NULL, `ix_bis_Element_UserLabel`, `ix_bis_Element_ecclassid`, FK indexes ([B]…/BisCore.ecschema.xml:549-700) |
| `bis_GeometricElement3d` | `ElementId PK FK→bis_Element ON DELETE CASCADE, ECClassId, CategoryId NOT NULL FK, InSpatialIndex BOOLEAN, Origin_X/Y/Z REAL, Yaw, Pitch, Roll REAL, BBoxLow_X/Y/Z, BBoxHigh_X/Y/Z REAL, GeometryStream BLOB, TypeDefinitionId FK ON DELETE SET NULL, TypeDefinitionRelECClassId, js1…jsN` | Joined table for all 3d geometric elements |
| `bis_GeometricElement2d` | `ElementId, ECClassId, CategoryId, Origin_X/Y, Rotation, BBoxLow_X/Y, BBoxHigh_X/Y, GeometryStream, TypeDefinitionId, TypeDefinitionRelECClassId, js1…` | Joined table for 2d (drawing/sheet) elements |
| `bis_DefinitionElement` | `ElementId, ECClassId, IsPrivate, js1…js19` | Joined table: categories, subcategories, GeometryPart, styles, materials, views … |
| `bis_InformationPartitionElement` | `ElementId, ECClassId, Description` | Partitions |
| `bis_InformationReferenceElement`, `bis_InformationRecordElement`, `bis_Document`, `bis_DriverBundleElement`, `bis_DocumentCarrier` | `ElementId, ECClassId, js…` | Other joined tables of InformationContentElement / InformationCarrierElement |
| `bis_Model` | `Id PK, ECClassId, ParentModelId FK→bis_Model, ModeledElementId FK→bis_Element, ModeledElementRelECClassId, IsPrivate, IsTemplate, JsonProperties, LastMod, ps1…` | All models (TPH, shared cols for subclasses: e.g. newer `GeometricModel.GeometryGuid`) |
| `bis_CodeSpec` | `Id INTEGER PK, Name TEXT UNIQUE NOCASE, JsonProperties TEXT` | CodeSpecs ([S] 33 rows) |
| `bis_ElementUniqueAspect` | `Id PK, ECClassId, ElementId NOT NULL FK ON DELETE CASCADE, ElementRelECClassId NOT NULL, ps1…` | All unique aspects |
| `bis_ElementMultiAspect` | `Id PK, ECClassId, ElementId NOT NULL FK CASCADE, ElementRelECClassId NOT NULL, ps1…ps7…` | All multi-aspects (e.g. ExternalSourceAspect) |
| `bis_ElementRefersToElements` | `Id PK, ECClassId, SourceId, TargetId, ps1…` | All link-table Element→Element relationships (TPH) |
| `bis_ElementDrivesElement` | `Id, ECClassId, SourceId, TargetId, Status, Priority` | Dependency relationships |
| `bis_ModelSelectorRefersToModels` | `Id, SourceId, TargetId` | Sealed link table (OwnTable → no ECClassId column) |
| `bis_*_Overflow` | `ElementId/Id PK, ECClassId NOT NULL, os1…` | Created on demand when a hierarchy exceeds its shared-column budget ([N]iModelCore/ECDb/ECDb/DbMappingManager.cpp:2150-2200) — none in the sample |

Row counts in the sample: §9.

### 2.5 Bootstrap content of every new iModel

`DgnDb::OnBisCoreSchemaImported` ([N]…/DgnCore/DgnDbSchema.cpp:418-439) creates:

| Id | Row(s) | Source |
|---|---|---|
| CodeSpec 1 | `bis:NullCodeSpec` (repository scope); then 31 more `bis:*` CodeSpecs (model scope or parent-element scope) | [N]…/DgnCore/CodeSpec.cpp:287-321; [S] 1..31 |
| **0x1** | `bis_Model` RepositoryModel (Id 1, ModeledElementId 1) + root `bis:Subject` element (Id 1, ModelId 1, CodeSpec bis:Subject, CodeScope 1, CodeValue = root-subject name) — inserted with deferred FKs because they reference each other | DgnDbSchema.cpp:309-345, 424-427; [N]…/PublicAPI/DgnPlatform/DgnElement.h:4112; DgnModel.h:704 |
| **0xe** (14) | `bis:LinkPartition` "BisCore.RealityDataSources" + private `bis:LinkModel` | DgnDbSchema.cpp:294-305; DgnElement.h:4118 |
| **0x10** (16) | `bis:DefinitionPartition` "BisCore.DictionaryModel" + `bis:DictionaryModel` | DgnDbSchema.cpp:277-290; DgnElement.h:4120; DgnModel.h:712 |
| be_Prop | `dgn_Db/SchemaVersion`, `CreationDate`, `LastEditor`, `Client`, `dgn_Db/Extents` (default extents if none), `dgn_Db/Units` (globalOrigin…) | DgnDbSchema.cpp:504-525 |
| be_Local | `bis_elementidsequence` reset (§4) | DgnDbSchema.cpp:431-438 |

Each partition/subject gets a fresh random FederationGuid (DgnDbSchema.cpp:249, 325). Models share the id of the element they model (DgnDbSchema.cpp:228-243).

---

## 3. ECDb mapping (EC classes → SQLite tables)

### 3.1 The vocabulary (ECDbMap custom attributes)

`[N]iModelCore/ECDb/ECDb/ECSchemas/ECDbMap.02.00.04.ecschema.xml:7-67`: `ClassMap{MapStrategy = OwnTable (default) | TablePerHierarchy | ExistingTable | NotMapped, TableName, ECInstanceIdColumn}`, `JoinedTablePerDirectSubclass`, `ShareColumns{ApplyToSubclassesOnly, MaxSharedColumnsBeforeOverflow}`, `DbIndexList{DbIndex{Name, IsUnique, Properties[], Where}}`, `PropertyMap{ColumnName, IsNullable, IsUnique, Collation}`, `ForeignKeyConstraint{OnDeleteAction, OnUpdateAction}` (on navigation properties), `LinkTableRelationshipMap{…}`, `SchemaMap{TablePrefix}` (default table prefix = schema alias → `bis_`, `generic_`, `func_`).

Persisted values in `ec_ClassMap` ([N]iModelCore/ECDb/ECDb/SqlNames.h:120-140):
- `MapStrategy`: 0 NotMapped, 1 OwnTable, 2 TablePerHierarchy, 3 ExistingTable, **10 ForeignKeyRelationshipInTargetTable, 11 ForeignKeyRelationshipInSourceTable** (navigation-property relationships).
- `ShareColumnsMode`: 0 No, 1 Yes, 2 ApplyToSubclassesOnly.
- `JoinedTableInfo`: 0 None, 1 JoinedTable (class lives in a joined table), 2 ParentOfJoinedTable.

### 3.2 How BisCore applies it

| Class | CAs | Effect | Source |
|---|---|---|---|
| `bis:Element` | `ClassMap{TablePerHierarchy}` + DbIndexList | `bis_Element` holds the Element-level properties for **every** element, discriminated by `ECClassId` | [B]Domains/0-Core/BisCore.ecschema.xml:549-600 |
| `bis:GeometricElement` | `JoinedTablePerDirectSubclass` | each *direct* subclass gets a joined table: `bis_GeometricElement3d`, `bis_GeometricElement2d` | [B]…:1486-1497 |
| `bis:GeometricElement3d` / `2d` | `ShareColumns{MaxSharedColumnsBeforeOverflow=32, ApplyToSubclassesOnly=True}` | 3d/2d own props get dedicated columns; **all subclasses' props share `js1…js32`**, then `bis_GeometricElement3d_Overflow.os1…` | [B]…:1498-1580, 1636-1690 |
| `bis:InformationContentElement` | `JoinedTablePerDirectSubclass` | → `bis_DefinitionElement`, `bis_InformationPartitionElement`, `bis_Document`, `bis_InformationReferenceElement`, `bis_InformationRecordElement`, `bis_DriverBundleElement` (each with ShareColumns 32/ApplyToSubclassesOnly) | [B]…:703-741, 1011-1019, 1069-1075, 1174-1181 |
| `bis:Model` | TPH + ShareColumns 32/ApplyToSubclassesOnly | `bis_Model` + `ps…` for subclasses | [B]…:125-140 |
| `bis:ElementUniqueAspect` / `ElementMultiAspect` | TPH + ShareColumns 32/ApplyToSubclassesOnly | one table each for all aspect classes, `ps…` columns | [B]…:1924-1934, 1955-1965 |

Shared-column naming: `ps<n>` in a primary table, `js<n>` in a joined table, `os<n>` in an overflow table ([N]iModelCore/ECDb/ECDb/DbSchema.cpp:1229-1245); overflow table name = `<table>_Overflow`, PK + ECClassId copied from parent, index `ix_<table>_ecclassid` ([N]…/DbMappingManager.cpp:2150-2200). Shared columns have `ec_Column.Type=0 (Any)`, `ColumnKind=4`, SQLite type `BLOB`, i.e. they hold whatever SQLite value is written ([S] ec_Column rows; table DDL). Overflow is chosen when a property needs more columns than remain (a property needing > 63 goes straight to overflow) ([N]…/ClassMapColumnFactory.cpp:453-535).

### 3.3 Property → column rules

- Columns per property ([N]iModelCore/ECDb/ECDb/ClassMapColumnFactory.cpp:203-235): navigation = 2 (Id + RelECClassId), point3d = 3, point2d = 2, struct = sum of members, **arrays = 1**, other primitives = 1. Dedicated (non-shared) columns are named `<Prop>` / `<Prop>_X/_Y/_Z` ([S] `Origin_X`, `BBoxLow_Z`); struct members use access strings `Struct.Member`.
- Value encodings observed ([S]): string → TEXT; double → REAL; int/long/Id → INTEGER; boolean → INTEGER 0/1; **dateTime → REAL Julian day** (`LastMod` = 2457960.36…; trigger uses `julianday('now')`); binary and GUID (`FederationGuid`, `GeometryStream`) → BLOB. *Not verified in this pass:* the textual JSON format ECDb uses for primitive/struct arrays.
- **Navigation properties**: FK column `<Nav>Id` (no extra "Id" if the name already ends in "Id") and `<Nav>RelECClassId` ([N]…/DbMappingManager.cpp:1803-1830). The RelECClassId column is **virtual** (exists only in `ec_Column`, not in SQLite) when the relationship class is sealed, or the table is virtual/existing ([N]…/DbMappingManager.cpp:1441-1450). Hence in [S]: `ModelRelECClassId`, `CodeSpecRelECClassId`, `CodeScopeRelECClassId`, `CategoryRelECClassId` are virtual; `ParentRelECClassId`, `TypeDefinitionRelECClassId`, `ModeledElementRelECClassId`, `ElementRelECClassId` are physical and must be filled with the ECClassId of the concrete relationship class (e.g. `bis_Model.ModeledElementRelECClassId` = 67 = `bis:ModelModelsElement` in [S]).
- FK-mapped relationship classes (`ElementOwnsChildElements`, `ModelContainsElements`, …) have `MapStrategy` 10/11 and only a **virtual** table entry in `ec_Table` (Type 4) — there is no link table ([N]…/RelationshipClassMap.cpp:105-160; [S] ec_ClassMap rows 64, 74, 140).
- **Link-table relationships**: TPH table with `Id, ECClassId, SourceId, TargetId, ps…` (e.g. `bis_ElementRefersToElements`; `ElementGroupsMembers.MemberPriority` → `ps1`), or a sealed OwnTable without `ECClassId` (e.g. `bis_ModelSelectorRefersToModels`). Uniqueness via per-class partial index `uix_<rel>_sourcetarget … WHERE ECClassId=<id>` ([S] sqlite_master).
- **ElementAspects**: `bis_ElementUniqueAspect` / `bis_ElementMultiAspect` with `ElementId` (FK, cascade delete) + `ElementRelECClassId` (NOT NULL) + `ps…`. Example ([S] ec_PropertyMap): `bis:ExternalSourceAspect` → Scope.Id=ps1, Identifier=ps2, Kind=ps3, Version=ps4, Checksum=ps5, JsonProperties=ps6, Source.Id=ps7.
- **ECInstanceIds for non-elements** (aspects, link-table rows, models are element ids): ECSQL INSERT allocates from the briefcase-based sequence `be_Local('ec_instanceidsequence')` ([N]iModelCore/ECDb/ECDb/ECDbImpl.cpp:54; [N]iModelCore/ECDb/ECDb/ECSql/ECSqlPreparedStatement.cpp:929).

### 3.4 Worked example: where does a subclass of `bis:PhysicalElement` land?

`Generic:PhysicalObject` in [S] (ec_Class 231): `ec_ClassMap` = MapStrategy 2 (TPH), ShareColumnsMode 1, MaxShared 32, JoinedTableInfo 1; `ec_cache_ClassHasTables` = {31 `bis_Element`, 43 `bis_GeometricElement3d`}. Its `ec_PropertyMap` (q2.out.txt):

```
ECInstanceId→bis_Element.Id + bis_GeometricElement3d.ElementId     ECClassId→both .ECClassId
Model.Id→ModelId  LastMod  CodeSpec.Id→CodeSpecId  CodeScope.Id→CodeScopeId  CodeValue  UserLabel
Parent.Id→ParentId  Parent.RelECClassId→ParentRelECClassId  FederationGuid  JsonProperties   (bis_Element)
Category.Id→CategoryId  InSpatialIndex  Origin.X/Y/Z→Origin_X/Y/Z  Yaw Pitch Roll  BBoxLow.X..→BBoxLow_X..
BBoxHigh.X..  GeometryStream  TypeDefinition.Id/RelECClassId                                (bis_GeometricElement3d)
PhysicalMaterial.Id → bis_GeometricElement3d.js1   (shared column; its RelECClassId is virtual)
```

So a **dynamic/domain schema class `MySchema:Pipe : bis:PhysicalElement` with properties `Diameter (double)`, `Spec (string)`** gets one `ec_Class` row, an `ec_ClassMap` row (2,1,32,1), and its two properties are mapped to the **next free `js<n>` columns of `bis_GeometricElement3d`** (shared with every other 3d subclass; ECDb reuses the same `js` numbers across sibling classes and adds columns only when a hierarchy needs more). Once a class's chain needs more than 32 shared columns, the rest go to `bis_GeometricElement3d_Overflow.os<n>`. The *only* reliable way to find a column is the `ec_PropertyMap` join:

```sql
SELECT pp.AccessString, t.Name, c.Name, c.IsVirtual
FROM ec_PropertyMap pm
JOIN ec_PropertyPath pp ON pp.Id = pm.PropertyPathId
JOIN ec_Column c ON c.Id = pm.ColumnId
JOIN ec_Table t ON t.Id = c.TableId
WHERE pm.ClassId = (SELECT cl.Id FROM ec_Class cl JOIN ec_Schema s ON s.Id=cl.SchemaId
                    WHERE s.Name='MySchema' AND cl.Name='Pipe');
```

---

## 4. Element ids

### 4.1 Layout

**64-bit = (BriefcaseId << 40) | localId**: "Top 24 bits are BeBriefcaseId. Lower 40 bits are local id"; constructor `m_id = briefcaseId * 2^40 + id` ([N]iModelCore/BeSQLite/PublicAPI/BeSQLite/BeSQLite.h:299-307; [C]docs/learning/common/Id64.md:7-10). Local id 0 is illegal; id 0 = invalid ([C]docs/learning/common/Id64.md:10-12). In JSON/TS, ids are lower-case hex strings `"0x…"` with no leading zeros ([C]docs/learning/common/Id64.md:19-29).

| BriefcaseId | Meaning | Source |
|---|---|---|
| 0 | `Standalone()` / `BriefcaseIdValue.Unassigned`: snapshots, checkpoints, pull-only briefcases, StandaloneDb; cannot create changesets | BeSQLite.h:262-263; [C]core/common/src/BriefcaseTypes.ts:44-56 |
| 1 | `DeprecatedStandalone` (old "standalone") | BeSQLite.h:264 |
| 2 … 2^24-11 | iModelHub-issued briefcases | BeSQLite.h:265-269; BriefcaseTypes.ts:31-35 |
| 2^24-10 | `SchemaSyncElementReserved` | BeSQLite.h:270-272; BriefcaseTypes.ts:37-42 |
| 0xffffff | "no briefcase" → *transient* ids (frontend decorations only) | [C]docs/learning/common/Id64.md:14 |

### 4.2 Allocation

- Element ids come from `BeBriefcaseBasedIdSequence("bis_elementidsequence")`; the **last used value** is stored as an INTEGER in `be_Local('bis_elementidsequence')` and incremented per insert; if it would reach `(briefcaseId+1)<<40` the insert fails ([N]…/DgnCore/DgnDb.cpp:33; [N]iModelCore/BeSQLite/BeBriefcaseBasedIdSequence.cpp:71-90; [N]…/DgnCore/DgnElements.cpp:487-495).
- A caller-supplied id (e.g. transformer/import) is accepted and `CheckMaxValue` bumps the sequence if needed (DgnElements.cpp:489-491).
- On briefcase-id change and after applying a changeset the sequence is **reset** to `max(Id) FROM bis_Element WHERE Id ∈ [bcid<<40, (bcid+1)<<40)` (or the stored value if larger) ([N]…/DgnCore/DgnDb.cpp:354-372, 401-424, 448-466).
- `ec_instanceidsequence` does the same for ECSQL-inserted non-element instances (§3.3).
- CodeSpec and Font ids are "server issued" style `max(Id)+1` ([N]iModelCore/BeSQLite/BeSQLite.cpp:2561-2581; [N]…/DgnCore/CodeSpec.cpp:122; DbFont.cpp:60).
- [S]: snapshot (bcid 0) with max element id 0x1556 = 5462 and `bis_elementidsequence` = 5462 — consistent.

### 4.3 Reserved/well-known ids

| Id | Meaning | Source |
|---|---|---|
| 0x1 | Root Subject element **and** RepositoryModel (`IModel.rootSubjectId`, `repositoryModelId`); exclusive lock on it = "schema lock" | DgnElement.h:4112; DgnModel.h:704; [C]core/common/src/IModel.ts:397-399; [C]docs/learning/backend/ConcurrencyControl.md:65 |
| 0xe | RealityDataSources LinkPartition + LinkModel | DgnElement.h:4118 |
| 0x10 | Dictionary DefinitionPartition + DictionaryModel (`IModel.dictionaryId`) — categories, styles, materials, GeometryParts go here | DgnElement.h:4120; DgnModel.h:712; IModel.ts:401 |
| CodeSpec 0x1 | `bis:NullCodeSpec`; "empty code" = `{spec:0x1, scope:0x1, value:null}` | CodeSpec.cpp:289, 436; [C]core/common/src/Code.ts:56-59 |

---

## 5. GeometryStream

### 5.1 Storage pipeline (verified by decoding sample blobs)

```
column bis_GeometricElement3d.GeometryStream / bis_GeometricElement2d.GeometryStream (BLOB)
 └ BeSQLite "SnappyToBlob" framing: repeat { uint16 chunkSize (LE, includes these 2 bytes); snappy RAW block }
      each block compresses ≤ 32 KiB of input                      [N]iModelCore/BeSQLite/BeSQLite.cpp:4644-4732, 4556-4605
   └ uncompressed = GeomBlobHeader { uint32 signature = 0x0600 ("DgnDb06"); uint32 size }   [N]…/DgnCore/DgnElement.cpp:1451-1459
        followed by `size` bytes of op stream                                               DgnElement.cpp:1483-1507, 3808-3840
      └ op stream = repeat { uint32 opCode; uint32 paddedSize; data[paddedSize] }, data zero-padded to 8 bytes
                                                                                             [N]…/DgnCore/ElementGeometry.cpp:876-901
         first op MUST be Header {uint32 version = 1; uint32 flags (bit0 = ViewIndependent)}  [N]…/PublicAPI/DgnPlatform/ElementGeometry.h:176-180, 222-234, 263
```

`bis:GeometryPart.GeometryStream` uses the identical encoding but lives in a **shared column of `bis_DefinitionElement`** ([S] `js1`); its bbox is in `GeometryPart.BBoxLow/BBoxHigh` (other `js` columns) ([B]…/BisCore.ecschema.xml:2971-2990).

Decoded sample element 0x38 (`Generic:PhysicalObject`, stored blob 203 bytes, first bytes `cb00 8002 30 00060000 f8000000 …`):
```
uint16 chunk = 0x00cb (203)   snappy varint length 0x80 0x02 = 256
blobHeader signature=0x0600 payloadSize=248
op 1  Header          paddedSize=8    version=1 flags=0
op 4  BasicSymbology  paddedSize=48   subCategoryId=0x18 color=0x0000f000 useColor=1 useWeight=1
op 11 SolidPrimitive  paddedSize=168  payload starts "bg0001fb"
```

### 5.2 Op codes

[N]iModelCore/iModelPlatform/PublicAPI/DgnPlatform/ElementGeometry.h:176-200 (TS mirror: [C]core/common/src/geometry/ElementGeometry.ts:31-72):

| Op | Name | Payload encoding |
|---|---|---|
| 1 | Header | raw struct {u32 version=1, u32 flags} |
| 2 | SubGraphicRange | `FB.PointPrimitive` with 2 coords (low, high) = local range of the *next* primitive; emitted by the builder for sub-graphics, never inside parts (ElementGeometry.cpp:945-959, 6105-6107) |
| 3 | GeometryPartInstance | `FB.GeometryPart` {geomPartId:long, origin, yaw, pitch, roll (degrees), scale=1}; identity transform ⇒ only the id; mirroring not allowed (ElementGeometry.cpp:1487-1523) |
| 4 | BasicSymbology | `FB.BasicSymbology` (§5.4) |
| 5 / 6 | PointPrimitive / PointPrimitive2d | `FB.PointPrimitive{coords:[DPoint3d], boundary}` / 2d variant. Boundary 0 None=point string, 1 Open=line/linestring, 2 Closed=shape. The native writer *simplifies* single lines, linestrings, shapes and point strings to these ops (ElementGeometry.cpp:905-943, 1081-1220) |
| 7 | ArcPrimitive | `FB.ArcPrimitive{center, vector0, vector90, start, sweep, boundary}` (ElementGeometry.cpp:964-975) |
| 8 / 10 / 9 / 11 / 12 | CurveVector / CurvePrimitive / Polyface / SolidPrimitive / BsplineSurface | **BentleyGeometryFlatBuffer** ("bgfb"): 8-byte ASCII prefix `bg0001fb` + flatbuffer of `Bentley.Geometry.FB.VariantGeometry` from `allcg.fbs` (union: LineSegment, EllipticArc, BsplineCurve, LineString, CurveVector, DgnCone, DgnSphere, DgnTorusPipe, DgnBox, DgnExtrusion, DgnRotationalSweep, DgnRuledSweep, Polyface, BsplineSurface, VectorOfVariantGeometry, InterpolationCurve, TransitionSpiral, PointString, AkimaCurve, CatenaryCurve, PartialCurve) ([N]iModelCore/GeomLibs/serialization/src/FlatBuffer/FixedStructs.cpp:62-96; [N]iModelCore/GeomLibs/serialization/src/FlatBuffer/allcg.fbs:414-444; ElementGeometry.cpp:1221-1310) |
| 19 | AreaFill | `FB.AreaFill` |
| 20 | Pattern | `FB.AreaPattern` (hatch / crosshatch / symbol / DWG hatch def lines) |
| 21 | Material | `FB.Material{useMaterial, materialId, …, trans2x3}` — 3d only |
| 22 | TextString | `FB.TextString` from TextString.fbs (§5.6) |
| 23 | LineStyleModifiers | `FB.LineStyleModifiers` |
| 25 | ParasolidBRep | `FB.BRepData{entityTransform, brepType, entityData (Parasolid XT), face symbology}` |
| 26 / 27 | BRepPolyfaceDeprecated / BRepCurveVecDeprecated | legacy |
| 28 | Image | `FB.Image{tileCorner0..3, drawBorder, textureId}` (ElementGeometry.cpp:1793-1810) |

Element-graphics flatbuffer schema: [N]iModelCore/iModelPlatform/DgnCore/ElementGraphics.fbs (namespace `BentleyM0200.Dgn.FB`, line 6; enums 11-15; structs 20-28; tables 33-241).

### 5.3 Symbology model

- **Category is not in the stream** — it is the element's `CategoryId` column. The stream switches **SubCategory** (default = the category's default subcategory) and carries *overrides* of the SubCategory appearance ([C]docs/learning/common/GeometryStream.md:5-11).
- Writer logic ([N]…/DgnCore/ElementGeometry.cpp:1526-1736):
  - `BasicSymbology{subCategoryId:long, color:uint, weight:uint, lineStyleId:long, transparency:double, displayPriority:int, geomClass:byte, useColor, useWeight, useStyle:ubyte}` (ElementGraphics.fbs:127-143). If nothing overrides, a table with only `subCategoryId` is written ("reset to subcategory appearance"). `subCategoryId = 0` is written for **GeometryPart** streams (parts can't change subcategory). `displayPriority` is forced to 0 for 3d (2d only).
  - Colour = `ColorDef` **0xTTBBGGRR** (red in low byte, transparency in high byte) ([C]core/common/src/ColorDef.ts:21, 37, 61). Sample: `0x0000f000` = pure green.
  - Weight = integer line weight (0-31 in iTwin.js `GeometryAppearanceProps.weight`); style = `bis:LineStyle` element id, components in `be_Prop` `dgn_LStyle/*`.
  - Transparency 0..1 adds to net transparency; GeometryClass enum Primary 0, Construction 1, Dimension 2, Pattern 3 (ElementGraphics.fbs:14).
  - Fill: `AreaFill{fill: FillDisplay(0 None,1 ByView,2 Always,3 Blanking), color, useColor, backgroundFill (0 none, 1 solid, 2 outline), transparency, mode (GradientMode), flags, angle, tint, shift, colors[], values[], thematicSettings}` (ElementGraphics.fbs:192-210).
  - Pattern: `AreaPattern{origin, rotation, space1, space2, angle1, angle2, scale, color, weight, useColor, useWeight, invisibleBoundary, snappable, symbolId (GeometryPart id), defLine[]}` (ElementGraphics.fbs:223-241).
  - Material: emitted only for 3d when not "from subcategory"; `materialId` = `bis:RenderMaterial` element id.
- Equivalent JSON entries: `appearance`, `styleMod`, `fill`, `pattern`, `material` ([C]core/common/src/geometry/GeometryStream.ts:33-83, 169-187).

### 5.4 Parts

`bis:GeometryPart` (DefinitionElement, usually in the Dictionary 0x10) holds a stream in local coords; elements reference it with op 3 / JSON `geomPart{part, origin?, rotation? (YPR), scale?}`. No nesting; no subcategory changes inside a part; the instance inherits the element's current symbology ([C]docs/learning/common/GeometryStream.md:52-57; [C]core/common/src/geometry/GeometryStream.ts:124-139).

### 5.5 Text

Op 22 payload = `FB.TextString{majorVersion, minorVersion, text:string, style: TextStringStyle{fontId:uint (→ dgn_Font.Id), isBold, isItalic, isUnderlined, height:double, widthFactor:double}, transform: TextStringTransform (3×4), range?, glyphIds?:[uint], glyphOrigins?:[{x,y}]}` ([N]iModelCore/iModelPlatform/DgnCore/TextString.fbs:11-77). The native writer includes glyph layout (`IncludeGlyphLayoutData`) (ElementGeometry.cpp:1781-1791). JSON: `textString: TextStringProps` (origin + rotation relative to placement).

### 5.6 JSON form accepted on insert (iTwin.js)

`GeometricElement3dProps` = `ElementProps{classFullName, model, code{spec, scope, value?}, parent?{id, relClassName}, federationGuid?, userLabel?, jsonProperties?}` + `category` + `placement{origin, angles{yaw,pitch,roll}, bbox?}` + `geom?: GeometryStreamProps` **or** `elementGeometryBuilderParams{entryArray:[{opcode, data(flatbuffer bytes)}], viewIndependent?}` + `typeDefinition?` ([C]core/common/src/ElementProps.ts:24-50, 104-124, 131-135, 161-163, 279-285; Code.ts:24-31). `bbox` should be left undefined: it is computed from the geometry ([C]docs/learning/common/GeometryStream.md:45).

`GeometryStreamProps` = array of entries, exactly one key each: `header{flags}` (first only), `appearance`, `styleMod`, `fill`, `pattern`, `material`, `geomPart`, `textString`, `brep`, `image`, `subRange`, or any IModelJson geometry key (`lineSegment`, `lineString`, `arc`, `bcurve`, `interpolationCurve`, `transitionSpiral`, `point`, `pointString`, `path`, `loop`, `parityRegion`, `unionRegion`, `bagOfCurves`, `indexedMesh`, `bsurf`, `box`, `sphere`, `cylinder`, `cone`, `torusPipe`, `linearSweep`, `rotationalSweep`, `ruledSweep`) ([C]core/common/src/geometry/GeometryStream.ts:169-187; [C]core/geometry/src/serialization/IModelJsonSchema.ts:67-205). Bare-number angles in IModelJson/YPR are **degrees** ([C]core/geometry/src/Geometry.ts:182-207). Example:

```json
{ "classFullName": "Generic:PhysicalObject", "model": "0x20000000001", "category": "0x20000000002",
  "code": { "spec": "0x1", "scope": "0x1" },
  "placement": { "origin": [100.0, 200.0, 0.0], "angles": { "yaw": 30 } },
  "geom": [
    { "appearance": { "subCategory": "0x20000000003", "color": 255, "weight": 2 } },
    { "lineString": [[0,0,0],[5,0,0],[5,5,0]] },
    { "arc": { "center": [0,0,0], "vectorX": [1,0,0], "vectorY": [0,1,0], "sweepStartEnd": [0,360] } } ] }
```

The TS package also contains a full **binary** encoder for the per-op payloads (`ElementGeometry.fromGeometryQuery` → `BentleyGeometryFlatBuffer.geometryToBytes(geom, true)`, `fromGeometryPart`, `fromTextString`, `appendGeometryParams` …), i.e. a portable reference implementation for a C# encoder ([C]core/common/src/geometry/ElementGeometry.ts:1069-1105, 1165, 1862, 2254-2291). Note the `entryArray` data is the bare flatbuffer; the native side adds the op framing, 8-byte padding, 0x0600 header and Snappy.

### 5.7 Coordinates, units, placement, 2d vs 3d

- **Units: metres, always** ("Coordinates in iModels are always stored in Meters") ([C]docs/learning/GeoLocation.md:30; Glossary.md:86). DGN UORs must be scaled by the model's UOR-per-metre on export.
- Geometry is stored in **element-local** coordinates; world = placement transform × local ([C]docs/learning/common/GeometryStream.md:39-45).
- **Placement3d** = `Origin_X/Y/Z` + `Yaw/Pitch/Roll` in **degrees** ([B]…/BisCore.ecschema.xml:1529-1560) with matrix **Rz(yaw)·Ry(−pitch)·Rx(roll)** (pitch is clockwise about +Y) ([C]core/geometry/src/geometry3d/YawPitchRollAngles.ts:40-58) + element-aligned bbox `BBoxLow/High` in local coords (computed by the builder: `GeometryBuilder::Finish` requires a valid placement, [N]…/ElementGeometry.cpp:5746-5790).
- **Placement2d** = `Origin_X/Y`, `Rotation` (degrees), `BBoxLow/High_X/Y` ([B]…:1660-1680); 2d streams use PointPrimitive2d for simple curves; display priority only meaningful in 2d.
- `InSpatialIndex=1` puts the world AABB of the placement into `dgn_SpatialIndex` via triggers (§2.3). Geometry-bearing models also carry `GeometricModel.GeometryGuid` (changed whenever geometry in the model changes; used by tile caches) ([B]…:197; [N]…/DgnCore/DgnDb.cpp:120-123).

---

## 6. File-level properties (extents, origin, ECEF, GCS)

| Property | Storage | Format | Source |
|---|---|---|---|
| Project extents | `be_Prop('dgn_Db','Extents').StrData` | JSON `{"low":[x,y,z],"high":[x,y,z]}` (key order may vary) | [N]iModelCore/iModelPlatform/DgnCore/DgnUnits.cpp:474-510, 572-580; [S] |
| Global origin, ECEF, initial project center, extents source | `be_Prop('dgn_Db','Units').StrData` | JSON `{"globalOrigin":[x,y,z], "initialProjectCenter":[…], "ecefLocation":{"origin":[x,y,z], "orientation":{"yaw":°,"pitch":°,"roll":°}, "cartographicOrigin":{"longitude":rad,"latitude":rad,"height":m}, "xVector":[…], "yVector":[…]}, "extentsSource":n}` | DgnUnits.cpp:94-115 (ECEF ToJson; lat/long converted to **radians**), 414-471 (Load/Save); [S] `{"globalOrigin":[0.0,0.0,0.0]}` |
| GCS | `be_Prop('dgn_Db','DgnGCS').Data` | **Binary MicroStation "Type 66" GCS app-data** (`CreateGeoCoordType66` / `FromGeoCoordType66AppData`), zlib-compressed by be_Prop when >100 bytes and smaller | [N]iModelCore/iModelPlatform/DgnGeoCoord/DgnGeoCoord.cpp:5334-5343, 5446-5462; BeSQLite.cpp:2113-2135 |
| Vertical CRS (named) | `be_Prop('dgn_Db','DgnGCSVerticalCRS')` | JSON `{"verticalCRS":{…}, "type66Hash":"<MD5 of type-66 bytes>"}` — ignored if the hash doesn't match | DgnGeoCoord.cpp:5350-5370, 5464-5481 |

Semantics ([C]docs/learning/GeoLocation.md:30-78): global origin is a bias added to spatial coordinates before cartographic conversion and applies only to spatial models; ECEF location = position of the spatial origin in ECEF + YPR rotation spatial→ECEF; linear-geolocated iModels have ECEF but no GCS; projected ones have both. TS: `IModelProps{projectExtents, globalOrigin, ecefLocation, geographicCoordinateSystem}` ([C]core/common/src/IModel.ts:61-104), settable at creation (`SnapshotDb.createEmpty(…, {geographicCoordinateSystem, ecefLocation})`, [C]core/backend/src/IModelDb.ts:4756-4772).

**Direct relevance to DgnKit:** the GCS is persisted as MicroStation's own type-66 GCS application data, so a V8 file's type-66 GCS payload is the natural source. *Unverified:* that the bytes are identical to what DgnKit reads from a V8 type-66 element (layout/version/endianness/"paper scale" handling — `InitCacheParameters` adjusts scale/global origin before storing, DgnGeoCoord.cpp:5452-5455).

---

## 7. Snapshot vs Standalone vs Briefcase; txns; changesets; locks/codes/channels

### 7.1 Kinds of files (same on-disk format)

| | SnapshotDb | StandaloneDb | BriefcaseDb |
|---|---|---|---|
| BriefcaseId | 0 (`resetBriefcaseId(Unassigned)`) | 0 | hub-issued 2…2^24-11 (or 0 for pull-only/checkpoints) |
| iTwinId (`be_Db/ProjectGuid`) | from source | empty GUID | iTwin GUID |
| Txns | none; read-only after close; `createFrom` vacuums, deletes txns and `StandaloneEdit` | `be_Local('StandaloneEdit') = {"txns": true}` if editing enabled; undo/redo, but txns deleted on close, no changesets | full Txn + changeset push/pull |
| Locks | n/a | none ("no lock server") | lock server |
| Source | [C]core/backend/src/IModelDb.ts:4722-4800 | IModelDb.ts:4897-4962, 5034-5035 | IModelDb.ts:3852-3946; [C]core/common/src/BriefcaseTypes.ts:44-56 |

Local briefcase file name = `<iModelId dir>/<briefcaseId>.bim` ([C]core/backend/src/BriefcaseManager.ts:161-162).

### 7.2 How changes are captured (Txns)

- `TxnManager` is a BeSQLite `ChangeTracker` = an **SQLite session** (`sqlite3session_create(db,"main")` + table filter) ([N]iModelCore/BeSQLite/ChangeSet.cpp:182-186; ChangeSet.h:473-506). Untracked tables: `be_Local`, `dgn_Txns`, `dgn_SpatialIndex*`, `dgn_Rebase`, FTS shadow tables ([N]…/DgnCore/TxnManager.cpp:1030-1039) — everything else, including `ec_*` and `be_Prop`, is captured.
- On `SaveChanges`, a row is inserted into `dgn_Txns` ([N]…/DgnCore/TxnManager.cpp:589-711):
  - `Id` = **TxnId = (SessionId << 32) | counter** ([N]…/PublicAPI/DgnPlatform/TxnManager.h:218-253) — [S] 0x100000000, 0x100000001, …, 0x200000000.
  - `IsSchemaChange` actually stores `TxnType`: 0 Data, 1 DDL (payload is SQL text, not a changeset), **NULL = 2 EcSchema** (changeset touching ec_ tables) (TxnManager.h:52-59; TxnManager.cpp:617-620).
  - `Change` = Snappy-framed `{uint32 0x0600, uint32 size}` + raw **SQLite changeset bytes** (or DDL text) (TxnManager.cpp:178-185, 655-697). [S] decoded: Txn 0x100000000 → "ALTER TABLE ec_Schema ADD COLUMN …" (DDL); Txn 0x100000001 → changeset starting with `'T'` table record for `be_Prop` (SQLite session format).
  - Alternative "file-based txns" (`be_Local('fileBasedTxns')='1'`): `Change` holds `{uint32 0x0700, uint32 uncompressedSize, SHA1}` and the changeset goes to `txns/<16-hex>.txn` (TxnManager.cpp:188-205, 303-338, 411-455).
- Txn size limits: warn > 200 MB, hard fail > 3 GB uncompressed (TxnManager.cpp:207-213, 597-608).

### 7.3 Changeset files

- Pushing merges pending txns with `sqlite3changegroup` and writes `<tempbase>.changeset` (ChangesetTxns.cpp:836-845, 1103-1170; ChangeSet.cpp:817-882).
- File layout ([N]iModelCore/BeSQLite/ChangesetFile.cpp:14-17, 92-131, 136-165, 252-277): `ChangesetLzmaHeader{uint16 sizeOfHeader; char id[15]="ChangeSetLzma"; uint16 formatVersion=0x10; uint16 compressionType=2 (LZMA2)}` followed by an **LZMA2** stream containing `[uint32 big-endian prefixLength][prefix JSON {"ContainsSchemaChanges":true,"DDL":"…"} — empty for data-only][SQLite changeset bytes]`.
- **ChangesetId** = hex SHA1 over: parent id as 20 raw bytes (zeros if none) ‖ prefix bytes ‖ changeset bytes ([N]…/DgnCore/ChangesetTxns.cpp:583-690; [C]docs/learning/iModelHub/Briefcases.md:11-13).
- Applying: `sqlite3changeset_apply_v3_strm` with conflict callbacks (ChangeSet.cpp:917-945), schema changesets applied first/separately (TxnManager.cpp:244-300; [C]core/backend/src/BriefcaseManager.ts:508-511), then parent id/index saved in be_Local, element-id sequence reset (DgnDb.cpp:401-424).

### 7.4 Locks, codes, channels

- **Locks** are not in the .bim: held by the iModelHub lock server per briefcase; local cache `<base>-locks` SQLite db (`locks`, `txn_locks`, `metadata`); Exclusive vs Shared; exclusive on 0x1 = schema lock; elements created by this briefcase above the pushed high-water mark are implicitly locked ([C]core/backend/src/internal/ServerBasedLocks.ts:44-74; [C]docs/learning/backend/ConcurrencyControl.md:49-65, 140-189).
- **Codes**: uniqueness is enforced *in the file* by the unique index `ix_bis_Element_Code(CodeSpecId, CodeScopeId, CodeValue)` (NULL CodeValue never collides); cross-briefcase coordination is optional (CodeService / reservations) ([S] index SQL; ConcurrencyControl.md:12, 238-252).
- **Channels**: a channel root is an element carrying a `bis:ChannelRootAspect{Owner = channelKey}`; everything not below a channel root is channel `"shared"`; enforcement is cooperative in TypeScript (`ChannelControl.addAllowedChannel`) — nothing in SQLite enforces it ([C]core/backend/src/internal/ChannelAdmin.ts:21, 49-61, 102-106; [C]core/backend/src/ChannelControl.ts:134; [C]docs/learning/backend/Channel.md; [B]…/BisCore.ecschema.xml:2164).

---

## 8. Can an iModel be written without iTwin.js / the native addon?

### 8.1 Official APIs today

- The only public write API is **`@itwin/core-backend` (TypeScript/Node)** on top of the **`@bentley/imodeljs-native`** N-API addon built from imodel-native ([N]README.md "Architecture"; [N]iModelJsNodeAddon/api_package/ts/src/NativeLibrary.ts is the contract). imodel-native is **Apache-2.0** (`gh api repos/iTwin/imodel-native` license), but per its README it "does not build on its own — it needs `imodel-native-internal` (build strategies, third-party sources)", which is not public; the C++ surface is not shipped as an SDK.
- No public .NET/C# iModel-file API exists in the iTwin GitHub org: the C# repos there are REST samples (`iTwin-api-sample-app`, `storage-api-sample-app`, …) and a Unity example (`gh api orgs/iTwin/repos`). Connectors (incl. MicroStation .dgn) are run via iTwin Synchronizer / TypeScript connector framework ([C]docs/learning/imodel-connectors.md:10, 25).
- *Not verified from source:* Bentley's hosted REST "iModels API" (baseline .bim upload) and "Synchronization API" (cloud DGN connector) as alternatives; legacy C++ "iModelBridge" SDKs.

### 8.2 Raw SQLite from C#: what you must reproduce / what breaks

Reading is straightforward with any SQLite (R*Tree/FTS5 needed only for those tables). Writing raw SQL is possible for **data**, but every invariant normally enforced by native code becomes your job:

| Area | Requirement / failure mode | Source |
|---|---|---|
| Container | WAL, page size, `user_version=10`, `application_id='BeDb'`, be_Prop/be_Local/be_EmbedFile DDL, DbGuid, BriefcaseId blob | §1 |
| Profile versions | Must write `be_Db`, `ec_Db`, `dgn_Db` SchemaVersion JSON consistent with the tables you produce; otherwise the file is rejected or forced read-only | §1.2; BeSQLite.cpp:3952-3997 |
| **Schema import / ECDb mapping** | The O/R mapping (class maps, shared-column allocation, overflow, FK vs link-table decisions, index generation, `ec_cache_*`) is thousands of lines (e.g. DbMappingManager.cpp 2356 lines, RelationshipClassMap.cpp 1615) — re-implementing it is the highest-risk part. ECSQL writes are additionally gated by a CRUD write token so even ECSQL can't be used to insert elements | [N]…/DgnCore/DgnDb.cpp:34 |
| Domain/handler tables | `dgn_Domain`/`dgn_Handler` rows for classes with handlers | DgnDbSchema.cpp:360-371 |
| Bootstrap | root Subject/RepositoryModel 0x1, 0xe, 0x10, 32 CodeSpecs, Units/Extents properties | §2.5 |
| Ids | Briefcase-based ids; update `be_Local('bis_elementidsequence')` and `('ec_instanceidsequence')` to ≥ max used, or later iTwin.js inserts collide (UNIQUE PK) — the stored value is only re-derived from `bis_Element` on briefcase/changeset events | §4.2 |
| Relationship columns | Physical `*RelECClassId` columns must hold the right relationship ECClassId; virtual ones must not be created | §3.3 |
| Codes | `CodeSpecId`/`CodeScopeId` NOT NULL (use 0x1/0x1 for "no code"); unique `(CodeSpec, Scope, Value)` | [S] DDL; Code.ts:56-59 |
| Geometry | Exact op framing, 8-byte padding, 0x0600 header, Snappy chunking (≤32 KiB input per chunk, uint16 chunk length), flatbuffer payloads, `bg0001fb` geometry flatbuffers; bbox must be computed (element-aligned in local coords) | §5 |
| Spatial index triggers | Inserting into `bis_GeometricElement3d` with `InSpatialIndex=1` fires `dgn_rtree_ins`, which calls `DGN_placement_aabb` etc. — **fails with "no such function" unless your connection registers equivalent SQL functions** (or you leave InSpatialIndex 0 and lose spatial queries) | DgnDbSchema.cpp:182-206, 403-406; DgnSqlFuncs.cpp:810-850 |
| LastMod / GeometryGuid | `LastMod` default/trigger handle elements; models' `GeometryGuid` should change when geometry changes (tile caches) | [S] trigger; DgnDb.cpp:120-123 |
| Validation | No handler validation (model/element type compatibility, parent/child rules, category in dictionary, 2d vs 3d model, project extents) | — |
| Change tracking | Raw writes are invisible to `TxnManager`; for a BriefcaseDb you'd have to produce dgn_Txns or a valid changeset (LZMA header + prefix + SQLite changeset, SHA1 id chain) yourself. For SnapshotDb/StandaloneDb (briefcase 0) no txns are needed | §7 |
| Fonts, line styles, GCS | dgn_Font + be_Prop `dgn_Font/EmbeddedFaceData`; `dgn_LStyle` components; type-66 GCS blob | §2.1, §6 |

**Lowest-risk raw-write strategy** (an inference, not an official recommendation): create the seed file with iTwin.js (`SnapshotDb.createEmpty` / `StandaloneDb.createEmpty`) and import BisCore/Generic plus any dynamic schema there (so ECDb does all mapping and DDL), then from C# insert rows into the existing tables using `ec_PropertyMap` to locate columns, register `DGN_*`-compatible SQL functions (or populate `dgn_SpatialIndex` yourself), keep both id sequences up to date, write GeometryStreams with a C# port of the TS `ElementGeometry` encoders, and finally open the result once with iTwin.js (open/`validateSchemas`/a few ECSQL queries) as an oracle. The fully supported alternative is to emit an intermediate (JSON per element with `GeometryStreamProps`) from C# and have a small Node/iTwin.js process insert it.

---

## 9. Sample inspection results (appendix)

Files: `[C]core/backend/src/test/assets/test.bim` (blob d568db9e…) and `[C]core/backend/src/test/assets/test_ec_4003.bim` (blob 5b416558…), both downloaded from itwinjs-core @ 79aa5374. Raw outputs: `scratchpad/research/q1.out.txt` (test.bim), `q1b.out.txt`, `q2.out.txt`, `q3.out.txt` (test_ec_4003.bim).

**Header / pragmas (both files):** page_size 4096; user_version 10; application_id 1113932898 (`BeDb`); encoding UTF-8; header bytes 18/19 = 2/2 (WAL). Page count 297 vs 1034.

**Profiles:** test.bim: be_Db 3.1.0.1, ec_Db 4.0.0.1, dgn_Db 2.0.0.0 (no `ec_Unit*`/`ec_Format*`/`ec_Phenomenon` tables, no `OriginalECXmlVersion*` columns, no sqlite_stat1). test_ec_4003.bim: be_Db 3.1.0.2, ec_Db 4.0.0.3 (InitialSchemaVersion 4.0.0.0), dgn_Db 2.0.0.5.

**be_Prop (test_ec_4003):**
```
be_Db  BeSQLiteBuild  02.00.02.999          be_Db  CreationDate 2017-07-25T20:44:59.397Z
be_Db  DbGuid         BLOB[16] dbd4826b4f72423cb4faff8205f2bae7
be_Db  SchemaVersion  {"major":3,"minor":1,"sub1":0,"sub2":2}
dgn_Db Client ''      dgn_Db LastEditor ''  dgn_Db LastSchemaUpgradeInPhase Development
dgn_Db Extents        {"high":[29.85…,35.77…,20.94…],"low":[-10.88…,-16.02…,-10.09…]}
dgn_Db SchemaVersion  {"major":2,"minor":0,"sub1":0,"sub2":5}
dgn_Db Units          {"globalOrigin":[0.0,0.0,0.0]}
dgn_View Thumbnail (Id 52) {"format":"png","height":768,"width":768} + PNG BLOB[19086]
ec_Db  InitialSchemaVersion {"major":4,…,"sub2":0}   ec_Db SchemaVersion {"major":4,…,"sub2":3}
```
**be_Local:** `be_repositoryid` BLOB 0000000000000000; `bis_elementidsequence` 5462 (int); `ec_instanceidsequence` 14 (int); `StandaloneEdit` BLOB `{ "txns": true }`.

**Tables (test_ec_4003, 66):** be_EmbedFile, be_Local, be_Prop; bis_CodeSpec, bis_DefinitionElement, bis_Document, bis_DocumentCarrier, bis_DriverBundleElement, bis_Element, bis_ElementDrivesElement, bis_ElementMultiAspect, bis_ElementRefersToElements, bis_ElementUniqueAspect, bis_GeometricElement2d, bis_GeometricElement3d, bis_InformationPartitionElement, bis_InformationRecordElement, bis_InformationReferenceElement, bis_Model, bis_ModelSelectorRefersToModels; dgn_Domain, dgn_Font, dgn_Handler, dgn_ProvenanceElement/File/Model (legacy V8-converter provenance, not created by today's code), dgn_SpatialIndex(+_node,_parent,_rowid), dgn_Txns, dgn_fts_content, dgn_fts_idx(+_config,_data,_docsize,_idx); ec_Class, ec_ClassHasBaseClasses, ec_ClassMap, ec_Column, ec_CustomAttribute, ec_Enumeration, ec_Format, ec_FormatCompositeUnit, ec_Index, ec_IndexColumn, ec_KindOfQuantity, ec_Phenomenon, ec_Property, ec_PropertyCategory, ec_PropertyMap, ec_PropertyPath, ec_RelationshipConstraint, ec_RelationshipConstraintClass, ec_Schema, ec_SchemaReference, ec_Table, ec_Unit, ec_UnitSystem, ec_cache_ClassHasTables, ec_cache_ClassHierarchy; ecdbf_ExternalFileInfo, ecdbf_FileInfoOwnership; func_FunctionalElement; sqlite_stat1. Triggers: bis_Element_CurrentTimeStamp, delete_embeddedFiles, dgn_fts_ad/ai/au, dgn_prjrange_del, dgn_rtree_ins, dgn_rtree_upd, dgn_rtree_upd1. 139 indexes.

**Row counts:** bis_Element 5449; bis_GeometricElement3d 4804; bis_GeometricElement2d 0; bis_DefinitionElement 26; bis_InformationPartitionElement 611; bis_Model 612; bis_CodeSpec 33; aspects 0/0; bis_ElementRefersToElements 9; bis_ModelSelectorRefersToModels 5; ec_Schema 13; ec_Class 322; ec_Property 339; ec_ClassMap 322; ec_PropertyMap 4129; ec_PropertyPath 364; ec_Table 100; ec_Column 725; ec_Index 62; ec_CustomAttribute 356; ec_Enumeration 16; dgn_Txns 5; dgn_Font 4.

**ec_Schema:** ECDbMap 2.0.1, ECDbSystem 5.0.2, ECDbMeta 4.0.1, CoreCustomAttributes 1.0.3, ECDbFileInfo 2.0.1, ECDbSchemaPolicies 1.0.1, BisCore 1.0.15, Generic 1.0.5, Functional 1.0.3, Raster, PointCloud, ThreeMx, ScalableMesh.

**Sample element rows:**
```
Id  class                ModelId ParentId CodeSpecId CodeScopeId CodeValue                    LastMod (julian)
0x1  Subject             1       NULL     31         1           TBD                          2457960.36457613
0xe  LinkPartition       1       1        29         1           BisCore.RealityDataSources
0x10 DefinitionPartition 1       1        29         1           BisCore.DictionaryModel
0x17 SpatialCategory     16      NULL     22         16          Uncategorized
0x18 SubCategory         16      23       30         23          Uncategorized
0x38 PhysicalObject      31      NULL     1          1           NULL   (FederationGuid NULL, JsonProperties NULL)
bis_GeometricElement3d 0x38: CategoryId 23, InSpatialIndex 1, Origin (6.494,19.898,8.020),
  Yaw 25.949 Pitch 4.8e-15 Roll 114.778, BBox ±9.7359, GeometryStream BLOB[203]
bis_Model: 0x1 RepositoryModel; 0xe LinkModel (IsPrivate 1); 0x10 DictionaryModel (IsPrivate 1);
  ModeledElementRelECClassId 67 (bis:ModelModelsElement) on all rows
```
**ec_ClassMap highlights:** Element (2,NULL,NULL,NULL); GeometricElement (2,–,–,2=ParentOfJoinedTable); GeometricElement3d (2,2,32,1); PhysicalElement / Generic:PhysicalObject (2,1,32,1); Model (2,2,32,–); ElementRefersToElements (2,2,32,–); ElementMultiAspect/UniqueAspect (2,2,32,–); ExternalSourceAspect (2,1,32,–); ModelContainsElements 10; ElementOwnsChildElements 10; GeometricElement3dIsInCategory 11; CodeSpec 1; ModelSelectorRefersToModels 1.

**ec_Column for bis_GeometricElement3d** shows interleaved shared and virtual columns: `js1 (Any, kind 4)`, `SectionViewRelECClassId (virtual)`, `js2`, `js3`, `ModelSelectorRelECClassId (virtual)`, `js4`, `CategorySelectorRelECClassId (virtual)`, `js5`, `ViewAttachmentRelECClassId (virtual)`, `PhysicalMaterialRelECClassId (virtual)`.

**dgn_Txns:** 5 rows; Ids 0x100000000–0x100000003 (session 1, "Automatic Profile Upgrade" etc.) and 0x200000000 (`{"description":"Created test data for queryExtents performance testing"}`); `IsSchemaChange` values 1, 0, 1, NULL, 0. Decoded Change blobs start with the Snappy chunk length, then `00060000` + size, then DDL text or a SQLite changeset (`'T'` record, nCol=8, PK flags 1-4, table `be_Prop`).

---

## 10. Not verified / caveats

1. Python was unavailable; inspection used `winsqlite3.dll` instead (read-only, immutable URI). Equivalent results, different tool than requested.
2. Both samples are converter-made 2017 files later upgraded; a **freshly created** current file was not inspected. Differences inferred from source: it would have `dgn_Rebase` (+`sqlite_sequence`), no `dgn_Provenance*`, only BisCore+Generic (+ECDb system schemas), newer BisCore columns (e.g. `GeometryGuid` in `bis_Model` shared columns), and profiles 3.1.0.2 / 4.0.0.5 / 2.0.0.9.
3. Exact JSON format ECDb uses for array properties stored in a single column — not checked.
4. That the stored `DgnGCS` type-66 bytes are byte-identical to a V8 type-66 element's payload — strongly implied by names, not verified.
5. `.ibim` — treated as a naming convention only; no `.ibim` file was opened.
6. BisCore version actually bundled with the current native addon (bis-schemas master is 01.00.28 "NotForProduction").
7. Arc `start`/`sweep` in `FB.ArcPrimitive` are the `DEllipse3d` angles (radians in Bentley geometry) — the .fbs only says "0.0 means full sweep"; units not confirmed from source here.
8. iTwin Platform REST alternatives (iModels API baseline upload, Synchronization API) and any closed-source Bentley C++/.NET SDKs — not verifiable from these repos.
9. The weight range 0-31 is the iTwin.js convention for `GeometryAppearanceProps.weight`; the flatbuffer field is a plain `uint`.
