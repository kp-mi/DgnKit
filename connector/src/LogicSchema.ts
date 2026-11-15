/*---------------------------------------------------------------------------------------------
 * Dynamic schema for engineering-logic relationships (DgnKit logic layer, docs/LOGIC.md).
 *
 * Each IMX relationshipClass becomes an ECRelationshipClass in the DgnKitLogic schema, derived from
 * bis:ElementRefersToElements (link table; BisCore maps subclass properties into shared columns), strength
 * "referencing", polymorphic bis:Element ends (0..*), with typed properties (Confidence, Rule, Status, Evidence,
 * Ambiguous + a Checksum the loader uses for change detection — relationships have no ExternalSourceAspect).
 * Like DgnKitDynamic, the schema only grows: classes and properties already in the iModel are kept, new ones added,
 * the minor version bumped. A separate schema keeps relationships and element aspects evolving independently.
 *--------------------------------------------------------------------------------------------*/
import type { IModelDb } from "@itwin/core-backend";
import type { ImxRelationshipClass } from "./imx";
import { toEcName } from "./DynamicSchema";

export const LogicSchemaName = "DgnKitLogic";
export const LogicSchemaAlias = "dgnkl";

type PrimitiveTypeName = "string" | "int" | "long" | "double" | "boolean";
interface RelClassDef { name: string; description: string; sourceRole: string; targetRole: string; props: Map<string, PrimitiveTypeName> }

function xmlEscape(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

/** ECDb reserves these on link-table relationships (BisCore ReservedPropertyNames + system properties). */
const reserved = new Set(["id", "classname", "classfullname", "sourceid", "sourceclassid", "targetid", "targetclassid",
  "ecinstanceid", "ecclassid", "sourceecinstanceid", "targetecinstanceid", "sourceecclassid", "targetecclassid"]);

export class LogicSchemaBuilder {
  private readonly _classes = new Map<string, RelClassDef>();
  private _changed = false;
  private _version: [number, number, number] = [1, 0, 0];

  public get hasClasses(): boolean { return this._classes.size > 0; }
  public get changed(): boolean { return this._changed; }
  public get classNames(): string[] { return [...this._classes.keys()]; }

  public loadExisting(db: IModelDb): void {
    let props: any;
    try {
      props = db.getSchemaProps(LogicSchemaName);
    } catch {
      return;
    }
    const v = String(props.version ?? "01.00.00").split(".").map((x: string) => parseInt(x, 10));
    this._version = [v[0] ?? 1, v[1] ?? 0, v[2] ?? 0];
    for (const [name, item] of Object.entries<any>(props.items ?? {})) {
      if (item.schemaItemType !== "RelationshipClass")
        continue;
      const def: RelClassDef = {
        name, description: item.description ?? "", sourceRole: item.source?.roleLabel ?? name, targetRole: item.target?.roleLabel ?? name, props: new Map(),
      };
      for (const p of item.properties ?? []) def.props.set(p.name, p.typeName as PrimitiveTypeName);
      this._classes.set(name, def);
    }
  }

  public add(rc: ImxRelationshipClass): void {
    const name = toEcName(rc.name, "Relationship");
    let def = this._classes.get(name);
    if (!def) {
      def = { name, description: rc.description ?? "", sourceRole: rc.sourceRole ?? name, targetRole: rc.targetRole ?? name, props: new Map() };
      this._classes.set(name, def);
      this._changed = true;
    }
    for (const [k, t] of Object.entries({ ...rc.properties, Checksum: "string" as const })) {
      let p = toEcName(k, "Property");
      if (reserved.has(p.toLowerCase())) p = "Logic_" + p;
      if (!def.props.has(p)) { def.props.set(p, t); this._changed = true; }
    }
  }

  /** EC property name → JS property name of a relationship instance. */
  public static jsName(ecName: string): string {
    return ecName.length === 0 ? ecName : ecName[0].toLowerCase() + ecName.substring(1);
  }

  public propertyType(className: string, prop: string): PrimitiveTypeName | undefined {
    return this._classes.get(className)?.props.get(prop);
  }

  public toXml(): string {
    const [r, w, m] = this._version;
    const version = `${String(r).padStart(2, "0")}.${String(w).padStart(2, "0")}.${String(this._changed ? m + 1 : m).padStart(2, "0")}`;
    const lines: string[] = [];
    lines.push(`<?xml version="1.0" encoding="UTF-8"?>`);
    lines.push(`<ECSchema schemaName="${LogicSchemaName}" alias="${LogicSchemaAlias}" version="${version}" displayLabel="DgnKit engineering logic" description="Relationships inferred, derived or asserted by the DgnKit engineering logic layer (hosting, connectivity, flow, supply …). Generated; grows between runs." xmlns="http://www.bentley.com/schemas/Bentley.ECXML.3.2">`);
    lines.push(`  <ECSchemaReference name="BisCore" version="01.00.00" alias="bis"/>`);
    lines.push(`  <ECSchemaReference name="CoreCustomAttributes" version="01.00.00" alias="CoreCA"/>`);
    lines.push(`  <ECCustomAttributes><DynamicSchema xmlns="CoreCustomAttributes.01.00.00"/></ECCustomAttributes>`);
    for (const def of this._classes.values()) {
      lines.push(`  <ECRelationshipClass typeName="${def.name}" description="${xmlEscape(def.description)}" modifier="None" strength="referencing" strengthDirection="forward">`);
      lines.push(`    <BaseClass>bis:ElementRefersToElements</BaseClass>`);
      for (const [p, t] of def.props)
        lines.push(`    <ECProperty propertyName="${p}" typeName="${t}"/>`);
      lines.push(`    <Source multiplicity="(0..*)" roleLabel="${xmlEscape(def.sourceRole)}" polymorphic="true"><Class class="bis:Element"/></Source>`);
      lines.push(`    <Target multiplicity="(0..*)" roleLabel="${xmlEscape(def.targetRole)}" polymorphic="true"><Class class="bis:Element"/></Target>`);
      lines.push(`  </ECRelationshipClass>`);
    }
    lines.push(`</ECSchema>`);
    return lines.join("\n");
  }
}
