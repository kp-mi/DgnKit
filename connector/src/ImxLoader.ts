/*---------------------------------------------------------------------------------------------
 * Loads an IMX interchange file into an iModel (iTwin.js 5.x, explicit EditTxn).
 *
 * Layout written (BIS "top of the world" rules for connectors — docs/IMODEL-NOTES.md):
 *
 *   RepositoryModel
 *   ├─ RepositoryLink  code = source path, url = file:///…           (one per source document)
 *   ├─ ExternalSource  per source model, repository → RepositoryLink
 *   └─ root Subject
 *      └─ job Subject  "DgnKit:<source path>"                         (everything this job writes lives below it)
 *         ├─ DefinitionPartition "Definitions" → DefinitionModel     categories (one per level & usage), line styles,
 *         │                                                           display styles, selectors, views
 *         ├─ DocumentPartition "Documents" → DocumentListModel       Drawing → DrawingModel, Sheet → SheetModel
 *         └─ Subject <model name> → PhysicalPartition → PhysicalModel   (one physical perspective per Subject)
 *
 * Provenance: every element / model / category gets a bis:ExternalSourceAspect {Scope = RepositoryLink,
 * Source = ExternalSource, Kind, Identifier, Version, Checksum}. Re-running against a StandaloneDb updates in place:
 * unchanged checksums are skipped, changed elements updated, unseen ones deleted (file-based deletion detection).
 *--------------------------------------------------------------------------------------------*/
import * as path from "node:path";
import { pathToFileURL } from "node:url";
import {
  CategorySelector, DefinitionModel, DisplayStyle2d, DisplayStyle3d, DocumentListModel, Drawing, DrawingCategory,
  DrawingViewDefinition, EditTxn, ExternalSource, ExternalSourceAspect, GenericSchema, IModelDb, LineStyleDefinition,
  ModelSelector, OrthographicViewDefinition, PhysicalModel, RepositoryLink, Sheet, SheetModel, SheetViewDefinition,
  SpatialCategory, Subject, withEditTxn,
} from "@itwin/core-backend";
import { Id64, Id64String, Logger } from "@itwin/core-bentley";
import {
  Code, ColorDef, FontType, IModel, LinePixels, QueryBinder, QueryRowFormat, SubCategoryAppearance,
} from "@itwin/core-common";
import { Range2d, Range3d } from "@itwin/core-geometry";
import { DynamicSchemaBuilder, DynamicSchemaName, toEcName } from "./DynamicSchema";
import { LogicSchemaBuilder, LogicSchemaName } from "./LogicSchema";
import {
  ImxCategory, ImxElement, ImxHeader, ImxLineStyle, ImxModel, ImxPart, ImxRecord, ImxRelationship, ImxSource, ImxSubCategory, readImx,
} from "./imx";

const loggerCategory = "DgnKit.Connector";
export const ConnectorName = "DgnKit";
export const ConnectorVersion = "0.1.0";

export interface LoadReport {
  inserted: number;
  updated: number;
  unchanged: number;
  deleted: number;
  models: number;
  categories: number;
  /** Logic-layer link-table relationships (DgnKitLogic). */
  relationships: { inserted: number; updated: number; unchanged: number; deleted: number; unresolved: number };
  warnings: string[];
}

interface ProvenanceEntry { aspectId: Id64String; elementId: Id64String; kind: string; checksum?: string; version?: string; className: string }

/** The characters BIS does not allow in category / sub-category code values. */
const invalidCategoryChars = /[<>\\/.\"?*|,='&]/g;

function categoryName(name: string): string {
  const s = name.replace(invalidCategoryChars, "_").trim();
  return s.length > 0 ? s : "Unnamed";
}

export class ImxLoader {
  private readonly _db: IModelDb;
  private readonly _imx: string;
  private _txn!: EditTxn;
  private _header!: ImxHeader;
  private readonly _schema = new DynamicSchemaBuilder();
  private readonly _fonts = new Map<string, number>();
  private readonly _styles = new Map<string, Id64String>();
  private readonly _categories = new Map<string, { spatial?: Id64String; drawing?: Id64String; rec: ImxCategory }>();
  private readonly _subCategories = new Map<string, Id64String>();
  private readonly _models = new Map<string, { id: Id64String; rec: ImxModel; source: Id64String }>();
  private readonly _parts = new Map<string, Id64String>();
  private readonly _elements = new Map<string, Id64String>();
  private readonly _prov = new Map<string, ProvenanceEntry[]>(); // kind|identifier → existing aspects
  private readonly _seen = new Set<Id64String>();
  private readonly _report: LoadReport = {
    inserted: 0, updated: 0, unchanged: 0, deleted: 0, models: 0, categories: 0,
    relationships: { inserted: 0, updated: 0, unchanged: 0, deleted: 0, unresolved: 0 }, warnings: [],
  };
  private readonly _logic = new LogicSchemaBuilder();
  /** existing logic relationships of this job: class|source|target → id, checksum */
  private readonly _logicExisting = new Map<string, { id: Id64String; classFullName: string; sourceId: Id64String; targetId: Id64String; checksum?: string }>();
  private readonly _logicSeen = new Set<string>();
  private _jobSubject!: Id64String;
  private _definitionModel!: Id64String;
  private _documentList?: Id64String;
  private _repositoryLink!: Id64String;
  private _sinceSave = 0;

  public constructor(db: IModelDb, imxPath: string) {
    this._db = db;
    this._imx = imxPath;
  }

  /** Reads the IMX header and the information needed before writing (dynamic schema classes, fonts). */
  public static async readHeader(imxPath: string): Promise<ImxHeader> {
    for await (const rec of readImx(imxPath))
      if (rec.kind === "header")
        return rec;
    throw new Error(`${imxPath}: no header`);
  }

  public async run(): Promise<LoadReport> {
    // ---- pass 1: schema + fonts (async work that must happen outside the explicit edit transaction)
    this._schema.loadExisting(this._db);
    this._logic.loadExisting(this._db);
    const fonts: Array<{ key: string; name: string; type: FontType }> = [];
    for await (const rec of readImx(this._imx)) {
      if (rec.kind === "header") this._header = rec;
      else if (rec.kind === "font") fonts.push({ key: rec.key, name: rec.name, type: rec.type === "rsc" ? FontType.Rsc : rec.type === "shx" ? FontType.Shx : FontType.TrueType });
      else if (rec.kind === "element") for (const g of rec.properties ?? []) this._schema.add(g);
      else if (rec.kind === "relationshipClass") this._logic.add(rec);
    }
    await this.importSchemas();
    // fonts already mapped are just looked up; new ones are assigned ids in their own (saved) transaction, because an
    // EditTxn cannot start while there are unsaved changes
    const newFonts = fonts.filter((f) => {
      const id = this._db.fonts.findId({ name: f.name, type: f.type });
      if (id !== undefined) this._fonts.set(f.key, id);
      return id === undefined;
    });
    if (newFonts.length > 0)
      await withEditTxn(this._db, "DgnKit: fonts", async () => {
        for (const f of newFonts)
          this._fonts.set(f.key, await this._db.fonts.acquireId({ name: f.name, type: f.type }));
      });

    // ---- pass 2: everything else in one explicit transaction (saved periodically)
    this._txn = new EditTxn(this._db, `${ConnectorName}: ${this._header.source.name}`);
    this._txn.start();
    try {
      this.setUpJob();
      await this.loadProvenance();
      await this.loadLogicRelationships();
      for await (const rec of readImx(this._imx))
        this.process(rec);
      this.deleteUnseenRelationships();
      this.deleteUnseen();
      this.createViews();
      this.updateProjectExtents();
      this._txn.end("save", `${ConnectorName}: ${this._header.source.name}`);
    } catch (e) {
      this._txn.end("abandon");
      throw e;
    }
    return this._report;
  }

  // ------------------------------------------------------------------ schemas

  private async importSchemas(): Promise<void> {
    if (this._db.querySchemaVersion("Generic") === undefined)
      await this._db.importSchemas([GenericSchema.schemaFilePath]);
    if (this._schema.hasClasses && (this._schema.changed || this._db.querySchemaVersion(DynamicSchemaName) === undefined))
      await this._db.importSchemaStrings([this._schema.toXml()]);
    if (this._logic.hasClasses && (this._logic.changed || this._db.querySchemaVersion(LogicSchemaName) === undefined))
      await this._db.importSchemaStrings([this._logic.toXml()]);
  }

  // ------------------------------------------------------------------ job structure

  private setUpJob(): void {
    const h = this._header;
    const txn = this._txn;
    const db = this._db;
    const docId = h.source.path || h.source.name;

    // RepositoryLink for the source document (code = document id, as connector-framework does)
    const linkCode = RepositoryLink.createCode(db, IModel.repositoryModelId, docId);
    this._repositoryLink = db.elements.queryElementIdByCode(linkCode) ?? txn.insertElement({
      classFullName: RepositoryLink.classFullName,
      model: IModel.repositoryModelId,
      code: linkCode,
      url: h.source.path ? pathToFileURL(h.source.path).href : undefined,
      userLabel: h.source.name,
      format: h.source.format,
      description: `${h.source.format} ${h.source.formatVersion ?? ""}`.trim(),
    } as any);

    // job subject
    const subjectCode = Subject.createCode(db, IModel.rootSubjectId, `${ConnectorName}:${docId}`);
    const existing = db.elements.queryElementIdByCode(subjectCode);
    if (existing) this._jobSubject = existing;
    else {
      this._jobSubject = txn.insertElement({
        classFullName: Subject.classFullName,
        model: IModel.repositoryModelId,
        parent: { id: IModel.rootSubjectId, relClassName: "BisCore:SubjectOwnsSubjects" },
        code: subjectCode,
        userLabel: h.source.name,
        description: `${h.source.format} file converted by ${ConnectorName}`,
        jsonProperties: { Subject: { Job: { Connector: ConnectorName, Properties: { ConnectorVersion, ConnectorType: "DgnKitImx" } } } },
      } as any);
    }
    this._definitionModel = this.partitionModel("BisCore:DefinitionPartition", "Definitions",
      () => DefinitionModel.insert(txn, this._jobSubject, "Definitions"));
  }

  /** Finds a partition below the job subject or creates it with its sub-model. */
  private partitionModel(partitionClass: string, name: string, create: () => Id64String, parent: Id64String = this._jobSubject): Id64String {
    const code = new Code({ spec: this._db.codeSpecs.getByName("bis:InformationPartitionElement").id, scope: parent, value: name });
    const id = this._db.elements.queryElementIdByCode(code);
    if (id)
      return id;
    const modelId = create();
    // ElementHasLinks: the partition originates from the source document
    this._txn.insertRelationship({ classFullName: "BisCore:ElementHasLinks", sourceId: modelId, targetId: this._repositoryLink });
    void partitionClass;
    return modelId;
  }

  private documentList(): Id64String {
    if (!this._documentList)
      this._documentList = this.partitionModel("BisCore:DocumentPartition", "Documents",
        () => DocumentListModel.insert(this._txn, this._jobSubject, "Documents"));
    return this._documentList;
  }

  // ------------------------------------------------------------------ provenance

  private async loadProvenance(): Promise<void> {
    const reader = this._db.createQueryReader(
      `SELECT a.ECInstanceId, a.Element.Id, a.Kind, a.Identifier, a.Checksum, a.Version, ec_classname(e.ECClassId, 's:c')
         FROM BisCore.ExternalSourceAspect a JOIN BisCore.Element e ON e.ECInstanceId = a.Element.Id
        WHERE a.Scope.Id = ?`,
      QueryBinder.from([this._repositoryLink]), { rowFormat: QueryRowFormat.UseECSqlPropertyIndexes });
    for (const row of await reader.toArray()) {
      const key = `${row[2]}|${row[3]}`;
      const list = this._prov.get(key) ?? [];
      list.push({ aspectId: row[0], elementId: row[1], kind: row[2], checksum: row[4] ?? undefined, version: row[5] ?? undefined, className: row[6] });
      this._prov.set(key, list);
    }
  }

  private findProvenance(src: ImxSource, className?: string): ProvenanceEntry | undefined {
    const list = this._prov.get(`${src.kind}|${src.id}`);
    if (!list) return undefined;
    return className ? list.find((p) => p.className.toLowerCase() === className.toLowerCase()) : list[0];
  }

  private aspectProps(elementId: Id64String, src: ImxSource, checksum: string | undefined, version: string | undefined, sourceId?: Id64String) {
    return {
      classFullName: ExternalSourceAspect.classFullName,
      element: { id: elementId, relClassName: "BisCore:ElementOwnsExternalSourceAspects" },
      scope: { id: this._repositoryLink, relClassName: "BisCore:ElementScopesExternalSourceIdentifier" },
      source: sourceId ? { id: sourceId, relClassName: "BisCore:ElementIsFromSource" } : undefined,
      kind: src.kind,
      identifier: src.id,
      checksum,
      version,
      jsonProperties: src.info ? JSON.stringify(src.info) : undefined,
    } as any;
  }

  /**
   * Insert-or-update helper: looks up the provenance of `src`, skips unchanged content, updates changed content,
   * inserts new content. Returns the element id.
   */
  private sync(src: ImxSource, checksum: string, version: string | undefined, className: string, sourceId: Id64String | undefined,
    makeProps: () => Record<string, unknown>, afterWrite?: (id: Id64String, isNew: boolean) => void): Id64String {
    const prev = this.findProvenance(src, className);
    if (prev) {
      this._seen.add(prev.elementId);
      if (prev.checksum === checksum) {
        this._report.unchanged++;
        return prev.elementId;
      }
      this._txn.updateElement({ ...makeProps(), id: prev.elementId } as any);
      this._txn.updateAspect({ ...this.aspectProps(prev.elementId, src, checksum, version, sourceId), id: prev.aspectId });
      afterWrite?.(prev.elementId, false);
      this._report.updated++;
      return prev.elementId;
    }
    const id = this._txn.insertElement(makeProps() as any);
    this._txn.insertAspect(this.aspectProps(id, src, checksum, version, sourceId));
    this._seen.add(id);
    afterWrite?.(id, true);
    this._report.inserted++;
    if (++this._sinceSave >= 20000) {
      this._txn.saveChanges(`${ConnectorName}: progress`);
      this._sinceSave = 0;
    }
    return id;
  }

  // ------------------------------------------------------------------ records

  private process(rec: ImxRecord): void {
    switch (rec.kind) {
      case "header": case "font": return;
      case "lineStyle": return this.lineStyle(rec);
      case "category": return this.category(rec);
      case "subCategory": return this.subCategory(rec);
      case "model": return this.model(rec);
      case "part": return this.part(rec);
      case "element": return this.element(rec);
      case "relationshipClass": return;
      case "relationship": return this.relationship(rec);
    }
  }

  private lineStyle(rec: ImxLineStyle): void {
    let id: Id64String | undefined;
    if (rec.linePixels !== undefined && rec.linePixels >= 1 && rec.linePixels <= 7) {
      const pixels = [LinePixels.Code1, LinePixels.Code2, LinePixels.Code3, LinePixels.Code4, LinePixels.Code5, LinePixels.Code6, LinePixels.Code7][rec.linePixels - 1];
      id = LineStyleDefinition.Utils.getOrCreateLinePixelsStyle(this._db, this._definitionModel, pixels, this._txn);
    } else if (rec.strokes && rec.strokes.length > 0) {
      const name = categoryName(rec.name);
      id = this._db.elements.queryElementIdByCode(new Code({ spec: this._db.codeSpecs.getByName("bis:LineStyle").id, scope: this._definitionModel, value: name }));
      if (!id) {
        const comp = LineStyleDefinition.Utils.createStrokePatternComponent(this._txn, {
          descr: rec.name,
          phase: rec.phase ?? 0,
          strokes: rec.strokes.map((s) => ({
            length: s.length,
            strokeMode: s.dash ? LineStyleDefinition.StrokeMode.Dash : LineStyleDefinition.StrokeMode.Gap,
            ...(s.width ? { orgWidth: s.width, endWidth: s.width, widthMode: LineStyleDefinition.StrokeWidth.Full } : {}),
          })),
        });
        id = LineStyleDefinition.Utils.createStyle(this._txn, this._definitionModel, name, comp);
      }
    } // else: continuous (no style element needed; elements keep the default solid line)
    if (rec.approximation)
      this._report.warnings.push(`line style '${rec.name}': ${rec.approximation}`);
    if (id) this._styles.set(rec.key, id);
  }

  private styleId(key: string | undefined): Id64String | undefined {
    if (key === undefined) return undefined;
    if (key === "ls:0") return Id64.invalid;
    return this._styles.get(key) ?? Id64.invalid;
  }

  private appearance(a: ImxCategory["appearance"]): SubCategoryAppearance.Props {
    return {
      color: a.color, weight: a.weight, style: this.styleId(a.style), priority: a.priority ?? 0, transp: a.transparency ?? 0,
      invisible: a.invisible ?? false, dontPlot: a.dontPlot ?? false, dontLocate: a.dontLocate ?? false,
    };
  }

  private category(rec: ImxCategory): void {
    const entry: { spatial?: Id64String; drawing?: Id64String; rec: ImxCategory } = { rec };
    for (const usage of rec.usage) {
      const isSpatial = usage === "spatial";
      const className = isSpatial ? SpatialCategory.classFullName : DrawingCategory.classFullName;
      const name = categoryName(rec.name);
      const app = this.appearance(rec.appearance);
      const id = this.sync(rec.source, rec.checksum, undefined, className, undefined,
        () => ({
          classFullName: className, model: this._definitionModel,
          code: (isSpatial ? SpatialCategory : DrawingCategory).createCode(this._db, this._definitionModel, this.uniqueCategoryName(name, isSpatial)),
          description: rec.description, rank: 2 /* Rank.User */,
        }),
        (catId) => {
          // the default sub-category carries the level symbology
          const sub = this._db.elements.getElementProps(IModel.getDefaultSubCategoryId(catId));
          this._txn.updateElement({ ...sub, appearance: app } as any);
        });
      if (isSpatial) entry.spatial = id; else entry.drawing = id;
      this._report.categories++;
    }
    this._categories.set(rec.key, entry);
  }

  private readonly _usedCategoryNames = new Set<string>();
  /** Level names that collide after removing invalid characters get a numeric suffix. */
  private uniqueCategoryName(name: string, spatial: boolean): string {
    let n = name;
    for (let i = 2; this._usedCategoryNames.has(`${spatial}|${n.toLowerCase()}`); i++) n = `${name} (${i})`;
    this._usedCategoryNames.add(`${spatial}|${n.toLowerCase()}`);
    return n;
  }

  private subCategory(rec: ImxSubCategory): void {
    const cat = this._categories.get(rec.category);
    for (const catId of [cat?.spatial, cat?.drawing]) {
      if (!catId) continue;
      const code = new Code({ spec: this._db.codeSpecs.getByName("bis:SubCategory").id, scope: catId, value: categoryName(rec.name) });
      const id = this._db.elements.queryElementIdByCode(code) ?? this._txn.insertElement({
        classFullName: "BisCore:SubCategory", model: this._definitionModel, parent: { id: catId, relClassName: "BisCore:CategoryOwnsSubCategories" },
        code, appearance: this.appearance(rec.appearance),
      } as any);
      this._subCategories.set(`${rec.key}|${catId}`, id);
    }
  }

  private model(rec: ImxModel): void {
    const txn = this._txn;
    const name = rec.name;
    // ExternalSource for the source model (DGN model ↔ ExternalSource)
    const sourceId = this.sync({ kind: "ModelSource", id: rec.source.id }, rec.checksum, undefined, ExternalSource.classFullName, undefined, () => ({
      classFullName: ExternalSource.classFullName, model: IModel.repositoryModelId, code: Code.createEmpty(), userLabel: `${this._header.source.name}, ${name}`,
      repository: { id: this._repositoryLink, relClassName: "BisCore:ExternalSourceIsInRepository" },
      connectorName: ConnectorName, connectorVersion: ConnectorVersion,
    }));

    let modelId: Id64String;
    const info = { ...rec.source.info, name, masterUnit: rec.masterUnit, metersPerMaster: rec.metersPerMaster };
    const modelSrc: ImxSource = { kind: "Model", id: rec.source.id, info };
    if (rec.type === "physical") {
      const prev = this.findProvenance(modelSrc);
      if (prev) { modelId = prev.elementId; this._seen.add(modelId); }
      else {
        const subject = txn.insertElement({
          classFullName: Subject.classFullName, model: IModel.repositoryModelId, code: Subject.createCode(this._db, this._jobSubject, name),
          parent: { id: this._jobSubject, relClassName: "BisCore:SubjectOwnsSubjects" }, description: rec.description,
        } as any);
        modelId = PhysicalModel.insert(txn, subject, name);
        txn.insertRelationship({ classFullName: "BisCore:ElementHasLinks", sourceId: modelId, targetId: this._repositoryLink });
        txn.insertAspect(this.aspectProps(modelId, modelSrc, rec.checksum, undefined, sourceId));
        this._seen.add(modelId);
      }
    } else if (rec.type === "drawing") {
      const prev = this.findProvenance(modelSrc);
      if (prev) { modelId = prev.elementId; this._seen.add(modelId); }
      else {
        modelId = Drawing.insert(txn, this.documentList(), this.uniqueDocumentName(name));
        txn.insertAspect(this.aspectProps(modelId, modelSrc, rec.checksum, undefined, sourceId));
        this._seen.add(modelId);
      }
    } else {
      const prev = this.findProvenance(modelSrc);
      if (prev) { modelId = prev.elementId; this._seen.add(modelId); }
      else {
        const docList = this.documentList();
        const ext = rec.extents ? Range3d.fromJSON(rec.extents) : undefined;
        const [w, h] = rec.sheetSize ?? (ext && !ext.isNull ? [ext.xLength(), ext.yLength()] : [0.841, 0.594]);
        const sheetId = txn.insertElement({
          classFullName: Sheet.classFullName, model: docList, code: Sheet.createCode(this._db, docList, this.uniqueDocumentName(name)),
          width: w, height: h, scale: 1, description: rec.description,
        } as any);
        txn.insertModel({ classFullName: SheetModel.classFullName, modeledElement: { id: sheetId } } as any);
        modelId = sheetId;
        txn.insertAspect(this.aspectProps(modelId, modelSrc, rec.checksum, undefined, sourceId));
        this._seen.add(modelId);
      }
    }
    this._models.set(rec.key, { id: modelId, rec, source: sourceId });
    this._report.models++;
  }

  private readonly _documentNames = new Set<string>();
  private uniqueDocumentName(name: string): string {
    let n = name;
    for (let i = 2; this._documentNames.has(n.toLowerCase()); i++) n = `${name} (${i})`;
    this._documentNames.add(n.toLowerCase());
    return n;
  }

  private part(rec: ImxPart): void {
    const id = this.sync(rec.source, rec.checksum, undefined, "BisCore:GeometryPart", undefined, () => ({
      classFullName: "BisCore:GeometryPart", model: this._definitionModel,
      code: new Code({ spec: this._db.codeSpecs.getByName("bis:GeometryPart").id, scope: this._definitionModel, value: `${rec.name} [${rec.source.id}]` }),
      geom: this.resolveGeometry(rec.geometry as any[], undefined, rec.is3d),
    }));
    this._parts.set(rec.key, id);
  }

  private element(rec: ImxElement): void {
    const model = this._models.get(rec.model);
    const cat = this._categories.get(rec.category);
    if (!model || !cat) {
      this._report.warnings.push(`element ${rec.key}: unknown model or category`);
      return;
    }
    const is3d = model.rec.type === "physical";
    const categoryId = is3d ? cat.spatial : cat.drawing;
    if (!categoryId) {
      this._report.warnings.push(`element ${rec.key}: category ${rec.category} has no ${is3d ? "spatial" : "drawing"} usage`);
      return;
    }
    const className = is3d ? (rec.role === "annotation" ? "Generic:Graphic3d" : "Generic:PhysicalObject") : "BisCore:DrawingGraphic";
    const parentId = rec.parent ? this._elements.get(rec.parent) : undefined;
    const o = rec.origin as number[];
    const props = () => ({
      classFullName: className,
      model: model.id,
      category: categoryId,
      code: Code.createEmpty(),
      userLabel: rec.userLabel,
      parent: parentId ? { id: parentId, relClassName: "BisCore:ElementOwnsChildElements" } : undefined,
      placement: is3d ? { origin: [o[0], o[1], o[2] ?? 0], angles: {} } : { origin: [o[0], o[1]], angle: 0 },
      geom: this.resolveGeometry(rec.geometry, categoryId, is3d),
    });
    // Bentley's DGN connector stores the V8 last-modified time as decimal milliseconds since 1970 in Version
    const ms = rec.modified ? Date.parse(rec.modified) : NaN;
    const version = Number.isFinite(ms) ? String(ms) : undefined;
    const id = this.sync(rec.source, rec.checksum, version, className, model.source, props, (elId, isNew) => {
      if (!isNew) // replace the business-property aspects
        for (const a of this._db.elements.getAspects(elId, "BisCore:ElementMultiAspect"))
          if (a.classFullName.startsWith(`${DynamicSchemaName}:`) || a.classFullName.startsWith(`${DynamicSchemaName}.`))
            this._txn.deleteAspect(a.id);
      for (const g of rec.properties ?? [])
        this._txn.insertAspect(this._schema.aspectProps(g, elId) as any);
    });
    this._elements.set(rec.key, id);
  }

  /** Replaces IMX keys (font, line style, sub-category, part) with iModel ids and makes 2D streams strictly 2D. */
  private resolveGeometry(geometry: Array<Record<string, any>>, categoryId: Id64String | undefined, is3d: boolean): any[] {
    return geometry.map((entry) => {
      if (entry.appearance) {
        const a = { ...entry.appearance };
        if (a.style !== undefined) a.style = this.styleId(a.style);
        if (a.subCategory !== undefined) {
          const sub = categoryId ? this._subCategories.get(`${a.subCategory}|${categoryId}`) : undefined;
          if (sub) a.subCategory = sub; else delete a.subCategory;
        }
        return { appearance: a };
      }
      if (entry.textString) {
        const t = { ...entry.textString, font: this._fonts.get(entry.textString.font) ?? 0 };
        return { textString: t };
      }
      if (entry.geomPart)
        return { geomPart: { ...entry.geomPart, part: this._parts.get(entry.geomPart.part) ?? Id64.invalid } };
      void is3d;
      return entry;
    });
  }

  // ------------------------------------------------------------------ logic relationships (DgnKitLogic)

  /**
   * Existing logic relationships whose source element belongs to this job (has provenance in its scope). Link-table
   * relationships carry no ExternalSourceAspect, so identity is (class, source, target) and change detection uses the
   * Checksum property; one row per (class, source, target) — the link table's uniqueness rule.
   */
  private async loadLogicRelationships(): Promise<void> {
    if (this._db.querySchemaVersion(LogicSchemaName) === undefined)
      return;
    const jobElements = new Set<Id64String>();
    for (const list of this._prov.values()) for (const p of list) jobElements.add(p.elementId);
    if (jobElements.size === 0)
      return;
    const reader = this._db.createQueryReader(
      `SELECT r.ECInstanceId, ec_classname(r.ECClassId, 's:c'), r.SourceECInstanceId, r.TargetECInstanceId
         FROM bis.ElementRefersToElements r WHERE ec_classname(r.ECClassId, 's') = '${LogicSchemaName}'`,
      undefined, { rowFormat: QueryRowFormat.UseECSqlPropertyIndexes });
    const rows = await reader.toArray();
    for (const row of rows) {
      const [id, cls, src, tgt] = row as [Id64String, string, Id64String, Id64String];
      if (!jobElements.has(src))
        continue;
      let checksum: string | undefined;
      try {
        checksum = (this._db.relationships.getInstanceProps(cls.replace(":", "."), id) as any).checksum;
      } catch { /* class without checksum */ }
      this._logicExisting.set(`${cls}|${src}|${tgt}`, { id, classFullName: cls, sourceId: src, targetId: tgt, checksum });
    }
  }

  private relationship(rec: ImxRelationship): void {
    const sourceId = this._elements.get(rec.source);
    const targetId = this._elements.get(rec.target);
    if (!sourceId || !targetId) {
      this._report.relationships.unresolved++;
      return;
    }
    const cls = toEcName(rec.class, "Relationship");
    const classFullName = `${LogicSchemaName}:${cls}`;
    const key = `${classFullName}|${sourceId}|${targetId}`;
    if (this._logicSeen.has(key)) {
      this._report.warnings.push(`relationship ${rec.key}: duplicate of an earlier ${cls} between the same elements (ignored)`);
      return;
    }
    this._logicSeen.add(key);
    const props: Record<string, unknown> = { classFullName, sourceId, targetId, checksum: rec.checksum };
    for (const [k, v] of Object.entries(rec.properties ?? {})) {
      let p = toEcName(k, "Property");
      if (["id", "classname", "classfullname", "sourceid", "sourceclassid", "targetid", "targetclassid"].includes(p.toLowerCase())) p = "Logic_" + p;
      const t = this._logic.propertyType(cls, p);
      if (v === null || v === undefined || t === undefined) continue;
      props[LogicSchemaBuilder.jsName(p)] = t === "string" ? (typeof v === "string" ? v : JSON.stringify(v)) : v;
    }
    const prev = this._logicExisting.get(key);
    if (prev) {
      if (prev.checksum === rec.checksum) { this._report.relationships.unchanged++; return; }
      this._txn.updateRelationship({ ...props, id: prev.id } as any);
      this._report.relationships.updated++;
      return;
    }
    this._txn.insertRelationship(props as any);
    this._report.relationships.inserted++;
  }

  /** Relationships this job wrote earlier but no longer infers (or whose ends are gone) are removed. */
  private deleteUnseenRelationships(): void {
    for (const [key, r] of this._logicExisting) {
      if (this._logicSeen.has(key)) continue;
      try {
        this._txn.deleteRelationship({ id: r.id, classFullName: r.classFullName, sourceId: r.sourceId, targetId: r.targetId });
        this._report.relationships.deleted++;
      } catch { /* already removed with an end element */ }
    }
  }

  // ------------------------------------------------------------------ deletions, views, extents

  /** File-based deletion detection: anything this document produced earlier but did not produce now is removed. */
  private deleteUnseen(): void {
    // only graphic content; definitions (categories, styles) and models are kept — they may still be referenced
    const kinds = new Set(["Element", "ElementLevelPart"]);
    const doomed: Id64String[] = [];
    for (const list of this._prov.values())
      for (const p of list)
        if (kinds.has(p.kind) && !this._seen.has(p.elementId))
          doomed.push(p.elementId);
    for (const id of doomed) {
      if (this._db.elements.tryGetElementProps(id) === undefined) continue; // already removed with its parent
      this._txn.deleteElement(id);
      this._report.deleted++;
    }
  }

  private createViews(): void {
    const txn = this._txn;
    const db = this._db;
    const defModel = this._definitionModel;
    const exists = (name: string) => db.elements.queryElementIdByCode(new Code({ spec: db.codeSpecs.getByName("bis:ViewDefinition").id, scope: defModel, value: name })) !== undefined;

    const spatialModels = [...this._models.values()].filter((m) => m.rec.type === "physical");
    const spatialCats = [...this._categories.values()].map((c) => c.spatial).filter((x): x is string => x !== undefined);
    if (spatialModels.length > 0 && spatialCats.length > 0 && !exists(`${this._header.source.name} - 3D`)) {
      const ext = Range3d.createNull();
      for (const m of spatialModels) if (m.rec.extents) ext.extendRange(Range3d.fromJSON(m.rec.extents));
      if (ext.isNull) ext.extendXYZ(0, 0, 0);
      ext.expandInPlace(Math.max(1, ext.maxLength() * 0.02));
      const ms = ModelSelector.insert(txn, defModel, `${this._header.source.name} - 3D`, spatialModels.map((m) => m.id));
      const cs = CategorySelector.insert(txn, defModel, `${this._header.source.name} - 3D`, spatialCats);
      const ds = DisplayStyle3d.insert(txn, defModel, `${this._header.source.name} - 3D`, { backgroundColor: ColorDef.black });
      OrthographicViewDefinition.insert(txn, defModel, `${this._header.source.name} - 3D`, ms, cs, ds, ext);
    }
    const drawingCats = [...this._categories.values()].map((c) => c.drawing).filter((x): x is string => x !== undefined);
    for (const m of this._models.values()) {
      if (m.rec.type === "physical" || drawingCats.length === 0) continue;
      const name = `${m.rec.name} - ${m.rec.type}`;
      if (exists(name)) continue;
      const e = m.rec.extents ? Range3d.fromJSON(m.rec.extents) : Range3d.createXYZXYZ(0, 0, 0, 1, 1, 0);
      const r = Range2d.createXYXY(e.low.x, e.low.y, e.high.x, e.high.y);
      r.expandInPlace(Math.max(0.01, Math.max(r.xLength(), r.yLength()) * 0.02));
      const cs = CategorySelector.insert(txn, defModel, name, drawingCats);
      const ds = DisplayStyle2d.insert(txn, defModel, name);
      if (m.rec.type === "sheet")
        SheetViewDefinition.insert(txn, { definitionModelId: defModel, name, baseModelId: m.id, categorySelectorId: cs, displayStyleId: ds, range: r });
      else
        DrawingViewDefinition.insert(txn, defModel, name, m.id, cs, ds, r);
    }
  }

  private updateProjectExtents(): void {
    const e = this._header.spatialExtents;
    if (!e) return;
    const ext = Range3d.fromJSON(e);
    if (ext.isNull) return;
    if (this._prov.size > 0) // updating: keep what earlier runs (or other jobs) already covered
      ext.extendRange(this._db.projectExtents);
    ext.expandInPlace(Math.max(1, ext.maxLength() * 0.05));
    this._txn.updateProjectExtents(ext);
  }
}

export function logInfo(msg: string): void {
  Logger.logInfo(loggerCategory, msg);
  process.stdout.write(msg + "\n");
}

export function baseName(p: string): string {
  return path.basename(p);
}
