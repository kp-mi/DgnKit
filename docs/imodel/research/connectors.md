# How iTwin connectors work today — research brief for a DgnKit → iModel connector

Compiled 2026-11-13. Scope: iTwin.js 4.x/5.x era. This brief does not repeat the deprecated iModel.js 2.x COBie sample.

**Evidence tags used throughout**

| Tag | Meaning |
|---|---|
| **[V-src]** | Verified by reading source code (repo path and line numbers given, branch tip as of 2026-11-13) |
| **[V-doc]** | Verified in official documentation (URL or doc source path given) |
| **[V-data]** | Verified by inspecting iModels that Bentley's own connectors produced. The files are test assets in `iTwin/itwinjs-core`. I queried them read-only through Windows' built-in `winsqlite3.dll`. Scripts: `scratchpad/research/scripts/sq.ps1` and `inspect.ps1` |
| **[V-staff]** | Statement by a Bentley developer in a public GitHub discussion |
| **[Inferred]** | My reasoning from the evidence. Not stated by Bentley |
| **[Unverified]** | Could not be found or confirmed publicly |

Snapshots of the main sources:
- `iTwin/connector-framework` on `main` @ `ccc473f` (2025-12-08). The last functional code commit was `6c3a071` "Port to iTwinjs Core 4.10.2" (2024-11-26). npm `@itwin/connector-framework@2.4.0`, published 2024-11-27.
- `iTwin/itwinjs-core` on `master` (core-backend `5.16.0-dev.12`). npm latest is `@itwin/core-backend@5.15.1` (2026-11-09).
- `iTwin/connector-samples` is **archived**, last push 2026-02-03. Its README says the samples are deprecated.

---

## 0. Headline findings

1. **The official framework is stuck on an end-of-life iTwin.js line.** [V-src][V-doc]
   - `@itwin/connector-framework@2.4.0` is the latest release. It peers on `@itwin/core-backend ^4.10.2` and requires `engines.node ^22.0.0` ([package.json](https://github.com/iTwin/connector-framework/blob/main/package.json); npm registry metadata).
   - iTwin.js 4.x reached **end-of-life on 2026-06-13** ([api-support-policies.md "Version support status"](https://github.com/iTwin/itwinjs-core/blob/master/docs/learning/api-support-policies.md)). 5.x is "Current".
   - Open PR #161 (2026-08-26) in connector-framework says the move to core 5.x "is a breaking core 4.x to 5.x migration" that has not happened yet. It calls its own fix "a short-term band-aid on the 4.x line" ([PR #161](https://github.com/iTwin/connector-framework/pull/161)).
   - No other 5.x-based connector framework is published on npm (npm search, 2026-11-13).
   - `@itwin/pcf` (the "Parametric Connector Framework" used by the connector-samples) is **archived**. Its last release is `2.0.0-dev.1` (2024-04), on core 4.x.
2. **The framework is small.** It is roughly 2,000 lines of TypeScript, and the logic you need is in two files: `ConnectorRunner.ts` (666 lines) and `Synchronizer.ts` (993 lines). Porting its behaviour to iTwin.js 5.x yourself is realistic. [Inferred from V-src]
3. **Third-party connectors cannot be hosted by Bentley's Synchronization service as far as public docs show.** The Synchronization API's `connectorType` is a closed enum of Bentley connectors (`MSTN`, `DWG`, `IFC`, `REVIT`, …), and I found no "bring your own connector" mechanism. A custom connector therefore runs on your own infrastructure and pushes to iModelHub through the iModels API. [V-doc for the enum; Inferred for the conclusion]
4. **Bentley's DGN connector mapping can be partly reconstructed from real output.** Bentley ships test iModels written by its own DGN bridge (`IModelBridgeForMstn` / `DgnV8Bridge`, LastEditor `DgnV8Converter`). They show the exact Subject/Partition layout, category-per-level rules, and the provenance `Kind` and `Identifier` conventions. See §7. [V-data] These files are from 2018–2019, so the current connector may differ in details.
5. **Supported path for non-JS code.** The only supported write path is the native addon, called through `@itwin/core-backend` (TypeScript/Node). The C++ `iModelBridgeFwk` / `DgnV8Converter` is **not** open source; it ships only as binaries inside Bentley's desktop tools.
   - The C++ engine `iTwin/imodel-native` (iModelPlatform, ECDb, …) **is** public under Apache-2.0 (since 2022-11-02). There is no supported or documented C++ SDK for writing iModels from it.
   - The prebuilt npm binary is under a separate "right-to-run" licence.
   - Standard pattern for other languages: an extractor in your language writes an intermediate store, and a TypeScript connector reads it. [V-src][V-doc]

---

## 1. BaseConnector lifecycle (connector-framework 2.4.0)

### 1.1 Members of `BaseConnector`

All from [`src/BaseConnector.ts`](https://github.com/iTwin/connector-framework/blob/main/src/BaseConnector.ts) [V-src].

**Abstract — you must implement these:**

| Method | Doc comment (summarised) | Channel / step (from runner) |
|---|---|---|
| `static create(): Promise<BaseConnector>` | Factory. The runner calls `require(path).default.create()`. The base version throws. | — |
| `openSourceData(source: string)` | Connect to the source. Must call `synchronizer.recordDocument(...)` here so the RepositoryLink is created "while we are in the repository channel" (TestConnector comment). | Repository step 1 |
| `importDomainSchema(token?)` | Import fixed schemas used by every iModel this connector writes. | Repository step 2 |
| `importDynamicSchema(token?)` | Import source-specific (dynamic) schemas. | Repository step 3 |
| `initializeJob()` | Doc says it runs only on first sync. The runner actually calls it on **every** run; TestConnector guards with `if (ItemState.New === this._sourceDataState)`. Create partitions and models here. | Repository step 4 |
| `importDefinitions()` | Categories, line styles, materials, CodeSpecs, and so on. | Repository step 4 |
| `updateExistingData()` | Main conversion work. Use `Synchronizer.detectChanges` / `updateIModel` / `onElementSeen`. | **Connector channel** step 5 |
| `getApplicationVersion(): string` | Written to the job Subject's JSON as `BridgeVersion`. | — |
| `getConnectorName(): string` | Used in the job subject name, snapshot/standalone file name, and root subject name. | — |

**Overridable, with defaults:**

| Method | Default | Purpose |
|---|---|---|
| `onOpenIModel()` | returns SUCCESS | Runs after `openSourceData` in step 1 |
| `onClosingIModel?` | undefined | Called in `onFinish` before `db.close()`. Must not write. |
| `getDeletionDetectionParams()` | `{fileBased:false, scopeToPartition:false}` (legacy channel-based) | See §1.6 |
| `shouldDeleteElements()` | `true` | If `false`, the runner skips deletion detection |
| `getJobSubjectName(sourcePath)` | `` `${getConnectorName()}:${sourcePath}` `` | Code value of the job Subject. **Must** be overridden if several files share one channel. |
| `getChannelKey()` | `ChannelControl.sharedChannelName` (`"shared"`) | Return a private key to make the job Subject a channel root |
| `shouldCreateChangeSetGroup()` | `false` | Wraps the run's changesets in an iModels "changeset group" (but see §1.8) |
| `getChangeSetGroupDescription()` | connector name | — |
| `unmapSource(source)` | logs an error | Called when `jobArgs.shouldUnmapSource` is set |
| `getDataChangesDescription?()` | undefined | Declared, but not called by the runner [V-src: no reference in ConnectorRunner.ts] |
| `reportError(...)`, `reportStructuredError(...)` | write `SyncError.json` | Structured error file in the format of `iTwin/connector-errors` |

**Properties set by the runner:** `synchronizer`, `jobSubject`, `issueReporter`, `connectorArgs` (pass-through of `jobArgs.connectorArgs`). `getAccessToken()` is a helper.

### 1.2 Runner sequence

Source: [`ConnectorRunner.runUnsafe`, src/ConnectorRunner.ts L161–266](https://github.com/iTwin/connector-framework/blob/main/src/ConnectorRunner.ts#L161-L266) [V-src].

| # | Runner action | Lock taken (briefcase only) | Connector calls | Persist (changeset comment = `${revisionHeader} - <desc>`) |
|---|---|---|---|---|
| 0 | `loadConnector` (`require(path).default.create()`); `initAuthClient` if `dbType==="briefcase"`; `loadIssueReporter` (SqliteIssueReporter); `loadDb` (§3); `loadSynchronizer` (passes deletion params, token, channel key) | — | `create()`, `getChannelKey()`, `getConnectorName()`, `getDeletionDetectionParams()` | — |
| 1 | `doInRepositoryChannel`: insert or update `SynchronizationConfigLink` (code `"SynchConfig"` in RepositoryModel); set `connectorArgs`; `openSourceData(jobArgs.source)`; `ensureRootSourceRelationshipExists(config, source)`; `onOpenIModel()` | **exclusive on `IModel.rootSubjectId` (0x1)** | `openSourceData`, `onOpenIModel` | "Write configuration and open source data." |
| 2 | `doInRepositoryChannel` | exclusive 0x1 | `importDomainSchema(token)` | "Write domain schema." |
| 3 | `doInRepositoryChannel` | exclusive 0x1 | `importDynamicSchema(token)` | "Write dynamic schema." |
| 4 | `doInRepositoryChannel`: `updateJobSubject()` (find or insert job Subject; make it a channel root if the key is not shared) | exclusive 0x1 | `initializeJob()`, `importDefinitions()` | "Write job subject and definitions." |
| 4b | If `jobArgs.shouldUnmapSource`: `unmapSource(jobSubjectName)` + `updateProjectExtent()`, then **return** | exclusive 0x1 | `unmapSource` | "Unmapping source data" |
| 5 | `doInConnectorChannel(jobSubject.id)`: `updateExistingData()`, then `updateDeletedElements()`, which runs `synchronizer.detectDeletedElements()` if `shouldDeleteElements()` | **exclusive on job Subject** (the comment says this "automatically acquires shared lock on root subject") | `updateExistingData` | "Synchronize." |
| 6 | `doInRepositoryChannel`: `computeProjectExtents` → `updateProjectExtents`; `synchronizer.updateRepositoryLinks()` (writes final RepositoryLink aspect version/checksum); `updateSynchronizationConfigLink` (`lastSuccessfulRun = Date.now()`) | exclusive 0x1 | — | "Write synchronization finish time and extent." |
| 7 | `closeChangeSetGroup()` (if one was opened) | — | — | — |

**What `persistChanges` does** ([L598–614](https://github.com/iTwin/connector-framework/blob/main/src/ConnectorRunner.ts#L598-L614)) [V-src]:
- For a briefcase (not standalone): `pullChanges()` → (create or fetch the changeset group id) → `saveChanges(comment)` → `pushChanges({description: comment})`.
- For standalone or snapshot: `saveChanges(comment)` only.
- WriteAConnector says this gives "approximately five changesets" per run ([doc](https://www.itwinjs.org/learning/writeaconnector/#changeset-groups)) [V-doc].

**The "repository channel" lock is the schema lock.** [V-src + V-doc] `IModel.rootSubjectId === "0x1" === IModel.repositoryModelId` ([core/common/src/IModel.ts L397–399](https://github.com/iTwin/itwinjs-core/blob/master/core/common/src/IModel.ts)). The concurrency doc says: "The schema lock is simply the exclusive lock on the root element … To acquire it, no other briefcase may hold any lock at all … It is, in effect, an exclusive lock on the entire iModel" ([ConcurrencyControl.md "The Schema Lock"](https://github.com/iTwin/itwinjs-core/blob/master/docs/learning/backend/ConcurrencyControl.md)).
- So each connector run takes a whole-iModel exclusive lock **five times** (steps 1–4 and 6).
- Concurrent connector jobs on one iModel therefore serialise at those points, and retry on `LockOwnedByAnotherBriefcase` (§1.7).
- Only step 5 runs under the narrow job-subject lock. [Inferred]

### 1.3 Job Subject

Source: [`updateJobSubject`, L336–380](https://github.com/iTwin/connector-framework/blob/main/src/ConnectorRunner.ts#L336-L380) [V-src].

- **Code:** `Subject.createCode(db, IModel.rootSubjectId, getJobSubjectName(source))`. The default value is `"<ConnectorName>:<source path as passed>"`. The Subject is a child of the root Subject (`SubjectOwnsSubjects`).
- **Lookup:** by code. If found, it is reused. If not, it is inserted with this JSON (kept for 2.x "bridge" compatibility):
  `{"Subject":{"Job":{"Properties":{"BridgeVersion":<getApplicationVersion()>,"BridgeType":"JSConnector"},"Bridge":<getConnectorName()>}}}`.
- **Channel root:** if `getChannelKey() !== "shared"`, the runner calls `db.channels.makeChannelRoot({elementId, channelKey})` on insert.
- The runner assigns `connector.jobSubject` and `synchronizer.jobSubjectId`.
- WriteAConnector rule: all Subjects, partitions, definitions and models a connector creates must sit under its job Subject ([doc "Job Subjects" and "Locks & Codes"](https://www.itwinjs.org/learning/writeaconnector/#job-subjects)) [V-doc].
- The source path is used verbatim in the code. Running the same file from a different path creates a **different** job Subject. [Inferred from V-src]

### 1.4 Source registration: RepositoryLink, ExternalSource, SynchronizationConfigLink

**`Synchronizer.recordDocument(SourceDocument)`** ([Synchronizer.ts L279–354](https://github.com/iTwin/connector-framework/blob/main/src/Synchronizer.ts#L279-L354)) [V-src]:
- Creates a `RepositoryLink` in the RepositoryModel with code `RepositoryLink.createCode(db, 0x1, docid)` (`makeRepositoryLink` L920–939). It sets `url = docProps.desktopURN`, `userLabel = description = sourceDocument.description`, `repositoryGuid = docProps.docGuid`, and `jsonProperties.DocumentProperties = {desktopURN, webURN, attributes}`.
- Provenance for the document itself: an ExternalSourceAspect on the RepositoryLink with `Kind="DocumentWithBeGuid"`, `Scope=0x1`, `Identifier=code.value`, `Version=lastModifiedTime`, `Checksum=checksum()`. This is the same `Kind` string the native C++ bridge uses (§7.2). [V-src + V-data]
- **Fail-safe ordering:** for New or Changed documents, the aspect is first written with version and checksum **erased**. The real values are written only in step 6 (`updateRepositoryLinks`). A crashed run therefore leaves the document "changed", and the next run redoes it. (L347–350, L987–991.)
- If a RepositoryLink with that code exists but has no matching aspect, it throws `IModelError(NotFound)` ("Maybe RecordDocument was previously called on this file with a different scope or kind").
- `getOrCreateExternalSource` (L249–267) creates one `ExternalSource` per RepositoryLink: `repository → RepositoryLink`, empty code, in the RepositoryLink's model. Its id is returned as `results.source`. The ExternalSource's `connectorName` / `connectorVersion` are **not** set.
- `SourceDocument.docid` guidance (L138–140): "Preferably this should be a GUID … If the document is an unmanaged file, then a lower-cased, relative filename should be used."

**SynchronizationConfigLink** ([L389–419](https://github.com/iTwin/connector-framework/blob/main/src/ConnectorRunner.ts#L389-L419)): one per iModel, code value `"SynchConfig"`. Props can come from `jobArgs.synchConfigFile` (a `require`d JSON). `lastSuccessfulRun` is updated at the end. `ensureRootSourceRelationshipExists` inserts `SynchronizationConfigSpecifiesRootSources(config → ExternalSource)` (L531–547). [V-src]

**Linking models to files:** WriteAConnector requires an `ElementHasLinks` relationship from each physical partition to the source RepositoryLink. TestConnector does this in `createPhysicalModel` ([TestConnector.ts L232–248](https://github.com/iTwin/connector-framework/blob/main/test/TestConnector/TestConnector.ts#L232-L248)) [V-src][V-doc]. Bentley's DGN bridge does the same for every partition (§7.2) [V-data].

**BIS semantics** ([Provenance-in-BIS.md](https://www.itwinjs.org/bis/domains/provenance-in-bis/)) [V-doc]:
- `RepositoryLink` = an external repository (file).
- `ExternalSource` = an "information container" within it. A DGN file can hold several, one per model.
- `ExternalSourceAttachment` (owned by the attaching ExternalSource via `ExternalSourceOwnsAttachments`) models reference hierarchies. Fields: `attaches`, `role` (`SpecifyContext=0` / `SpecifyPart=1`), `translation`, `yaw`/`pitch`/`roll`, `scale` ([core/backend/src/ExternalSource.ts](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/ExternalSource.ts); [core/common/src/ElementProps.ts L488–509](https://github.com/iTwin/itwinjs-core/blob/master/core/common/src/ElementProps.ts)).
- `ExternalSourceGroup` handles duplicates across files (the IFC "same Wall in 3 files" case).
- `SynchronizationConfigLink` relates to sources via `SynchronizationConfigProcessesSources` / `SynchronizationConfigSpecifiesRootSources`.

### 1.5 Unmap

`jobArgs.shouldUnmapSource=true` → the connector's `unmapSource()` runs after step 4 and the runner returns. TestConnector's implementation does `deleteElementTree(jobSubject)`, then `synchronizer.deleteSynchronizationConfigLinkIfUnmappingLastExternalSource(repoLinkId)`, then deletes the RepositoryLink and the ExternalSource ([TestConnector.ts L172–180](https://github.com/iTwin/connector-framework/blob/main/test/TestConnector/TestConnector.ts#L172-L180)) [V-src].

### 1.6 Deletion detection ("unseen elements")

Source: [Synchronizer.ts L704–806](https://github.com/iTwin/connector-framework/blob/main/src/Synchronizer.ts#L704-L806) [V-src]; [CHANGELOG 2.0.3](https://github.com/iTwin/connector-framework/blob/main/CHANGELOG.md) [V-doc].

- The Synchronizer records ids passed to `onElementSeen`. Seen ids are also added automatically by insert/update in `updateIModel`, `insertResultsIntoIModel` and `updateResultInIModelForOneElement`.
- **Never runs on a SnapshotDb** (`if (this.imodel.isSnapshotDb()) return;`).
- **Channel-based (legacy default)** — `fileBased:false`, or `scopeToPartition:true`. `detectDeletedElementsInChannel()`:
  - Returns immediately if **any** recorded document was Unchanged.
  - Otherwise calls core `deleteElementSubTrees(db, jobSubjectId, filter)`. The filter deletes an element when it is not the job subject, is not seen, **and has at least one ExternalSourceAspect**. Untracked elements (no aspect) are left; the comment says "It's up to the connector to do GC on untracked elements".
  - `deleteElementSubTrees` walks top-down breadth-first. When the filter rejects an element it keeps descending, so unseen children of a seen parent are still deleted ([core ElementTreeWalker.ts L434–587](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/ElementTreeWalker.ts)).
- **File-based (recommended for new connectors)** — `fileBased:true, scopeToPartition:false`. `detectDeletedElementsInFiles()`:
  - Considers only RepositoryLinks whose state is **Changed**. New and Unchanged documents are skipped.
  - For each, it finds all ExternalSourceAspects with `Scope.Id = <RepositoryLink id>`. Unseen owners are deleted: `DefinitionElement`s through `deleteDefinitionElements`, others through `deleteElement`. Their unique aspects not seen are deleted too.
  - It recurses into aspects scoped to each found element (`detectDeletedElementsInScope(elementId)`).
  - **This is why the framework wants `SourceItem.scope = RepositoryLink id`.**
- `scopeToPartition:true` exists for legacy connectors, which "followed the incorrect example in the test connector of assigning the partition id rather than the repository link id". It forces channel-based detection (CHANGELOG 2.0.3 / 2.0.6). Note that Bentley's own native DGN bridge scopes element aspects to the **partition** (§7.2). [V-data]
- WriteAConnector "Deletion Detection" says new connectors should choose file-based ([doc](https://www.itwinjs.org/learning/writeaconnector/#deletion-detection-params)) [V-doc].

### 1.7 Error handling and retries

`ConnectorRunner.run()` ([L144–159](https://github.com/iTwin/connector-framework/blob/main/src/ConnectorRunner.ts#L144-L159), [L277–322](https://github.com/iTwin/connector-framework/blob/main/src/ConnectorRunner.ts#L277-L322)) [V-src] never throws; it returns `BentleyStatus`. On exception it:
1. Logs the error.
2. Calls `connector.reportError(stagingDir, msg, "ConnectorRunner","Run", …)`, which writes `SyncError.json` into `stagingDir` in the [`iTwin/connector-errors`](https://github.com/iTwin/connector-errors) format `{version:"1.0", errors:[{system, phase, category, descriptionKey, description, kbArticleLink, canUserFix}]}`.
3. `onFailure`: for a BriefcaseDb, `abandonChanges()` and `locks.releaseAllLocks()`; then writes `jobArgs.errorFile` (default `<stagingDir>/error.json`) containing `{id, message:"Failure", description, extendedData}`.

`finally` → `onFinish`: `abandonChanges()`, `connector.onClosingIModel?.()`, `db.close()`, then `issueReporter.publishReport()` and `close()`. The SqliteIssueReporter writes a `<activityId>-…-badgers.db` and a `…-issues.json` report "to be uploaded by the orchestrator" ([SqliteIssueReporter.ts](https://github.com/iTwin/connector-framework/blob/main/src/SqliteIssueReporter.ts), [ConnectorIssueReporter.ts](https://github.com/iTwin/connector-framework/blob/main/src/ConnectorIssueReporter.ts)).

**Retries** ([L616–651](https://github.com/iTwin/connector-framework/blob/main/src/ConnectorRunner.ts#L616-L651)):
- Only lock acquisition (and `BriefcaseDb.upgradeSchemas` on a fresh download) is retried, and only for `IModelHubStatus.LockOwnedByAnotherBriefcase`.
- Up to `hubArgs.maxLockRetries` (default 3) times, sleeping `random() × maxLockRetryWaitSeconds` (default 5 s).
- Before each lock retry it runs `pullChanges()` and `pushChanges({description:""})`.
- Push and merge retries come from core `PushChangesArgs` defaults: `mergeRetryCount` 5, `pushRetryCount` 3, 3 s delays ([core BriefcaseManager.ts L58–83](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/BriefcaseManager.ts)) [V-src].
- There is no job-level retry. A failed run leaves the RepositoryLink version/checksum erased (§1.4), so the next run reprocesses the document.

### 1.8 Gotchas found reading the code

All [Inferred from V-src]. Verify before relying on them.

- **One channel key per connector, not per job.**
  - `getChannelKey()` takes no arguments, and the runner calls it in `loadDb` before `connectorArgs` are assigned.
  - Core `makeChannelRoot` throws `"root-exists"` if any element is already the root for that key ([core internal/ChannelAdmin.ts](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/internal/ChannelAdmin.ts)).
  - So a non-shared key can serve only **one** job Subject per iModel. A second source file run with the same connector would fail at step 4.
  - The test suite runs non-shared keys against a single source only ([test/standalone/NonSharedChannelKey.test.ts](https://github.com/iTwin/connector-framework/blob/main/test/standalone/NonSharedChannelKey.test.ts)).
  - Workarounds: derive the key from something available at load time (an environment variable or `process.argv`), or stay on the shared channel.
- **Repository-channel steps rely on the shared channel being writable.** The runner only calls `addAllowedChannel(connectorKey)`. The shared channel is allowed only because `IModelHostOptions.allowSharedChannel` still defaults to `true` (`ChannelAdmin` constructor). The docstring says it "will be changed to default to false in 5.0", but the 5.16-dev code still defaults to allowing it.
- **Children of unchanged parents must be marked seen.** The connector must call `onElementSeen` for every child (and every aspect-bearing element) it wants to keep, including children of Unchanged parents. Both deletion modes otherwise delete them: children carry aspects, see next bullet.
- **Children inherit the parent's provenance.** `insertResultsIntoIModel` inserts each child with the **parent's** `aspectProps` (same Scope/Kind/Identifier) (L654–676). Several elements then share one identity triple. `detectChanges` takes `ids[0]` from `findAllBySource`, so it will normally pick the parent's (first-inserted) aspect; result order is not specified.
- **Changeset groups probably do not group anything.** The framework creates a group (`POST /imodels/{id}/changesetgroups`) and later closes it (`PATCH … {state:"completed"}`) ([src/ChangeSetGroup.ts](https://github.com/iTwin/connector-framework/blob/main/src/ChangeSetGroup.ts)). But it never passes the group id to `pushChanges`, and core `PushChangesArgs` has no group field. The iModels client does support `groupId` on changeset creation ([imodels-clients ChangesetOperationParams.ts L50–53](https://github.com/iTwin/imodels-clients/blob/main/clients/imodels-client-authoring/src/operations/changeset/ChangesetOperationParams.ts)).
- **`deleteElements` passes the whole array.** It calls `deleteElement(elementIds)` with the full array inside its per-id loop (L793–794). This works, but unseen-aspect cleanup then runs against already-deleted elements.
- **Class changes are returned, not thrown.** `updateIModel` on an element whose class changed returns `IModelStatus.WrongClass` ("Do delete + add instead"). The return status must be checked.
- **Some JobArgs fields cannot be set from JSON.** `updateDomainSchemas` and `updateDbProfile` are not read from `JobArgsProps` (always `true`). `HubArgs.region` is never used.
- **Narrow public exports.** The package index exports only `Args`, `LoggerCategory`, `ConnectorRunner`, `BaseConnector` and `Synchronizer` ([src/connector-framework.ts](https://github.com/iTwin/connector-framework/blob/main/src/connector-framework.ts)).

---

## 2. Synchronizer API in detail

All from [`src/Synchronizer.ts`](https://github.com/iTwin/connector-framework/blob/main/src/Synchronizer.ts) unless noted [V-src].

### 2.1 Types

```ts
enum ItemState { Unchanged, New, Changed }
interface SourceItem {          // L96-117
  id: string;                   // stable external id, unique within (scope, kind)
  scope?: string;               // element id; DEFAULT = jobSubjectId (setSourceItemDefaults L360-372)
  kind?: string;                // DEFAULT ""
  version?: string;             // e.g. last-modified; must change when content changes
  checksum?(): string|undefined;// lazily computed hash; "should cache its return value"
  source?: string;              // ExternalSource id; DEFAULT = the only recorded link's ExternalSource if exactly 1 link
}
interface ChangeResults { id?: Id64String; state: ItemState; existingExternalSourceAspect?: ExternalSourceAspectProps; }
interface SynchronizationResults { elementProps: ElementProps; childElements?: SynchronizationResults[]; itemState: ItemState; }
interface SourceDocument { docid: string; docProps?: {docGuid?, webURN?, desktopURN?, attributesJson?}; description?: string; lastModifiedTime?: string; checksum?(): string|undefined; }
interface RecordDocumentResults extends SynchronizationResults { source: Id64String; preChangeAspect?; postChangeAspect; }
interface DeletionDetectionParams { fileBased: boolean; scopeToPartition: boolean; }
```

The doc comment (L48–95) says: "The (kind, scope, id) triple must uniquely identify the entity in the outside world."

**Default-scope trap:** if you omit `scope`, it defaults to the **job Subject**. File-based deletion only looks at aspects scoped to RepositoryLinks, so new connectors must set `scope` explicitly to the RepositoryLink id (as TestConnector does at L478–484). [V-src]

### 2.2 `detectChanges(item)` (L379–432)

1. Apply defaults.
2. If `item.id !== ""`, run `ExternalSourceAspect.findAllBySource(db, scope, kind, id)`. This is the ECSQL `SELECT Element.Id, ECInstanceId FROM BisCore.ExternalSourceAspect WHERE Scope.Id=:scope AND Kind=:kind AND Identifier=:identifier` ([core ElementAspect.ts](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/ElementAspect.ts)).
3. No hit → `New`.
4. Otherwise load the first aspect, add it to `_seenAspects`, and set `existingExternalSourceAspect` and `id = elementId`.
5. Decide the state:
   - If `item.version !== undefined && item.version === aspect.version` → **Unchanged**. The checksum is never computed.
   - Else if `(item.checksum?.() ?? item.version) !== (aspect.checksum ?? aspect.version)` → **Changed**.
   - Else → **Unchanged**.
   - So version wins when present and equal; otherwise the comparison is checksum-or-version.
6. `detectChanges` does **not** mark the element seen. For Unchanged items the connector must call `onElementSeen(results.id)` (TestConnector L486–489), or call `updateIModel` with `itemState=Unchanged`, which does it (L449–455).

### 2.3 `updateIModel(results, sourceItem)` (L443–488)

- **Unchanged:** requires `results.elementProps.id`, calls `onElementSeen`, returns.
- **New or Changed:** looks up the aspect by triple again.
  - **Found** → `updateResultsInIModel`: `updateElement` (the class must be unchanged), `updateAspect` with new props, then the children (below).
  - **Not found** → `insertResultsIntoIModel`: `insertElement`, `insertAspect`, `onElementSeen`, then recursively insert children with `parent = ElementOwnsChildElements(elid)` and the **same aspectProps**.
- **Aspect props written** (`makeExternalSourceAspectPropsFromSourceItem`, L967–979): `{classFullName:"BisCore:ExternalSourceAspect", element, scope:{id}, identifier:id, kind, checksum:checksum?.(), version, source:{id}?}`. `jsonProperties` is **never** set by the framework.
- **Children on update** (L831–918):
  - If every child has an id or is New, children are matched to existing children by id. Matched → update; New → insert.
  - Otherwise existing children are mapped arbitrarily by position, with a warning.
  - Unmatched existing children are **not** deleted here. The comment says deletion relies on them not being marked seen.
- `setExternalSourceAspect(elementProps, state, item)`: a manual aspect insert or update, for elements written outside `updateIModel`.
- `makeExternalSourceAspectProps(item)`: build props without writing them.

### 2.4 The `ExternalSourceAspect` class in core 5.x

Source: [core/backend/src/ElementAspect.ts](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/ElementAspect.ts) [V-src]. It is an `ElementMultiAspect`, tagged `@public @preview`, owned via `ElementOwnsMultiAspects`.

| Field | Meaning |
|---|---|
| `scope: RelatedElement` | `ElementScopesExternalSourceIdentifier` nav prop. Comment: "in a future major release the `scope` property will be optional" |
| `identifier: string` | External id |
| `kind: string` | Classifier. Standard enum `ExternalSourceAspect.Kind` = `"Element"`, `"Relationship"`, `"Scope"` |
| `version?: string` | — |
| `checksum?: string` | — |
| `jsonProperties?: string` | A **string** today. Comment: "in a future major release … will be changed to object" — a planned breaking change |
| `source?: RelatedElement` | → `ExternalSource` |

- `ExternalSourceAspect.findAllBySource(db, scope, kind, identifier)` returns `{elementId, aspectId}[]`. Several matches are allowed.
- BIS doc: uniqueness of (Identifier, Kind, Scope) "is not enforced". Multiple aspects with the same triple on one element are allowed and can be disambiguated with `JsonProperties`. It "is recommended that data-synchronizers assign not-null values to `Identifier`, `Kind` and `Scope`" ([Provenance-in-BIS.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/bis/domains/Provenance-in-BIS.md)) [V-doc].
- Physical storage, as seen in real files: table `bis_ElementMultiAspect`, with `Scope.Id→ps1, Identifier→ps2, Kind→ps3, Version→ps4, Checksum→ps5, JsonProperties→ps6, Source.Id→ps7` [V-data via `ec_PropertyMap`].

### 2.5 Scope in multi-file jobs

- With `fileBased:true`, each file gets a RepositoryLink (via `recordDocument`) and an ExternalSource. Item scope = that file's RepositoryLink. Deletion is evaluated per **changed** file.
- `SourceItem.source` must be set explicitly once more than one link is recorded (the default applies only when `linkCount === 1`).
- If several files share one channel, override `getJobSubjectName` (BaseConnector comment, L150–156, L175–178). [V-src]
- BIS also allows a finer scope such as a partition or an ExternalSource per model. Bentley's DGN bridge uses the partition as scope (§7.2).

---

## 3. Running modes, configuration, versions, platform

### 3.1 `dbType` modes

Source: [ConnectorRunner.ts L483–567](https://github.com/iTwin/connector-framework/blob/main/src/ConnectorRunner.ts#L483-L567) [V-src].

| `jobArgs.dbType` | How the db is opened | Locks / push | Deletion detection | Incremental? |
|---|---|---|---|---|
| `"snapshot"` | `SnapshotDb.createEmpty(<stagingDir>/<ConnectorName>.bim, {rootSubject:{name: ConnectorName}})`. **Deletes any existing file first.** | none / none | **no-op** (`isSnapshotDb()` early return) | No — full rebuild each run |
| `"standalone"` | `StandaloneDb.openFile` if `<stagingDir>/<ConnectorName>.bim` exists, else `StandaloneDb.createEmpty(...)` | skipped / `saveChanges` only | yes | **Yes** — best for local development |
| `"briefcase"` (**default**) | `hubArgs.briefcaseFile`, else a cached briefcase from `BriefcaseManager.getCachedBriefcases(iModelGuid)` (optionally filtered by `briefcaseId`), else `BriefcaseManager.downloadBriefcase({iTwinId: projectGuid, iModelId})` then `BriefcaseDb.upgradeSchemas` → `BriefcaseDb.open` | yes / pull → save → push per step | yes | Yes |

After opening, `db.channels.addAllowedChannel(getChannelKey())`.

**What the runner does not do — the host process must:**
- Call `IModelHost.startup()`.
- Configure hub access (`BackendIModelsAccess` from `@itwin/imodels-access-backend` around `IModelsClient` from `@itwin/imodels-client-authoring`, base URL `https://[qa-|dev-]api.bentley.com/imodels`).
- Set `IModelHost.authorizationClient`.

See [test/integration/ConnectorRunner.test.ts L37–62](https://github.com/iTwin/connector-framework/blob/main/test/integration/ConnectorRunner.test.ts) and [wrapper-example/ExampleWrapper.ts](https://github.com/iTwin/connector-framework/blob/main/wrapper-example/ExampleWrapper.ts) [V-src].
- In iTwin.js 5.x, `IModelHost.setHubAccess` is no longer public. The setter is an internal symbol (`[_setHubAccess]`). Pass `hubAccess` in `IModelHostOptions` instead ([core IModelHost.ts L138, L592–604](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/IModelHost.ts)) [V-src].
- `IModelHostOptions.cacheDir` defaults to `%LOCALAPPDATA%/iModelJs/` on Windows. Briefcases are cached under it.

### 3.2 Args file (`ConnectorRunner.fromFile(path)` / `fromJSON`)

Source: [src/Args.ts](https://github.com/iTwin/connector-framework/blob/main/src/Args.ts) [V-src].

```jsonc
{
  "version": "0.0.1",                       // REQUIRED, exact value; anything else throws
  "jobArgs": {
    "source": "C:/data/foo.dgn",            // REQUIRED; passed to openSourceData() and used in the job-subject name
    "stagingDir": "...",                    // default: <framework lib dir>/staging; also holds snapshot/standalone .bim, error.json, SyncError.json
    "revisionHeader": "JSFWK",              // changeset comment prefix
    "dbType": "briefcase|snapshot|standalone", // default "briefcase"
    "activityId": "<guid>",                 // default: generated
    "issuesDbDir": "...",
    "loggerConfigJSONFile": "...json",      // Logger.configureLevels; default level Info
    "synchConfigFile": "...json",           // props for SynchronizationConfigLink
    "errorFile": "...",                     // default <stagingDir>/error.json
    "shouldUnmapSource": false,
    "connectorArgs": { /* free-form, given to connector.connectorArgs */ }
  },
  "hubArgs": {                              // required for briefcase
    "projectGuid": "<iTwinId>", "iModelGuid": "<iModelId>",
    "briefcaseFile": "...", "briefcaseId": 2,
    "tokenCallbackUrl": "http://...",       // GET → {access_token, expires_in}; cached until expiry
    "doInteractiveSignIn": false,
    "maxLockRetries": 3, "maxLockRetryWaitSeconds": 5,
    "region": "0"                           // accepted but unused
  }
}
```

**Validation (`HubArgs.isValid`):**
- Requires `iModelGuid` and `projectGuid`.
- `doInteractiveSignIn` requires `clientConfig`, which is **not** in `HubArgsProps` and must be set in code.
- Otherwise `tokenCallbackUrl` or `tokenCallback` (a function, so also code-only) must be present.
- From a pure JSON file, therefore, only `tokenCallbackUrl` works for briefcase mode. [V-src]

### 3.3 Authentication

Source: [ConnectorAuthenticationManager.ts](https://github.com/iTwin/connector-framework/blob/main/src/ConnectorAuthenticationManager.ts) [V-src].

The runner initialises auth only for `dbType==="briefcase"`. Snapshot and standalone runs use the token `"notoken"`.

Three client kinds:
1. **`callback`** → `CallbackClient`.
2. **`callbackUrl`** → `CallbackUrlClient`: fetches `{access_token, expires_in}` and caches it until expiry.
3. **Interactive** → `NodeCliAuthorizationClient(clientConfig)` with `signIn()` (opens a browser).
   - Config: `{clientId, redirectUri (localhost), scope, issuerUrl?}`.
   - The COBie-pcf sample used `scope: "imodels:modify imodels:read"`, `redirectUri: "http://localhost:3000/signin-callback"`, `issuerUrl: "https://qa-ims.bentley.com"` ([connector-samples cobie-excel-connector/src/App.ts](https://github.com/iTwin/connector-samples/blob/main/cobie-excel-connector/src/App.ts)).

**Scopes:**
- Framework README: "The following scopes are required: `imodels:modify` `imodels:read`". Its `.env` example uses `"imodels:modify imodels:read itwin-platform"` ([README](https://github.com/iTwin/connector-framework/blob/main/README.md)).
- Current developer-portal APIs document the single scope **`itwin-platform`** (for example Create iModel, Create Storage Connection) ([create-imodel](https://developer.bentley.com/apis/imodels-v2/operations/create-imodel/)) [V-doc].
- Create iModel additionally needs the iTwin permission `imodels_manage`.

**Non-interactive (CI or server):**
- Register a *service* application on developer.bentley.com and get tokens with `@itwin/service-authorization` (latest 2.1.3, peers core-bentley ^5) through a `tokenCallback`.
- The framework does not bundle a service client. [Inferred; package version V-src from npm]

The framework's `peerDependencies` pin `@itwin/node-cli-authorization ^2.0.3` (core 4.x). Version 3.x requires core-bentley ^5 (PR #161). [V-src]

### 3.4 Versions

npm registry as of 2026-11-13, plus docs.

| Package / platform | Status |
|---|---|
| `@itwin/connector-framework` | 2.4.0 (2024-11-27). peer `@itwin/core-backend/-bentley/-common ^4.10.2`, `@itwin/imodels-client-authoring ^5.8.2`, `@itwin/node-cli-authorization ^2.0.3`. `engines.node ^22.0.0`. CommonJS (PR #161 notes ESM breakage from `@openid/appauth@1.4.0`; fix is to pin `~1.3.2`) |
| `@itwin/core-backend` 4.x | last 4.11.7 (2025-11-13). Node 18/20/22. **EOL 2026-06-13** |
| `@itwin/core-backend` 5.x | latest 5.15.1 (2026-11-09). `engines.node ^20 \|\| ^22 \|\| ^24`. 5.16 (nightly) adds Node 26 ([SupportedPlatforms.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/learning/SupportedPlatforms.md)) |
| `@bentley/imodeljs-native` | 5.16.27 |
| `@itwin/imodels-access-backend` | 6.1.3 (peers core ^5). Last 5.x = 5.3.0 (peers core ^4) |
| `@itwin/imodels-client-authoring` | 6.2.2 |
| `@itwin/imodel-transformer` | latest 1.2.0 (core ^4.3.5, node ^18). 5.x line only as `2.0.0-rc.0` (2025-05) and nightly `2.0.0-dev.81` (core ^5.15, node ^22.12 \|\| ^24.18, **ESM-only**) |
| Node | WriteAConnector: "Node 22 LTS is suggested". Node 22 is the only version both 4.10+ and 5.x support |

**Implication [Inferred]:** if you use connector-framework 2.4.0 as published, you are on iTwin.js 4.x, which is EOL. Running it on 5.x means overriding peer dependencies (`--legacy-peer-deps`/overrides). The code also uses 5.x-deprecated implicit-transaction APIs (`insertElement` without `EditTxn`, `db.saveChanges`, `makeChannelRoot` without `txn`). These still work while `implicitWriteEnforcement="allow"` (the current default), per [EditTxn.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/learning/backend/EditTxn.md) and [ChannelAdmin.ts deprecations "in 5.9.0 … not removed until after 2027-05-04"](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/internal/ChannelAdmin.ts). [V-src/V-doc]

### 3.5 Native addon on Windows x64

- `@bentley/imodeljs-native` is a ~300 KB stub. Its `postinstall` runs `installNativePlatform.js`, which checks the platform (win32 supports **x64 only**) and runs `npm install --no-save @bentley/imodeljs-win32-x64@<ver>` ([imodel-native iModelJsNodeAddon/api_package/installNativePlatform.js](https://github.com/iTwin/imodel-native/blob/main/iModelJsNodeAddon/api_package/installNativePlatform.js)) [V-src].
- Prerequisite: **Visual Studio 2022 Redistributable** (SupportedPlatforms.md) [V-doc].
- So it works out of the box on Windows 10/11 x64 with npm. Two inferred caveats:
  - The install needs the npm registry and lifecycle scripts enabled. `--ignore-scripts`, or package managers that block postinstall by default, need allow-listing.
  - The install script honours `IMODELJS_NATIVE_OVERRIDE`.

### 3.6 Licence of the native module

Matters for the local-only mode.

- The npm binary ships under the **"BENTLEY RIGHT-TO-RUN AUTHORIZATION"** ([api_package/LICENSE.md](https://github.com/iTwin/imodel-native/blob/main/iModelJsNodeAddon/api_package/LICENSE.md)) [V-src]. "You may use the software if: a. You are using a Snapshot iModel, b. Or, for all other iModels, you and the program you are operating are identified and authorized by the owner of the data …, and such owner has been duly authorized by either Bentley or a third party authorized by Bentley." It also says "You may not modify the software".
- [AccessingIModels.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/learning/backend/AccessingIModels.md) still says the native module "is *not* open source" and that "Snapshot iModels may be accessed by any iTwin.js program". It says that changing the briefcase id of a snapshot "violates the spirit and intent of the license agreement" [V-doc].
- The C++ **source** is now public under Apache-2.0 at `iTwin/imodel-native` (repo created 2022-11-02, "Open Source initial commit") [V-src]. The docs saying it is not open source are stale with respect to the source; the binary licence is unchanged.
- **[Inferred — needs legal review]** SnapshotDb output is unambiguously covered. A `StandaloneDb` workflow falls under clause (b), and whether your own local data counts as "authorized by Bentley" is unclear.

---

## 4. Channels, locks, and Bentley's REST APIs

### 4.1 Channels (iTwin.js 4.6+)

Sources: [Channel.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/learning/backend/Channel.md), [ChannelControl.ts](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/ChannelControl.ts), [internal/ChannelAdmin.ts](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/internal/ChannelAdmin.ts) [V-doc/V-src].

- A channel is the tree of models and elements below a channel-root Subject. The root carries a `bis:ChannelRootAspect` whose `Owner` holds the `channelKey`.
- Everything outside any channel root is in the **shared channel** (`"shared"`).
- Writing an element, aspect or model whose channel is not in `IModelDb.channels` "allowed" set throws `ChannelConstraintViolation`.
- The shared channel is allowed by default (`IModelHostOptions.allowSharedChannel` defaults to true "for backwards compatibility").
- Key convention: `{organization}:{application}:{extension}`, for example `"bentley:localsync:mstn"`.
- **Channels may not nest.** A key can belong to only one root element.
- `ChannelRootAspect.Version` supports semantic versioning of a channel's layout. 5.x adds a beta `upgradeChannel(...)`.
- The 5.x API is `makeChannelRoot({elementId, channelKey, txn})` and `insertChannelSubject({subjectName, channelKey, parentSubjectId?, txn})`. The overloads without `txn` were deprecated in 5.9.0.
- Channels and locks are orthogonal: "To edit an element, its channel must be allowed AND its lock must be held."

**Why a connector owns a channel:** connector data must not be edited by other applications, and the connector must not edit theirs ([imodel-connectors.md "Connector jobs hold the locks"](https://www.itwinjs.org/learning/imodel-connectors/); Channel.md) [V-doc].

**Status in the framework:**
- Default is still the **shared channel**. WriteAConnector says: "beginning in iTwinjs 5.0, this will likely reverse and the default will require a channel key" ([WriteAConnector "Shared Channels"](https://www.itwinjs.org/learning/writeaconnector/#shared-channels)). The framework has not changed this [V-src].
- Bentley's own connectors do create channel roots: `ChannelRootAspect` owners `"osdbooster2pa"` and `"OpenSite+"` appear in `sim-master.bim`. The BIS doc's connector diagram shows a ChannelRootAspect on each connector Subject ("Bentley OpenBuildings Designer", "IFC", "Bentley Civil") ([information-hierarchy.md "iModel Connector Repository Organization"](https://github.com/iTwin/itwinjs-core/blob/master/docs/bis/guide/data-organization/information-hierarchy.md)) [V-data/V-doc].

### 4.2 Locks

Source: [ConcurrencyControl.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/learning/backend/ConcurrencyControl.md) [V-doc].

- Pessimistic locks: exclusive and shared.
  - Insert an element → shared on its model (and parent).
  - Update or delete → exclusive on the element.
  - Aspect insert/update/delete → exclusive on the owner.
- "Exclusive locking downward is implicit": an exclusive lock on a model's element or a parent covers its contents.
- Locks are checked at edit time and throw `IModelStatus.LockNotHeld`.
- `pushChanges` releases all locks unless `retainLocks:true`.
- Elements inserted since the last push are implicitly exclusively locked.
- Lock acquisition can fail with `PullIsRequired` if the briefcase is behind.
- `noLocks` iModels (set at creation, "experimental … Do NOT use this in production") make lock calls no-ops.
- 5.x adds "reservations" through a synchronous channel and "Schema Sync".
- [LockControl.ts](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/LockControl.ts): `acquireLocks({shared, exclusive})`, `releaseAllLocks()`, `holdsExclusiveLock`, `isServerBased`, …

The framework's pattern, an exclusive lock on the job Subject, covers the connector's whole subtree. That is why everything must live under the job Subject ([WriteAConnector "Locks & Codes"](https://www.itwinjs.org/learning/writeaconnector/#locks--codes)) [V-doc]. Codes are not reserved because they are scoped by the job Subject (same section).

### 4.3 iModels API (REST, `https://api.bentley.com/imodels`)

Source: [operations list](https://developer.bentley.com/apis/imodels-v2/operations/), [create-imodel](https://developer.bentley.com/apis/imodels-v2/operations/create-imodel/) [V-doc]. Scope is `itwin-platform`, with `Accept: application/vnd.bentley.itwin-platform.v2+json`.

**Create an iModel:** `POST /imodels/` with `{iTwinId, name, description?, extent?{southWest,northEast lat/long}, creationMode?: "empty"|"fromiModelVersion"|"fromBaseline", template?{iModelId, changesetId?}, baselineFile?{size}, geographicCoordinateSystem?{horizontalCRSId: "EPSG:…"}}`.
- Omitting `creationMode` is deprecated and cannot set a GCS.
- Rate limit: 5/min on trial, 50/min on paid tiers.

**Baseline upload** (to seed an iModel from a locally built `.bim`, for example a Standalone/Snapshot output):
1. Send `creationMode:"fromBaseline"` with the exact `baselineFile.size`.
2. `PUT` the file to the returned `upload` link (header `x-ms-blob-type: BlockBlob`).
3. Call the "Complete iModel Baseline Upload" operation (link `_links.complete`). There is also `POST/GET /imodels/{id}/baselinefile`.

**Other operations:**
- Changesets: `POST/GET /imodels/{id}/changesets…`
- Changeset groups: `POST/GET/PATCH /imodels/{id}/changesetgroups[/{groupId}]`
- Briefcases: `POST/GET/DELETE /imodels/{id}/briefcases…`
- Locks: `GET/PATCH /imodels/{id}/locks`, `POST …/locks/release-chunk`, `GET …/locks/released-locks`
- Named versions, checkpoints, clone (`POST /imodels/{id}/clone`), fork (`POST /imodels/{id}/fork`), permissions

You normally do not call these directly from a connector. `BackendIModelsAccess` and `BriefcaseDb.pushChanges` do it for you.

### 4.4 Synchronization API (Bentley-hosted connectors)

Sources: [overview](https://developer.bentley.com/apis/synchronization/overview/), [create-storage-connection](https://developer.bentley.com/apis/synchronization/operations/create-storage-connection/), [storage tutorial](https://developer.bentley.com/tutorials/synchronization-storage-tutorial), [supported formats](https://developer.bentley.com/apis/synchronization/supported-formats/) [V-doc].

**Concepts:**
- Connector.
- Connection (files → iModel). Only one run at a time per connection.
- Source file (can be marked a "spatial root").
- Run.

**Two connection kinds:**
- Storage connections, for files in iTwin Storage: `POST /synchronization/imodels/storageconnections` with `{iModelId, displayName?, authenticationType?: "User"|"Service", sourceFiles:[{storageFileId, connectorType}]}`. Start with `POST …/storageConnections/{id}/run` (202). Poll with `GET …/storageConnections/{id}/runs`; success is `state:"Completed", result:"Success"`. User-type connections need a stored refresh token (`GET /synchronization/imodels/connections/authorizationInformation?redirectUrl=…`).
- Manifest connections, for any document management system with pre-authenticated URLs: `…/manifestconnections`, `…/{id}/runs`, `…/{id}/sourcefiles/{sourceFileId}`.
- Diagnostics: `GET /synchronization/reports?...`

**`connectorType` enum** (schema): `AUTOPLANT, CIVIL, CIVIL3D, DWG, GEOSPATIAL, IFC, MSTN, NWD, OBD, OPENTOWER, REVIT, SPPID, SPXREVIEW, PROSTRUCTURES, RVM, PSEXCEL, INTELLIPID`. The page's 422 example also lists `DACQJSON`, an inconsistency on the page.

**Supported formats (selected):**
- MicroStation `.dgn` → `MSTN` ("CONNECT from Update 4"; also `.dwg`, `i.dgn`, "CONNECT / V8i Editions").
- OpenRoads/OpenRail/OpenBridge/OpenSite → `CIVIL`; OpenBuildings → `OBD`; ProStructures → `PROSTRUCTURES`.
- AutoCAD `.dwg`/`.dxf` → `DWG` (2015–2025); Civil 3D → `CIVIL3D`.
- IFC → `IFC` (IFC2x3, IFC4, IFC4.3); Revit → `REVIT` (2015–2024); Navisworks → `NWD`.
- Geospatial: SHP, GeoJSON, KML, ArcGIS FS, WFS → `GEOSPATIAL`.
- Many interchange formats (STEP, IGES, JT, Parasolid, SKP, OBJ, FBX…) go "MicroStation (via dgn)": they must first be imported into MicroStation and saved as DGN.

**Connector settings** — "Connection/iModel/iTwin configurations", Technical Preview ([patch-connection-configuration](https://developer.bentley.com/apis/synchronization/operations/patch-connection-configuration/), [patch-imodel-configuration](https://developer.bentley.com/apis/synchronization/operations/patch-imodel-configuration/)):

| Setting | Effect |
|---|---|
| `reclassifyDgnElements` | Change an element's base class if it was classified from the **Generic** schema (re-processes all Generic elements) |
| `ignoreDgnAttachments` | Skip reference attachments (DGN and DWG) |
| `enableSheetsAndDrawings` | Sheets and drawings (default **false**) |
| `enableSheetIndex`, `filterBySheetIndex` | Sheet index processing |
| `uploadDgnRealityData` | .3sm/.3mx/.pod/raster attachments → Reality Data API (default true) |
| `convertDgnTerrainModels` | Terrain model elements (default false) |
| `convertDgnHyperModelingSections` | Section graphics in 3D context (default true) |
| `squashIntermediateRevisions` | One changeset for physical + drawing data |
| `clampZExtent` | Limit project Z extent |
| `treatDwgModelSpaceAs2d` | DWG model space as 2D |
| `enableC3dGraphsIn2dModel` | Civil 3D 2D elements to a graph model |
| `ignoreIfcGlobalIds` | IFC provenance without GlobalIds |
| Revit settings | `setRvtLOD`, `skipRvtReprojection`, `overrideRvtReprojectionDistance`, `allowRevitIgnoreGcs` |

**Can third parties host a connector in this service? [Unverified → Inferred "no"]**
- The enum is closed, and no public documentation describes registering a custom connector.
- The framework's `ExampleWrapper.ts` shows Bentley's internal orchestrator contract: argv positions, a `connector/run|input|iTwinContext` JSON, and tokens via the `imbridge--server-token` file or a `TOKEN_URI`+`AUTH_GUID` endpoint. Its comments link to private Bentley Azure DevOps repos ("iModelBridgeApiServer", "iModelBridgeService connectorconfig.json"). This suggests Bentley runs JS connectors in its cloud through a wrapper, but only Bentley-authored ones [Inferred].
- A Bentley staffer answered requests to drive Bentley's connectors directly with "We don't normally encourage calling the Connectors directly…" and pointed to an Aha idea ([discussion #3276](https://github.com/iTwin/itwinjs-core/discussions/3276)) [V-staff].
- **Plan to run your connector on your own infrastructure** (a desktop or a server/container with Node 22) and push to iModelHub using a service or interactive token.

**Desktop tools:**
- iTwin Synchronizer (client and portal) and the free **iTwin Snapshot** app ([discussion #1659](https://github.com/iTwin/itwinjs-core/discussions/1659), [V-staff]: Snapshot "will create a static snapshot iModel").
- Both install Bentley's native connectors under `%LOCALAPPDATA%\imodelbridges\<Bridge>\iModelBridgeFwk.exe`. Examples: `RevitBridge`, `RealDwgBridge`, `IModelBridgeForMstn\DwgBridgeM02.dll` ([discussions #3276](https://github.com/iTwin/itwinjs-core/discussions/3276), [#6078](https://github.com/iTwin/itwinjs-core/discussions/6078)) [V-staff/user reports].
- **Useful as a DGN oracle:** run Bentley's MSTN connector into a snapshot and diff it against DgnKit's output. I did not verify whether iTwin Snapshot is still downloadable today.

---

## 5. `@itwin/imodel-transformer` — when it is used

Sources: [transformer docs index](https://github.com/iTwin/imodel-transformer/blob/main/docs/learning/transformer/index.md), [branching-imodels.md](https://github.com/iTwin/imodel-transformer/blob/main/docs/learning/transformer/branching-imodels.md), [packages/transformer/README.md](https://github.com/iTwin/imodel-transformer/blob/main/packages/transformer/README.md) [V-doc].

- **What it is:** ETL between **iModels**: `IModelExporter` → `IModelTransformer` (clone, filter, augment, schema-map, "change squashing") → `IModelImporter`.
- Also covers incremental export of changesets (`exportChanges`), branching and forking (`processChanges`, reverse sync), `tryAlignGeolocation`, and `autoExtendProjectExtents`.
- **Not a connector framework.** Source and target are both iModels.

**When to use it instead of or alongside a connector [Inferred from docs]:**
- **Two-stage connector.** Convert DGN → a local Snapshot/Standalone iModel with your own code (no hub, no locks). Then use the transformer (`processChanges` / `processAll` with provenance) to sync that iModel into the hub iModel. Changeset squashing and provenance come for free.
- **Consolidation and filtering.** Merge several iModels, or extract subsets.
- **Branch/fork workflows.**

**Provenance written by the transformer:**
- ExternalSourceAspects on target elements, scoped to a "Target Scope Element". This is usually an `ExternalSource` representing the source iModel, else the target root subject.
- A `Kind="Scope"` aspect stores sync state (`ExternalSourceAspect.Kind` enum `Element`/`Relationship`/`Scope`).
- Seen in real data: `ExternalSource` named "Transformation source iModel id - <guid>" carrying a `Kind="Scope"` aspect with `{"pendingReverseSyncChangesetIndices":[],"pendingSyncChangesetIndices":[],"reverseSyncVersion":""}` (`sim-master.bim`) [V-data].
- Federation GUIDs are preserved, element ids are not. "Only provenance can correctly correlate all elements between branches" (branching doc).

**Status:**
- Latest stable is `1.2.0` (iTwin.js 4.x).
- The 5.x line is `2.0.0-rc.0` / nightly `2.0.0-dev.81`. It is **ESM-only** and "relies on @internal APIs in iTwin.js, and therefore has very strict peerDependencies versions … version check at runtime" (README).
- 2.x requires a **started `EditTxn`** for the target (and the source for reverse sync) ([EditTxnInTransformer.md](https://github.com/iTwin/imodel-transformer/blob/main/docs/learning/EditTxnInTransformer.md)).
- Strict version coupling is a maintenance cost to weigh. [Inferred]

---

## 6. Non-JS languages (C#/C++)

**Supported write path [V-doc/V-src]:**
- Only the `@bentley/imodeljs-native` addon, through `@itwin/core-backend` (TypeScript/JavaScript on Node).
- WriteAConnector "Data Extraction" endorses two strategies:
  1. A TS-compatible extraction library called from `OpenSourceData`.
  2. "If a TypeScript binding is not available, extract the data into an intermediary format that can be then ingested by the alignment phase … A two-process architecture may also be employed."
- Source: [WriteAConnector.md L172–180](https://github.com/iTwin/itwinjs-core/blob/master/docs/learning/WriteAConnector.md).

**C++ iModelBridge / `iModelBridgeFwk` / `DgnV8Converter` [V-staff/V-data; open-source status Unverified → "not public"]:**
- They exist as native binaries inside Bentley's desktop tooling (`%LOCALAPPDATA%\imodelbridges\…\iModelBridgeFwk.exe`). Bentley-produced iModels record `dgn_Db.LastEditor = "DgnV8Converter"` (JoesHouse.bim).
- No public repo or SDK was found:
  - The iTwin GitHub org has no bridge or converter repos besides `connector-framework`, `connector-samples` (archived), `pcf` (archived) and `connector-errors`.
  - `iTwin/imodel-native` contains iModelPlatform/ECDb/GeomLibs etc. but no bridge framework or V8 converter (repo tree searched for "bridge", "dgnv8", "converter").
- The old JS `@bentley/imodel-bridge` npm package is deprecated ("reached end of life").
- Bentley's developer page mentions "iModel SDK" managed (.NET) write-only and native+managed options ([bentley.com/support/software-developers](https://www.bentley.com/support/software-developers)). [Inferred] This most likely refers to the legacy DGN-based *i-model* (i.dgn), not BIS iModels. Treat it as unverified for BIS.
- `iTwin/imodel-native` C++ source is Apache-2.0 and buildable in principle. It is **not** a supported SDK, has no documented C++ API for third parties, and the Node addon is the only consumer. [Inferred]

**Patterns for feeding data from C# [Inferred, ranked for DgnKit]:**
1. **C# extractor → intermediate store → TypeScript connector.** The COBie pattern. Use SQLite (DgnKit could write it directly with Microsoft.Data.Sqlite), NDJSON, or FlatBuffers/Protobuf. The store would hold:
   - per-element stable ids, a version (V8 `lastModified`) and a checksum, computed in C# so `detectChanges` costs nothing in TS;
   - geometry pre-converted to iTwin.js `GeometryStreamProps` JSON (or flatbuffer-encoded `ElementGeometry`) in meters and in iModel coordinates;
   - levels, models, attachments, cells and properties.
   
   The TS side stays thin: schema, definitions, `Synchronizer` loop. This is the least risky option.
2. **Child process streaming.** The TS connector spawns the DgnKit CLI and reads stdout as NDJSON. Same as (1) without a file, but harder to debug and to re-run.
3. **In-process .NET hosting from Node** via [`microsoft/node-api-dotnet`](https://github.com/microsoft/node-api-dotnet) or [`agracio/edge-js`](https://github.com/agracio/edge-js). Both are active as of 2026-11. Lowest latency, but adds native-interop fragility next to the iModel native addon.
4. **Do not** attempt to write `.bim` SQLite tables directly from C#. ECDb mapping, `GeometryStream` encoding, txn and changeset semantics, and the right-to-run terms make this unsupported.

---

## 7. How Bentley's connectors map DGN, DWG and IFC to BIS

**Caveat:** Bentley has not published a mapping specification. A user asked on GitHub, "Is there any documentation on how MicroStation bridge/synchronizer maps dgn schema to imodel ECSchema", and got no answer ([discussion #1820](https://github.com/iTwin/itwinjs-core/discussions/1820)).

The evidence below comes from:
- **(a)** iModels produced by Bentley's native DGN and DWG bridges, shipped as test assets in `iTwin/itwinjs-core` [V-data]:
  - [`test-apps/display-test-app/test-models/JoesHouse.bim`](https://github.com/iTwin/itwinjs-core/blob/master/test-apps/display-test-app/test-models/JoesHouse.bim) — `IModelBridgeForMstn`, master DGN + 9 references, BisCore 1.0.4, file dates Dec 2018–Mar 2019.
  - [`core/backend/src/test/assets/3dLinestyle.bim`](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/test/assets/3dLinestyle.bim) — `IModelBridgeForMstn` 1.0.0.0, Dec 2019.
  - [`full-stack-tests/presentation/assets/datasets/Properties_60InstancesWithUrl2.ibim`](https://github.com/iTwin/itwinjs-core/blob/master/full-stack-tests/presentation/assets/datasets/Properties_60InstancesWithUrl2.ibim) — `DgnV8Bridge`, with item types and a V8 EC schema.
  - [`core/backend/src/test/assets/mirukuru.ibim`](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/test/assets/mirukuru.ibim), `planprojection.bim`, `test.bim` — older DgnV8Bridge/DgnV8 converter output.
  - [`core/backend/src/test/assets/sim-master.bim`](https://github.com/iTwin/itwinjs-core/blob/master/core/backend/src/test/assets/sim-master.bim) — DWG/Civil 3D connector, OpenSite+ connector and transformer output, BisCore 1.0.16, 2024.
- **(b)** Bentley developers' statements in GitHub discussions [V-staff].
- **(c)** BIS docs, the developer-portal configuration docs, and `frontend-devtools` source.

The DGN files are 2018–2019 vintage. The current MicroStation connector likely adds ExternalSource and ExternalSourceAttachment elements and may differ in naming. Use these as conventions to emulate, not as a spec.

### 7.1 DGN: Subject, partition and model layout

**Current-generation layout** (`IModelBridgeForMstn`; JoesHouse.bim and 3dLinestyle.bim) [V-data]:

```
Root Subject                              (CodeValue = iModel name)
├─ RepositoryLink × N (one per DGN file, master + every reference file), in RepositoryModel
│    CodeValue = file URI ("file:///d|/dgn/joe/…/struct 2.dgn", lower-cased) or bare file name ("barrier_1.dgn"),
│    UserLabel = "Struct 2.dgn", JsonProperties = {"DocumentProperties":{"attributes":null,"desktopURN":"<path>","webURN":""}}
└─ Subject "IModelBridgeForMstn master model file://<path>, <root model name>"   UserLabel = "<master file base name>"
   │   JsonProperties {"Subject":{"Model":{"Type":"SourceMasterModel","rootModelExternalAspectInstanceId":"<aspect id>"}}}
   │   ElementHasLinks → master RepositoryLink
   └─ Subject "IModelBridgeForMstn"          ← JOB SUBJECT
       │   {"Subject":{"Job":{"Bridge":"IModelBridgeForMstn","Properties":{"BridgeType":"IModelBridgeForMstn","BridgeVersion":"1.0.0.0","ConverterType":0},
       │                      "Transform":[[1,0,0,0],[0,1,0,0],[0,0,1,0]]}}}      (job-level spatial transform, 3×4)
       ├─ GroupInformationPartition "Groups"     → Generic:GroupModel
       ├─ DocumentPartition "Drawings"           → DocumentListModel
       ├─ DocumentPartition "Sheets"             → DocumentListModel
       ├─ DefinitionPartition "Definitions"      → DefinitionModel (job-private: "Default"/"Uncategorized" categories, views, selectors, display styles)
       └─ Subject "Physical"  {"Subject":{"Model":{"Type":"Hierarchy"}}}
           ├─ PhysicalPartition "<master file base name>"  {"PhysicalPartition":{"Model":{"Content":"Master"}}} → PhysicalModel ; ElementHasLinks → its RepositoryLink
           ├─ Subject "Struct master.dgn" {"Subject":{"Model":{"Type":"References"}}}
           │   ├─ PhysicalPartition "Struct master" {"…Content":"Reference"} → PhysicalModel ; ElementHasLinks → that file's RepositoryLink
           │   ├─ Subject "Struct 2.dgn" (References) └─ PhysicalPartition "Struct 2" (Reference)
           │   └─ Subject "Struct 1.dgn" (References) └─ PhysicalPartition "Struct 1" (Reference)
           ├─ Subject "Arch master.dgn" (References) … nested exactly like the reference-attachment tree
           └─ Subject "MEP master.dgn" (References) …
```

Notes on this layout:
- One `PhysicalModel` per V8 3D model, including every referenced model. The **Subject nesting mirrors the attachment nesting**.
- BIS docs confirm the intent: "both the OpenBuilding Designer and Bentley Civil iModel Connectors create child Subjects for every referenced dgn & model on the external dataset" ([information-hierarchy.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/bis/guide/data-organization/information-hierarchy.md)) [V-doc].
- Partition CodeValue = file base name for each file's root model (JoesHouse). The V8 model name is kept in the model's provenance JSON (`v8ModelName`).

**Older DgnV8Bridge layout (2018)** [V-data]:
- Job Subject directly under root, CodeValue `"DgnV8Bridge:<path>, <root model name>"`, JSON `{"Subject":{"Job":{"Bridge":"DgnV8Bridge","Properties":{"BridgeType":"DgnV8Bridge","BridgeVersion":1,"RootModel":"3D Metric Design"}}}}`.
- Partitions "Converted Groups" / "Converted Drawings" / "Converted Sheets" / "Definition Model For <job subject code>".
- A Subject per file (`Type:"Hierarchy"`) with a PhysicalPartition.
- `test.bim` shows an even older form: job JSON `{"DgnV8":{"ConverterType":0,"NamePrefix":"","V8File":"mf3.dgn","V8RootModel":"A"}}` and nested `"Type":"References"` Subjects.

**Newer BIS provenance layer (documented, not seen in these old files)** [V-doc]:
- One `ExternalSource` per **DGN model** (`repository` → the file's RepositoryLink).
- An `ExternalSourceAttachment` (owned by the attaching ExternalSource) for each reference attachment.
- Each element's aspect points `source` → its model's ExternalSource and `scope` → its PhysicalPartition.
- Diagrams: [external-source-example.png and external-sources.png in Provenance-in-BIS / information-hierarchy](https://github.com/iTwin/itwinjs-core/tree/master/docs/bis/guide/media). The road-interchange example has "ExternalSource 'Model-1' → repository 'road interchange.dgn'", attachments, and aspects with `Identifier="987", Kind="Element"`, scope → PhysicalPartition "Road design".
- `sim-master.bim` (DWG, 2024) does contain ExternalSource elements and `Source` on aspects, which supports the conclusion that current connectors write them [V-data].

### 7.2 DGN: provenance conventions (ExternalSourceAspect)

[V-data] from JoesHouse.bim and 3dLinestyle.bim (`IModelBridgeForMstn`).

| Owner element (BIS class) | `Kind` | `Identifier` | `Scope` | `Version` | `Checksum` | `JsonProperties` |
|---|---|---|---|---|---|---|
| `RepositoryLink` (per file) | `DocumentWithBeGuid` | file URI, e.g. `file://d:\dgn\joe\…\Struct 2.dgn` (or bare file name) | root Subject `0x1` | e.g. `1552667460` (a Unix-**seconds** timestamp; meaning not confirmed) | empty | `{"lastSaveTime":<ms>,"fileSize":"<bytes>","fileName":"<path>","idPolicy":0}` |
| `PhysicalPartition` (per V8 model) | `Model` | V8 **ModelId** as decimal (`"0"` = default model) | that file's `RepositoryLink` | — | — | `{"transform":[0.0001,0,0,0, 0,0.0001,0,0, 0,0,0.0001,0],"v8ModelName":"3D Imperial Design"}` — 3×4 row-major UOR→meter transform (10,000 UOR/m) |
| `SubCategory` (default subcat of the level's category) | `Level` | V8 **LevelId** as decimal (64 = "Default") | that file's `RepositoryLink` | — | — | `{"v8ModelId":-1,"levelType":0,"v8LevelName":"Columns"}` (`v8ModelId -1` = file-level level table) |
| `SpatialViewDefinition` | `ViewDefinition` | V8 view element id (decimal) | file `RepositoryLink` | V8 lastModified (ms) | empty | `{"v8ViewName":"3D Imperial Design - View 1"}` |
| graphic element (`Generic:PhysicalObject`, …) | `Element` | V8 **ElementId** as decimal (`"811"`) | the element's **PhysicalPartition** | V8 element last-modified time, **ms since 1970** as decimal (`"1545167987482"`) | 32 hex chars, a 128-bit hash (likely MD5; algorithm and inputs not public) | NULL |

Supporting evidence:
- Bentley's `frontend-devtools` says "if the iModel was produced by the MicroStation bridge, the source aspect Id is usually a V8 element Id". Its tool queries `… FROM BisCore.ExternalSourceAspect WHERE … [Kind]='Element'` ([core/frontend-devtools/src/tools/SourceAspectIdTools.ts L18, L76, L93](https://github.com/iTwin/itwinjs-core/blob/master/core/frontend-devtools/src/tools/SourceAspectIdTools.ts)) [V-src].
- The connector-framework's `recordDocument` reuses `Kind="DocumentWithBeGuid"` with Scope `0x1`, the same convention [V-src].
- Element scope = **PhysicalPartition** in the native bridge. That is exactly the "legacy scopeToPartition" style the JS framework now discourages (§1.6). If you want ExternalSourceAspects that look like Bentley's MSTN output, you must use `scopeToPartition:true`, which forces channel-based deletion, or write your own deletion logic. [Inferred]

### 7.3 DGN: levels, categories and definitions

[V-data]

- **Every V8 level → a `SpatialCategory` named after the level, in the shared `BisCore.DictionaryModel` (0x10).** The ExternalSourceAspect goes on its default `SubCategory`.
  - Categories are **merged by name across files**. Level "Floor" from `Struct 1.dgn` (LevelId 3) and `Arch 1.dgn` (LevelId 1) map to the same SpatialCategory (id 50). Its SubCategory carries **two** `Level` aspects, one per file, each scoped to that file's RepositoryLink.
  - BIS docs: "Categories are similar to *levels* in DGN"; "SubCategories are similar to *levels* in DGN … as being the construct directly involved in visibility and styling" ([categories.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/bis/guide/fundamentals/categories.md)) [V-doc].
  - BIS guidance on where definitions go: "Only User-rank definitions should be placed in Subject-specific `DictionaryModel`s … the `DefinitionPartition` … should have a `CodeValue` of 'Definitions'" ([organizing-definition-elements.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/bis/guide/data-organization/organizing-definition-elements.md)) [V-doc]. The 2019 bridge still put level categories in the shared dictionary.
- **V8 "Default" level (id 64)** → a category in the job's own Definitions model, named `"Uncategorized"` (2019) or `"Default"` (Dec 2019). It comes with a matching `DrawingCategory`.
- **2D-related categories:** `DrawingCategory` "Detail", "Elevation", "Section", "Plan", "ExtractedGraphics" are created in the DictionaryModel (3dLinestyle.bim). [Inferred] These are for drawing extraction / hypermodeling.
- **Line styles:** V8 line styles → `LineStyle` in the DictionaryModel, including `lc1`…`lc7` (V8 standard line codes) and named styles such as "ROAD 3D WireRope NS wTerms". 3D line-style components → unnamed `GeometryPart`s in the dictionary.
- **Materials:** → `RenderMaterial`, named `"<material>: <palette>"` (e.g. "Grass Areas: Civil").
- **Views:** V8 saved/named views → `SpatialViewDefinition` "<model> - View N" with `ModelSelector`, `CategorySelector`, `DisplayStyle3d`, and `AuxCoordSystemSpatial` (ACS) in the job Definitions model. A `dgn_View.Thumbnail` is also stored.
- **Model properties:** `PhysicalModel.JsonProperties.formatter` carries the V8 working units for display: `{"fmtFlags":{…},"mastUnit":{"label":"m"},"subUnit":{"label":"mm","num":1000.0},"rndUnit":1000.0}` (Properties_60InstancesWithUrl2.ibim).

### 7.4 DGN: element classes, cells, item types and EC data

[V-data + V-staff]

**Default class:** plain 3D graphics without EC data → **`Generic:PhysicalObject`**.
- CodeValue is NULL.
- UserLabel: "If the V8 element has an ECInstance on it, the connector will use the DisplayLabel of that ECInstance as the UserLabel … Otherwise, the UserLabel is set to the description given by the V8 element's handler. The standard MicroStation Connector does not set CodeValues on elements by default." (cammac1803, [#1820](https://github.com/iTwin/itwinjs-core/discussions/1820)) [V-staff]. In the 2019 sample files UserLabel was NULL for plain elements [V-data].
- FederationGuid was NULL in the 2019 output.
- Placement: origin and bounding box in meters; `GeometryStream` holds the converted geometry.

**Elements with V8 EC instances (ECXAttributes):**
- The V8 EC schema is converted to an EC3 schema (e.g. `PCJ_TestSchema`). Support schemas are imported alongside: `ECv3ConversionAttributes`, `DgnCustomAttributes`, `Bentley_Standard_CustomAttributes`.
- The **element's class becomes the converted class**, e.g. 60 × `PCJ_TestSchema:TestClass` with UserLabel "TestClass".
- "When the MicroStation Connector converts the schema, it must decide what type of Element each V8 class will become. For example, graphics will become PhysicalElements and group headers will become GroupInformationElements. However, each V8 class can only become a single type in the iModel." (cammac1803, [#2559](https://github.com/iTwin/itwinjs-core/discussions/2559)) [V-staff].
- The `reclassifyDgnElements` setting re-bases elements previously "classified from the Generic schema" (§4.4) [V-doc].

**Item types** → a dynamic schema **`DgnCustomItemTypes_<ItemTypeLibraryName>`**.
- Each Item Type `X` becomes an **`XElementAspect`** (ElementMultiAspect) on the element. Example: `DgnCustomItemTypes_MyProp:areaElementAspect`.
- Names are EC-encoded (`volume__x0020__per__x0020__mass…`).
- Users query them as `DgnCustomItemTypes_IoT.LamporElementAspect` ([#2559](https://github.com/iTwin/itwinjs-core/discussions/2559)) [V-data/V-staff].
- An item type's "Use Item Name for Element" property drives the element label (pmconne, [#1820](https://github.com/iTwin/itwinjs-core/discussions/1820)) [V-staff].
- Dynamic schemas carry the `CoreCustomAttributes:DynamicSchema` custom attribute (seen on the DWG-side dynamic schemas in sim-master; required by docs) [V-data/V-doc].

**Cells** — bbastings, [#2559](https://github.com/iTwin/itwinjs-core/discussions/2559) and [#8483](https://github.com/iTwin/itwinjs-core/discussions/8483) [V-staff]:
- **Normal (named or orphan/group) cell → always an assembly.** The cell header becomes the parent element with **no geometry and no placement**. Each component becomes a child geometric element (`ElementOwnsChildElements`), "to preserve the levels of the components, but it's also to preserve things like ItemTypes … attached to the components". Nested cells nest as assemblies; the "Assembly" selection scope selects them.
- **Shared cell instance → a single geometric element** whose placement reflects the instance origin and rotation, "provided that the shared cell definition components have the same effective level". Otherwise it becomes an assembly. [Inferred] Shared cell definitions → `GeometryPart`s referenced from the instance's GeometryStream; unnamed dictionary GeometryParts are present.
- **SmartSolid → always a single geometric element** (its wireframe cell is ignored).
- "There is no concept of elements that contain other elements like cells in itwin" (bbastings) — assemblies are the stand-in.

**Named groups** → "a DgnV8 named group is mapped onto a subclass of GroupInformationElement" ([information-models-and-elements.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/bis/guide/other-perspectives/information-models-and-elements.md)) [V-doc]. They go in the "Groups"/"Converted Groups" `Generic:GroupModel` [V-data].

**2D elements, drawings and sheets:**
- Sheets and drawings are converted only when `enableSheetsAndDrawings` is on (default false in the Synchronization API).
- They go into the "Drawings" and "Sheets" `DocumentListModel`s. BIS: a `Drawing` is sub-modeled by a `DrawingModel`, a `Sheet` by a `SheetModel` ([Drawings & Sheets](https://www.itwinjs.org/bis/domains/drawings-sheets/)) [V-doc].
- Drawing and sheet views carry ExternalSourceAspects whose Scope is the RepositoryLink (Bentley answer, [#5396](https://github.com/iTwin/itwinjs-core/discussions/5396)) [V-staff].
- **Class used for 2D graphics in DrawingModels: [Unverified]** — presumably `BisCore:DrawingGraphic`. No sample available.
- Hypermodeling: `convertDgnHyperModelingSections` (default true) places 2D section graphics in 3D context [V-doc].

**Reference attachments:**
- One Subject + PhysicalPartition per attached model, recursively. One RepositoryLink per file. Newer BIS: `ExternalSourceAttachment` [V-data/V-doc].
- `ignoreDgnAttachments` syncs only the master.
- Attachments are found from "master files". Users are told to pick master files containing the 3D models (not sheets) as roots ([OpenBuildings Speedikon "Best Practices"](https://docs.bentley.com/LiveContent/web/OpenBuildings%20Speedikon%20Help-v4/en/GUID-564AD55B-4552-4351-8A30-A0359DA81B25.html)). The Synchronization API marks a source file as a "spatial root" [V-doc].
- **Reality-data attachments:** `uploadDgnRealityData` (default true) uploads .3sm/.3mx/.pod/raster to the Reality Data API. mirukuru.ibim contains a `ScalableMesh:ScalableMeshModel`, and DGN-derived iModels include `PointCloud`, `Raster`, `ScalableMesh` and `ThreeMx` schemas [V-data/V-doc].

**Units:**
- All coordinates are stored in meters ("All coordinates and distances in an iModel must be stored in meters", [WriteAConnector "Units and Coordinate systems"](https://www.itwinjs.org/learning/writeaconnector/#units-and-coordinate-systems)) [V-doc].
- The bridge records each model's UOR→meter transform in the `Model` aspect JSON [V-data].
- KindOfQuantity display formats: "In most cases the default/active format for a KOQ uses the unit from the source file … If there are multiple source files with different UOMs then I believe the first UOM in will win." (ColinKerr, [#7507](https://github.com/iTwin/itwinjs-core/discussions/7507)) [V-staff].

**GCS:**
- WriteAConnector: if the iModel already has a GCS, transform source data into it; otherwise initialise the iModel's GCS from the source. Subtract an existing global origin [V-doc].
- GeoLocation doc: "some applications such as MicroStation store a GCS, even for a Linear GeoLocation" ([GeoLocation.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/learning/GeoLocation.md)) [V-doc].
- The DGN bridge stores a job-level `Transform` in the job Subject JSON (identity in the samples) [V-data].
- **[Unverified]** The exact GCS reprojection and alignment rules of the MSTN connector (e.g. when it reprojects versus applies a linear transform).

**"ConvertToBIS" options:** **[Unverified]** — no public reference found in docs, discussions, the Synchronization API, or the BIS docs. The only related public knobs are the Synchronization API connector settings above and `ConverterType` (0) in the job Subject JSON.

### 7.5 DWG connector — for comparison

[V-data] from `sim-master.bim` (Civil 3D/DWG connector, 2024), plus [V-doc].

**Layout:**
- `Subject "DwgBridge master model, xref-base.dwg, xref-Base"` (`Type:"SourceMasterModel"`).
- → Job Subject `"Civil3d"`.
- → `DocumentPartition "Imported Sheets"` / `"Imported Drawings"`, `GroupInformationPartition "Imported Groups"`, `DefinitionPartition "DwgDefinitionModel"` (UserLabel "Definitions").
- → `Subject "xref-Base"` (Hierarchy).
- → `PhysicalPartition "xref-Base"` (Master) and `GraphicalPartition3d "Graph Model[xref-Base-0x3000000000b]"`. The second appears when `enableC3dGraphsIn2dModel` puts 2D entities in a `Generic:GraphicalModel3d`.
- Plus a `"Civil Alignment"` Subject (`{"Model":{"Perspective":"Aligned"}}`) with RoadRail domain partitions.
- `RepositoryLink` CodeValue = lower-cased file name, with an `ExternalSource` per file and a `SynchronizationConfigLink`.

**Provenance `Kind`s**, each with `Identifier` = **DWG object handle as hex** (`0xE66A`):

| DWG object | `Kind` | Owner | Scope | JSON / notes |
|---|---|---|---|---|
| Entities | `Element` | — | PhysicalPartition | `Source`→ExternalSource |
| Layers | `Layer` | default SubCategory of a `SpatialCategory` named after the layer, in DictionaryModel | RepositoryLink | `{"LayerType":0,"LayerName":"C-ROAD-CORR","IsMerged":false}` |
| Linetypes | `LineType` | `LineStyle` | — | — |
| Materials | `Material` | `RenderMaterial` | — | — |
| Text styles | `TextStyle` | `AnnotationTextStyle` | — | — |
| Blocks | `GeometryPart` | `GeometryPart` | DefinitionPartition | Identifier `"<BLOCK>[2:e136]-0"` |
| ModelSpace | `Model` | PhysicalPartition | — | `{"Transform":[0.3048006096012192,…],"DwgName":"xref-Base","SourceType":"ModelSpace"}` (US-survey-foot→m) |
| PaperSpace layout | `Model` | `Sheet` "Layout1[xref-Base]" | — | `SourceType "PaperSpace"` |
| ModelSpace viewports | `ViewDefinition` | `SpatialViewDefinition` | — | "a DWG connector creates views from model viewports" (donhfu, [#6078](https://github.com/iTwin/itwinjs-core/discussions/6078)) [V-staff] |
| File | `RepositoryLink` | RepositoryLink | — | `{"LastSaveTime":<julian>,"LastModifiedTime":<unix s>,"FileSize","VersionGuid","UniqueName","DwgName"}` |
| File | `ExternalSource` | ExternalSource | — | — |

**Classes:**
- Dynamic schemas: `AdskCivil3dSchema` (e.g. `AeccParcel` : `AeccBase` : **`BisCore:GraphicalElement3d`**; `AcadEntity2d` : `GraphicalElement3d`).
- `DwgAttributeDefinitions_<file>`: block attributes → `<Block>ElementAspect`.
- `DwgSourceInfo` (`ProxyInformation`).
- Entity UserLabel = DXF type name ("LINE", "LWPOLYLINE", "HATCH").
- Options: `treatDwgModelSpaceAs2d`, `ignoreDgnAttachments` [V-doc].

### 7.6 IFC connector — mostly not public

- Supported: IFC2x3, IFC4, IFC4.3 (`connectorType: IFC`) [V-doc].
- `ignoreIfcGlobalIds` "Ignores IFC global IDs when calculating element provenance". So by default provenance is derived from IFC **GlobalId** [V-doc for the option; Inferred that GlobalId is the default Identifier basis].
- `ExternalSourceGroup` exists for the case where "the same 'Wall', with the same 'unique' id, [appears] in more than one IFC file" ([Provenance-in-BIS.md](https://github.com/iTwin/itwinjs-core/blob/master/docs/bis/domains/Provenance-in-BIS.md)) [V-doc].
- The BIS connector-organization diagram shows an "IFC" Subject with a `ChannelRootAspect` and a child Subject per building/dataset, alongside OBD and Civil channels [V-doc].
- **[Unverified]** Class mapping (IfcWall → ?), spatial-structure mapping (IfcSite/Building/Storey → SpatialComposition / BuildingSpatial?), and property-set handling (aspects vs dynamic classes).

---

## 8. Recommendations for DgnKit (opinion, derived from the above)

1. **Architecture.** Use a C# DgnKit extractor writing an intermediate SQLite store, plus a thin TypeScript connector. Either:
   - (a) fork or port connector-framework's `ConnectorRunner` + `Synchronizer` (~1.6k lines) onto **iTwin.js 5.x** with `EditTxn`; or
   - (b) start on framework 2.4.0 / iTwin.js 4.x knowing it is EOL.
   
   (a) is preferable. The framework's own maintainers describe 4.x as needing a "breaking core 4.x to 5.x migration".
2. **Local development loop.** Use `standalone` mode (incremental and deletion-aware) for development. Use `snapshot` for distributable artefacts. Seed hub iModels by **baseline upload** or by running the connector in `briefcase` mode.
3. **Provenance scheme.** Mirror Bentley's MSTN conventions so data looks familiar to Bentley tooling (`Kind` = `Element` / `Model` / `Level` / `ViewDefinition` / `DocumentWithBeGuid`; Identifier = decimal V8 ElementId / ModelId / LevelId; Version = V8 lastModified ms; Checksum = your content hash). Choose Scope deliberately:
   - RepositoryLink scope → file-based deletion works out of the box.
   - Partition scope (Bentley-like) → requires `scopeToPartition:true` (channel-based deletion) or a custom deleter.
   
   V8 ElementIds are unique per *file*, not per model, so RepositoryLink scope is sufficient for uniqueness [Inferred from DGN V8 format]. Also write `Source` → a per-model `ExternalSource`, and `ExternalSourceAttachment`s for references, as current BIS recommends.
4. **Hierarchy.**
   - Job Subject → Definitions / Groups / Drawings / Sheets partitions.
   - Subject per master/reference file → PhysicalPartition per 3D model, with an ElementHasLinks to its RepositoryLink.
   - Categories per level name, with SubCategory aspects per file.
   - Normal cells → assemblies. Shared cells → GeometryPart + single element when single-level.
   - Item types → `<Lib>` dynamic schema with `<ItemType>ElementAspect`s.
   - Plain graphics → `Generic:PhysicalObject` (3D) / `DrawingGraphic` (2D).
5. **Channel.** Use a private channel key such as `"<org>:dgnkit:<fileKey>"`. Note the one-root-per-key gotcha (§1.8): derive the key per source file.
6. **Hosting.** Expect to run it yourself, on a desktop or a Node 22 container with a service-app token. The Synchronization API will not run it.

---

## 9. Open questions / not verified

- Whether Bentley offers any program to host third-party connectors in the Synchronization service. Nothing public found.
- The current (2025–26) MicroStation connector's exact layout: are there still "IModelBridgeForMstn"-named Subjects? Does it write ExternalSource / ExternalSourceAttachment elements? Has the checksum algorithm changed? Are FederationGuids populated? What class is used for 2D graphics? What are the GCS rules? Has the "Uncategorized"/"Default" naming changed? All evidence here is from 2018–2019 output (JoesHouse.bim, 3dLinestyle.bim). A fresh conversion with iTwin Synchronizer or iTwin Snapshot would settle it.
- What is hashed in Bentley's `Element` checksum.
- "ConvertToBIS" options — no public trace.
- IFC connector class and spatial mapping.
- Whether `StandaloneDb` use is covered by the native right-to-run licence for self-owned data.
- Whether connector-framework changeset groups actually group pushed changesets. The code suggests not.
- Whether iTwin Snapshot is still offered for download.

---

## 10. Source index

**Repositories** (GitHub API, raw):
- `iTwin/connector-framework` @ `main`: `src/{BaseConnector,ConnectorRunner,Synchronizer,Args,ChangeSetGroup,ConnectorAuthenticationManager,ConnectorIssueReporter,SqliteIssueReporter,SyncErrors,iModelConnectorErrors,connector-framework}.ts`, `test/TestConnector/*.ts`, `test/standalone/*.ts`, `test/integration/*.ts`, `wrapper-example/ExampleWrapper.ts`, `README.md`, `CHANGELOG.md`, `package.json`, PR #161.
- `iTwin/connector-samples` (archived): README, `cobie-excel-connector/*` (pcf-based).
- `iTwin/itwinjs-core` @ `master`:
  - `core/backend/src/{ChannelControl,internal/ChannelAdmin,ExternalSource,ElementAspect,LockControl,BriefcaseManager,IModelHost,BackendHubAccess,ElementTreeWalker,TxnManager}.ts`, `core/common/src/{IModel,ElementProps}.ts`, `core/frontend-devtools/src/tools/SourceAspectIdTools.ts`
  - `docs/learning/{WriteAConnector,imodel-connectors,SupportedPlatforms,api-support-policies,GeoLocation,ImplementADynamicSchema}.md`, `docs/learning/backend/{Channel,ConcurrencyControl,EditTxn,AccessingIModels}.md`
  - `docs/bis/domains/Provenance-in-BIS.md`, `docs/bis/guide/{data-organization/information-hierarchy,data-organization/organizing-definition-elements,fundamentals/categories,schema-evolution/schema-customization,physical-perspective/physical-hierarchy-organization,other-perspectives/information-models-and-elements}.md`, `docs/changehistory/{5.0.0,5.15.0,NextVersion}.md`
  - Test iModels listed in §7.
- `iTwin/imodel-native`: README, LICENSE.md (Apache-2.0), `iModelJsNodeAddon/api_package/{installNativePlatform.js,LICENSE.md}`.
- `iTwin/imodel-transformer`: `packages/transformer/{README.md,package.json}`, `docs/learning/transformer/{index,branching-imodels}.md`, `docs/learning/EditTxnInTransformer.md`.
- `iTwin/imodels-clients`: `clients/imodels-client-authoring/src/operations/changeset/ChangesetOperationParams.ts`.
- `iTwin/connector-errors`: README.

**npm registry metadata:** `@itwin/connector-framework`, `@itwin/core-backend`, `@bentley/imodeljs-native`, `@itwin/imodel-transformer`, `@itwin/imodels-access-backend`, `@itwin/imodels-client-authoring`, `@itwin/node-cli-authorization`, `@itwin/service-authorization`, `@itwin/pcf`, `@bentley/imodel-bridge`.

**Web:**
- https://www.itwinjs.org/learning/writeaconnector/
- https://www.itwinjs.org/learning/imodel-connectors/
- https://developer.bentley.com/apis/synchronization/overview/
- https://developer.bentley.com/apis/synchronization/operations/create-storage-connection/
- https://developer.bentley.com/apis/synchronization/supported-formats/
- https://developer.bentley.com/apis/synchronization/operations/patch-connection-configuration/
- https://developer.bentley.com/apis/synchronization/operations/patch-imodel-configuration/
- https://developer.bentley.com/apis/synchronization/changelog/
- https://developer.bentley.com/tutorials/synchronization-storage-tutorial
- https://developer.bentley.com/apis/imodels-v2/overview/
- https://developer.bentley.com/apis/imodels-v2/operations/
- https://developer.bentley.com/apis/imodels-v2/operations/create-imodel/
- https://docs.bentley.com/LiveContent/web/MicroStation%20Help-v24/en/Synchronize.html
- https://www.bentley.com/support/software-developers

**GitHub discussions (iTwin/itwinjs-core):** #1659, #1820, #2559, #2893, #3276, #5396, #6078, #7507, #7776, #8152, #8168, #8483.
