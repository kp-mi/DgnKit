/*---------------------------------------------------------------------------------------------
 * Dynamic schema for source business properties (EC instances, item types, tags, linkages).
 *
 * BIS guidance: user-defined / source-specific properties belong in ElementAspects of a *dynamic* Application-layer
 * schema (not in JsonProperties, which generic UI does not show). Bentley's DGN connector does the same
 * (DgnCustomItemTypes_<lib>:<Type>ElementAspect). Each property group class becomes an ElementMultiAspect subclass with
 * typed primitive properties. The schema only ever grows: existing classes/properties are kept, new ones are added and
 * the minor version is bumped (dynamic schemas must stay additive across connector runs).
 *--------------------------------------------------------------------------------------------*/
import type { IModelDb } from "@itwin/core-backend";
import type { ImxPropertyGroup } from "./imx";

export const DynamicSchemaName = "DgnKitDynamic";
export const DynamicSchemaAlias = "dgnkd";

export type PrimitiveTypeName = "string" | "int" | "long" | "double" | "boolean";

interface PropertyDef { name: string; label: string; type: PrimitiveTypeName }
interface ClassDef { name: string; label: string; description: string; props: Map<string, PropertyDef> /* by source key */; names: Set<string> }

/** Names that ECDb/BIS reserve on aspects (system properties and ElementMultiAspect members). */
const reserved = new Set(["id", "ecinstanceid", "ecclassid", "element", "sourceecinstanceid", "targetecinstanceid", "sourceecclassid", "targetecclassid",
  "classfullname", "jsonproperties"]); // the last two collide with ElementAspectProps members once lower-cased

/** Turns any text into a valid EC name: [A-Za-z_][A-Za-z0-9_]*, at most 80 characters. */
export function toEcName(text: string, fallback = "Unnamed"): string {
  let s = (text ?? "").normalize("NFKD").replace(/[^A-Za-z0-9_]+/g, "_").replace(/_+/g, "_").replace(/^_+|_+$/g, "");
  if (s.length === 0)
    s = fallback;
  if (!/^[A-Za-z_]/.test(s))
    s = "_" + s;
  return s.length > 80 ? s.substring(0, 80) : s;
}

function xmlEscape(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

/** Narrowest EC type that can hold every value seen so far. */
function widen(current: PrimitiveTypeName | undefined, value: unknown): PrimitiveTypeName | undefined {
  let t: PrimitiveTypeName | undefined;
  if (value === null || value === undefined)
    return current;
  if (typeof value === "boolean")
    t = "boolean";
  else if (typeof value === "number")
    t = Number.isInteger(value) ? (Math.abs(value) <= 2147483647 ? "int" : "long") : "double";
  else
    t = "string";
  if (current === undefined || current === t)
    return t;
  const numeric = (x: PrimitiveTypeName) => x === "int" || x === "long" || x === "double";
  if (numeric(current) && numeric(t))
    return current === "double" || t === "double" ? "double" : "long";
  return "string";
}

export class DynamicSchemaBuilder {
  private readonly _classes = new Map<string, ClassDef>(); // by group key
  private readonly _classNames = new Set<string>();
  private readonly _loaded = new WeakSet<PropertyDef>();
  private _changed = false;
  private _version: [number, number, number] = [1, 0, 0];

  /** The class key of a property group: what makes two groups "the same kind of record". */
  public static groupKey(g: ImxPropertyGroup): string {
    return `${g.source}|${g.schema ?? ""}|${g.class ?? g.name}`;
  }

  public get hasClasses(): boolean { return this._classes.size > 0; }
  public get changed(): boolean { return this._changed; }

  /** Loads the classes of the schema already in the iModel (so the new version is a superset of it). */
  public loadExisting(db: IModelDb): void {
    let props: any;
    try {
      props = db.getSchemaProps(DynamicSchemaName);
    } catch {
      return; // not in the iModel yet
    }
    const v = String(props.version ?? "01.00.00").split(".").map((x: string) => parseInt(x, 10));
    this._version = [v[0] ?? 1, v[1] ?? 0, v[2] ?? 0];
    for (const [className, item] of Object.entries<any>(props.items ?? {})) {
      if (item.schemaItemType !== "EntityClass")
        continue;
      const key: string = item.description?.match(/\[key:(.*)\]$/)?.[1] ?? className;
      const def: ClassDef = { name: className, label: item.label ?? className, description: item.description ?? "", props: new Map(), names: new Set() };
      for (const p of item.properties ?? []) {
        const sourceKey: string = p.description?.match(/\[source:(.*)\]$/)?.[1] ?? p.name;
        const pd: PropertyDef = { name: p.name, label: p.label ?? p.name, type: p.typeName as PrimitiveTypeName };
        def.props.set(sourceKey, pd);
        def.names.add(p.name.toLowerCase());
        this._loaded.add(pd); // stored in the iModel: its type can no longer change
      }
      this._classes.set(key, def);
      this._classNames.add(className.toLowerCase());
    }
  }

  /** Registers a property group's class and properties (types widen as more values are seen). */
  public add(g: ImxPropertyGroup): void {
    const key = DynamicSchemaBuilder.groupKey(g);
    let def = this._classes.get(key);
    if (!def) {
      let base = toEcName(`${g.source}_${g.schema ? g.schema + "_" : ""}${g.class ?? g.name}`, "Group");
      let name = base;
      for (let i = 2; this._classNames.has(name.toLowerCase()); i++)
        name = `${base}_${i}`;
      this._classNames.add(name.toLowerCase());
      def = { name, label: `${g.source}: ${g.class ?? g.name}`, description: `${g.source} properties${g.schema ? " (" + g.schema + ")" : ""} [key:${key}]`, props: new Map(), names: new Set() };
      this._classes.set(key, def);
      this._changed = true;
    }
    for (const [k, v] of Object.entries(g.values)) {
      const existing = def.props.get(k);
      const t = widen(existing?.type, v);
      if (existing) {
        if (t && t !== existing.type) {
          // a type can only be widened by a new schema version if the property is new; keep the stored type and
          // let valueFor() coerce (string accepts everything)
          if (existing.type !== "string" && !this._isLoadedProperty(def, k)) { existing.type = t; this._changed = true; }
        }
        continue;
      }
      let pname = toEcName(k, "Property");
      if (reserved.has(pname.toLowerCase()))
        pname = "Source_" + pname;
      const base = pname;
      for (let i = 2; def.names.has(pname.toLowerCase()); i++)
        pname = `${base}_${i}`;
      def.names.add(pname.toLowerCase());
      def.props.set(k, { name: pname, label: k, type: t ?? "string" });
      this._changed = true;
    }
  }

  private _isLoadedProperty(def: ClassDef, key: string): boolean {
    const p = def.props.get(key);
    return p !== undefined && this._loaded.has(p);
  }

  /** ECSchema XML (EC 3.2) for the current class set, with the minor version bumped when anything changed. */
  public toXml(): string {
    const [r, w, m] = this._version;
    const version = `${String(r).padStart(2, "0")}.${String(w).padStart(2, "0")}.${String(this._changed ? m + 1 : m).padStart(2, "0")}`;
    const lines: string[] = [];
    lines.push(`<?xml version="1.0" encoding="UTF-8"?>`);
    lines.push(`<ECSchema schemaName="${DynamicSchemaName}" alias="${DynamicSchemaAlias}" version="${version}" displayLabel="DgnKit source properties" description="Business properties of converted source elements (EC instances, item types, tags, linkages). Generated by DgnKit; grows between runs." xmlns="http://www.bentley.com/schemas/Bentley.ECXML.3.2">`);
    lines.push(`  <ECSchemaReference name="BisCore" version="01.00.00" alias="bis"/>`);
    lines.push(`  <ECSchemaReference name="CoreCustomAttributes" version="01.00.00" alias="CoreCA"/>`);
    lines.push(`  <ECCustomAttributes><DynamicSchema xmlns="CoreCustomAttributes.01.00.00"/></ECCustomAttributes>`);
    for (const def of this._classes.values()) {
      lines.push(`  <ECEntityClass typeName="${def.name}" displayLabel="${xmlEscape(def.label)}" description="${xmlEscape(def.description)}">`);
      lines.push(`    <BaseClass>bis:ElementMultiAspect</BaseClass>`);
      for (const [sourceKey, p] of def.props)
        lines.push(`    <ECProperty propertyName="${p.name}" typeName="${p.type}" displayLabel="${xmlEscape(p.label)}" description="${xmlEscape(`[source:${sourceKey}]`)}"/>`);
      lines.push(`  </ECEntityClass>`);
    }
    lines.push(`</ECSchema>`);
    return lines.join("\n");
  }

  /** Aspect props for a property group (class must have been added). */
  public aspectProps(g: ImxPropertyGroup, elementId: string): Record<string, unknown> {
    const def = this._classes.get(DynamicSchemaBuilder.groupKey(g));
    if (!def)
      throw new Error(`property group class not registered: ${DynamicSchemaBuilder.groupKey(g)}`);
    const props: Record<string, unknown> = {
      classFullName: `${DynamicSchemaName}:${def.name}`,
      element: { id: elementId, relClassName: "BisCore:ElementOwnsMultiAspects" },
    };
    for (const [k, v] of Object.entries(g.values)) {
      const p = def.props.get(k);
      if (!p)
        continue;
      const value = DynamicSchemaBuilder.coerce(p.type, v);
      if (value !== undefined)
        props[lowerFirst(p.name)] = value;
    }
    return props;
  }

  private static coerce(type: PrimitiveTypeName, v: unknown): unknown {
    if (v === null || v === undefined)
      return undefined;
    switch (type) {
      case "string": return typeof v === "string" ? v : JSON.stringify(v);
      case "boolean": return typeof v === "boolean" ? v : undefined;
      case "int": case "long": return typeof v === "number" && Number.isInteger(v) ? v : undefined;
      case "double": return typeof v === "number" ? v : undefined;
    }
  }
}

/** iTwin.js maps EC property names to JS props with a lower-case first letter. */
function lowerFirst(s: string): string {
  return s.length === 0 ? s : s[0].toLowerCase() + s.substring(1);
}
