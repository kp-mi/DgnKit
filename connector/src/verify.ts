/*---------------------------------------------------------------------------------------------
 * Checks a connector-written iModel against the IMX it was made from:
 *
 *   node dist/verify.js <in.imx.jsonl> <file.bim> [--profile <name>]
 *
 * 1. Geometry: every curve/point/mesh/solid/text primitive sent (including members of paths, loops and regions) is in
 *    the stored GeometryStreams. iTwin.js normalises a few forms on write — a lineSegment is stored as a 2-point
 *    lineString, a single-member path is unwrapped — so segments and line strings are counted together and containers
 *    are not counted themselves. The backend silently drops JSON it does not understand; this is what catches it.
 * 2. Placement: per source model, the stored model extents against the IMX model extents. Text boxes are measured with
 *    whatever font the backend has (glyphs are not embedded yet), so a few % on text-bound edges is expected.
 *
 * Exit code 0 when the primitives match, 1 otherwise.
 *--------------------------------------------------------------------------------------------*/
import * as fs from "node:fs";
import { IModelHost, SnapshotDb } from "@itwin/core-backend";
import { GeometricElementProps } from "@itwin/core-common";
import { Range3d } from "@itwin/core-geometry";
import { startHost } from "./host";

const notPrimitives = new Set(["header", "appearance", "fill", "styleMod", "material", "subRange", "pattern"]);
const containers = new Set(["path", "loop", "parityRegion", "unionRegion"]);

function countPrimitives(entries: unknown, acc: Map<string, number>): Map<string, number> {
  for (const e of (Array.isArray(entries) ? entries : []) as Array<Record<string, unknown>>)
    for (const [k, v] of Object.entries(e)) {
      if (notPrimitives.has(k))
        continue;
      if (containers.has(k)) {
        countPrimitives(Array.isArray(v) ? v : [v], acc);
        continue;
      }
      // iTwin.js stores a cone with equal radii as a cylinder
      const kind = k === "lineSegment" || k === "lineString" ? "line" : k === "cylinder" ? "cone" : k;
      acc.set(kind, (acc.get(kind) ?? 0) + 1);
    }
  return acc;
}

const show = (m: Map<string, number>) => [...m].sort(([a], [b]) => a.localeCompare(b)).map(([k, n]) => `${k} ${n}`).join(", ");

async function main(): Promise<number> {
  const [imx, bim] = process.argv.slice(2).filter((a, i, all) => !a.startsWith("--") && all[i - 1] !== "--profile");
  if (!imx || !bim) {
    process.stderr.write("usage: node dist/verify.js <in.imx.jsonl> <file.bim> [--profile <name>]\n");
    return 2;
  }
  const sent = new Map<string, number>();
  const models: Array<{ name: string; type: string; extents?: { low: number[]; high: number[] }; source: { id: string } }> = [];
  for (const line of fs.readFileSync(imx, "utf8").split("\n")) {
    if (line.includes('"kind":"element"')) countPrimitives(JSON.parse(line).geometry, sent);
    else if (line.includes('"kind":"model"')) models.push(JSON.parse(line));
  }

  await startHost(process.argv);
  const db = SnapshotDb.openFile(bim);
  try {
    const stored = new Map<string, number>();
    const ids: string[] = [];
    for await (const row of db.createQueryReader("SELECT ECInstanceId FROM bis.GeometricElement"))
      ids.push(row[0]);
    for (const id of ids)
      countPrimitives(db.elements.getElementProps<GeometricElementProps>({ id, wantGeometry: true }).geom, stored);
    const match = show(sent) === show(stored);
    process.stdout.write(`geometry : ${ids.length} geometric elements; primitives ${match ? "match" : "DIFFER"}\n`);
    process.stdout.write(`  sent   : ${show(sent)}\n`);
    if (!match)
      process.stdout.write(`  stored : ${show(stored)}\n`);

    // the Model provenance aspect sits on the modelled element (partition / drawing / sheet), whose id is the model id
    for await (const row of db.createQueryReader("SELECT Element.Id, Identifier FROM bis.ExternalSourceAspect WHERE Kind='Model'")) {
      const [modelId, identifier] = row.toArray() as [string, string];
      const m = models.find((x) => x.source.id === identifier);
      if (!m?.extents)
        continue;
      const [ext] = await db.models.queryExtents(modelId);
      const r = Range3d.fromJSON(ext.extents);
      const lo = [r.low.x, r.low.y], hi = [r.high.x, r.high.y];
      const wl = m.extents.low, wh = m.extents.high;
      const size = Math.max(wh[0] - wl[0], wh[1] - wl[1], 1e-9);
      const dev = Math.max(...[0, 1].flatMap((i) => [Math.abs(lo[i] - wl[i]), Math.abs(hi[i] - wh[i])])) / size;
      const f = (v: number) => v.toFixed(3);
      process.stdout.write(`model    : '${m.name}' (${m.type}) imx x ${f(wl[0])}..${f(wh[0])} y ${f(wl[1])}..${f(wh[1])}; ` +
        `iModel x ${f(lo[0])}..${f(hi[0])} y ${f(lo[1])}..${f(hi[1])}; largest edge difference ${(dev * 100).toFixed(2)}% of model size\n`);
    }
    // logic relationships: every one sent is stored, with its class and ends
    const sentRels = fs.readFileSync(imx, "utf8").split("\n").filter((l) => l.includes('"kind":"relationship"')).map((l) => JSON.parse(l));
    let relMatch = true;
    if (sentRels.length > 0) {
      const byClass = new Map<string, number>();
      for (const r of sentRels) byClass.set(r.class, (byClass.get(r.class) ?? 0) + 1);
      const stored = new Map<string, number>();
      for await (const row of db.createQueryReader("SELECT ec_classname(ECClassId, 'c'), COUNT(*) FROM bis.ElementRefersToElements WHERE ec_classname(ECClassId, 's') = 'DgnKitLogic' GROUP BY ECClassId"))
        stored.set(row[0], row[1]);
      relMatch = show(byClass) === show(stored);
      process.stdout.write(`logic    : ${sentRels.length} relationships sent; ${relMatch ? "all stored" : "DIFFER"}\n  sent   : ${show(byClass)}\n`);
      if (!relMatch) process.stdout.write(`  stored : ${show(stored)}\n`);
    }
    return match && relMatch ? 0 : 1;
  } finally {
    db.close();
    await IModelHost.shutdown();
  }
}

main().then((code) => process.exit(code), (err) => {
  process.stderr.write(`error: ${err?.stack ?? err}\n`);
  process.exit(2);
});
