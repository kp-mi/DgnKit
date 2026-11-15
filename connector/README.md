# dgnkit-imodel-connector

Writes DgnKit's IMX interchange ([docs/IMODEL-INTERCHANGE.md](../docs/IMODEL-INTERCHANGE.md)) into an iModel with
iTwin.js 5.15 (`@itwin/core-backend`, explicit `EditTxn`). Background and mapping: [docs/IMODEL.md](../docs/IMODEL.md).

Needs Node.js 22, 24 or 26 (tested on 26.13.1; iTwin.js 5.15 itself lists 20/22/24, so npm prints an `EBADENGINE`
warning on 26). Windows x64 also needs the Visual C++ 2022 redistributable for the native addon. npm 11.20+ holds
back `@bentley/imodeljs-native`'s install script until it is approved; that is harmless here, because the 5.15
package already ships the Windows binaries (`imodeljs-win32-x64/imodeljs.node`).

```bash
npm install
```

```bash
npm run build
```

```bash
node dist/main.js ../testdata/out/file.imx.jsonl ../testdata/out/file.bim
```

```bash
node dist/main.js file.imx.jsonl file.bim --standalone
```

```bash
node dist/inspect.js file.bim
```

```bash
node dist/verify.js file.imx.jsonl file.bim
```

* default: a **snapshot** iModel, rebuilt on every run;
* `--standalone`: a standalone iModel created once and **updated in place** afterwards — unchanged elements are skipped
  (ExternalSourceAspect checksum), changed ones updated, vanished ones deleted;
* `--name`: root Subject / iModel name (default: source file name);
* `--profile <name>` (all three commands): iTwin.js locks its per-user profile directory
  (`%LOCALAPPDATA%\iModelJs\profiles\<name>`) for the life of the process, so runs **in parallel** need distinct
  profile names; sequential runs can share the default. A run that finds its profile in use stops with exit code 3.

`verify.js` checks an iModel against the IMX it came from:
* every primitive sent is stored (iTwin.js silently drops GeometryStream JSON it rejects — this is what catches it);
* the stored model extents match the IMX extents;
* every logic relationship sent is stored, counted per class.

The exit code is 1 when primitives or relationships are missing.

Engineering-logic results (`dgnkit imodel … --logic`, [docs/LOGIC.md](../docs/LOGIC.md)) arrive as `relationshipClass` /
`relationship` records. They become link-table relationships of the dynamic `DgnKitLogic` schema (`HostedBy`,
`FlowsInto`, `SupportedBy` …, each a `bis:ElementRefersToElements` subclass with `Confidence`, `Rule`, `Status`,
`Evidence`, `Ambiguous`, `Checksum`). With `--standalone` they are updated in place: unchanged (same class, ends and
checksum) skipped, changed updated, no longer inferred deleted.

| file | |
|---|---|
| `src/imx.ts` | IMX record types and streaming reader |
| `src/ImxLoader.ts` | BIS tree, categories, line styles, models, elements, provenance, change detection, views |
| `src/DynamicSchema.ts` | `DgnKitDynamic` schema: one `ElementMultiAspect` class per source property group, additive across runs |
| `src/LogicSchema.ts` | `DgnKitLogic` schema: one `ECRelationshipClass` (ElementRefersToElements subclass) per logic relation, additive |
| `src/main.ts` | CLI |
| `src/inspect.ts` | prints schemas, models, classes, aspects and provenance of any `.bim` |
| `src/verify.ts` | IMX ↔ iModel check: primitive counts and model extents |
| `src/host.ts` | `IModelHost` startup with `--profile` and a clear message when the profile is in use |

Not implemented yet: iModelHub briefcases (auth, locks, channel, push), geometry parts, reference attachments.
