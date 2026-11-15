/*---------------------------------------------------------------------------------------------
 * IMX — DgnKit's iModel interchange (see docs/IMODEL-INTERCHANGE.md). Newline-delimited JSON, one record per line,
 * in dependency order. Coordinates are metres. Keys link records; `source` is the stable provenance identity.
 *--------------------------------------------------------------------------------------------*/
import * as fs from "node:fs";
import * as readline from "node:readline";

export type XYZ = [number, number, number];
export type XY = [number, number];

export interface ImxSource {
  kind: string;
  id: string;
  info?: Record<string, unknown>;
}

export interface ImxRange { low: XYZ; high: XYZ }

export interface ImxHeader {
  kind: "header";
  format: "dgnkit-imx";
  version: number;
  generator: string;
  units: "meter";
  source: { path: string; name: string; format: string; formatVersion?: string; size: number; modified?: string; sha256?: string };
  spatialExtents?: ImxRange;
  gcs?: { epsg?: number; csmapKey?: string; description?: string; unitMeters?: number; wkt?: string; source?: string };
}

export interface ImxFont { kind: "font"; key: string; name: string; type: "truetype" | "rsc" | "shx" }

export interface ImxLineStyle {
  kind: "lineStyle";
  key: string;
  name: string;
  description?: string;
  linePixels?: number;
  strokes?: Array<{ length: number; dash: boolean; width?: number }>;
  phase?: number;
  annotation?: boolean;
  /** Why this style only approximates the source one (reported as a warning); absent when exact. */
  approximation?: string;
  source: ImxSource;
  checksum: string;
}

export interface ImxAppearance {
  color: number;
  weight: number;
  style?: string;
  priority?: number;
  transparency?: number;
  invisible?: boolean;
  dontPlot?: boolean;
  dontLocate?: boolean;
}

export interface ImxCategory {
  kind: "category";
  key: string;
  name: string;
  description?: string;
  usage: Array<"spatial" | "drawing">;
  appearance: ImxAppearance;
  source: ImxSource;
  checksum: string;
}

export interface ImxSubCategory { kind: "subCategory"; key: string; category: string; name: string; appearance: ImxAppearance; checksum: string }

export interface ImxModel {
  kind: "model";
  key: string;
  name: string;
  description?: string;
  type: "physical" | "drawing" | "sheet";
  is3d: boolean;
  masterUnit?: string;
  metersPerMaster: number;
  extents?: ImxRange;
  sheetSize?: XY;
  source: ImxSource;
  checksum: string;
}

export interface ImxPart { kind: "part"; key: string; name: string; is3d: boolean; geometry: unknown[]; source: ImxSource; checksum: string }

export interface ImxPropertyGroup {
  source: string;
  name: string;
  schema?: string;
  class?: string;
  values: Record<string, unknown>;
}

export interface ImxElement {
  kind: "element";
  key: string;
  model: string;
  category: string;
  role: "geometry" | "annotation";
  parent?: string;
  userLabel?: string;
  origin: XYZ | XY;
  geometry: Array<Record<string, any>>;
  properties?: ImxPropertyGroup[];
  modified?: string;
  source: ImxSource;
  checksum: string;
}

/** A relationship class (logic layer): becomes an ECRelationshipClass derived from bis:ElementRefersToElements. */
export interface ImxRelationshipClass {
  kind: "relationshipClass";
  name: string;
  description?: string;
  base: string;
  sourceRole?: string;
  targetRole?: string;
  /** property name → EC primitive type */
  properties: Record<string, "string" | "double" | "int" | "long" | "boolean">;
}

/** A link-table relationship between two elements of the same IMX stream (by element key). */
export interface ImxRelationship {
  kind: "relationship";
  key: string;
  class: string;
  source: string;
  target: string;
  properties?: Record<string, unknown>;
  checksum: string;
}

export type ImxRecord = ImxHeader | ImxFont | ImxLineStyle | ImxCategory | ImxSubCategory | ImxModel | ImxPart | ImxElement
  | ImxRelationshipClass | ImxRelationship;

/** Streams the records of an IMX file in order. */
export async function* readImx(path: string): AsyncGenerator<ImxRecord> {
  const rl = readline.createInterface({ input: fs.createReadStream(path, { encoding: "utf8" }), crlfDelay: Infinity });
  let lineNo = 0;
  for await (const line of rl) {
    lineNo++;
    if (line.trim().length === 0)
      continue;
    let rec: ImxRecord;
    try {
      rec = JSON.parse(line) as ImxRecord;
    } catch (e) {
      throw new Error(`${path}:${lineNo}: invalid JSON (${(e as Error).message})`);
    }
    if (lineNo === 1 && (rec.kind !== "header" || (rec as ImxHeader).format !== "dgnkit-imx"))
      throw new Error(`${path}: not an IMX file (first record must be a dgnkit-imx header)`);
    yield rec;
  }
}

/** Collects every property group of an IMX file (first pass, for the dynamic schema). */
export async function collectPropertyGroups(path: string): Promise<ImxPropertyGroup[]> {
  const groups: ImxPropertyGroup[] = [];
  for await (const rec of readImx(path))
    if (rec.kind === "element" && rec.properties)
      groups.push(...rec.properties);
  return groups;
}
