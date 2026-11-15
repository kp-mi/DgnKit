/*---------------------------------------------------------------------------------------------
 * dgnkit-imodel-connector — IMX → iModel.
 *
 *   node dist/main.js <in.imx.jsonl> <out.bim> [--standalone] [--name "iModel name"]
 *
 * Default: a fresh SnapshotDb (immutable, rebuilt every run). --standalone: a StandaloneDb that is created once and
 * then updated in place on every later run (change detection via ExternalSourceAspect checksums, deletions detected
 * per source file) — the local stand-in for an iModelHub briefcase.
 *--------------------------------------------------------------------------------------------*/
import * as fs from "node:fs";
import { IModelDb, IModelHost, SnapshotDb, StandaloneDb, withEditTxn } from "@itwin/core-backend";
import { OpenMode } from "@itwin/core-bentley";
import { GeographicCRSProps } from "@itwin/core-common";
import { optionValue, startHost } from "./host";
import { ImxHeader } from "./imx";
import { ConnectorName, ConnectorVersion, ImxLoader } from "./ImxLoader";

function usage(): never {
  process.stderr.write("usage: node dist/main.js <in.imx.jsonl> <out.bim> [--standalone] [--name <iModel name>] [--profile <name>]\n");
  process.exit(1);
}

const valueOptions = new Set(["--name", "--profile"]);

function gcsProps(h: ImxHeader): GeographicCRSProps | undefined {
  const g = h.gcs;
  if (!g || (!g.epsg && !g.csmapKey))
    return undefined;
  const key = g.csmapKey && !/^EPSG:/i.test(g.csmapKey) ? g.csmapKey : (g.epsg ? `EPSG:${g.epsg}` : g.csmapKey!);
  // heights are ellipsoidal unless the producer names the vertical datum (NAVD88, NGVD29, GEOID, LOCAL_ELLIPSOID)
  return { horizontalCRS: { id: key, epsg: g.epsg, description: g.description }, verticalCRS: { id: g.verticalCRS ?? "ELLIPSOID" } };
}

async function main(): Promise<number> {
  const args = process.argv.slice(2);
  const positional = args.filter((a, i) => !a.startsWith("--") && !(i > 0 && valueOptions.has(args[i - 1])));
  if (positional.length < 2) usage();
  const [imx, out] = positional;
  const standalone = args.includes("--standalone");

  const header = await ImxLoader.readHeader(imx);
  const name = optionValue(args, "name") ?? header.source.name;

  await startHost(args);
  let db: IModelDb;
  try {
    if (standalone && fs.existsSync(out)) {
      db = StandaloneDb.openFile(out, OpenMode.ReadWrite);
      process.stdout.write(`updating standalone iModel ${out}\n`);
    } else {
      if (fs.existsSync(out))
        fs.unlinkSync(out); // a snapshot is rebuilt from scratch
      const props = { rootSubject: { name, description: `Created by ${ConnectorName} ${ConnectorVersion} from ${header.source.name}` } };
      db = standalone ? StandaloneDb.createEmpty(out, { ...props, allowEdit: JSON.stringify({ txns: true }) }) : SnapshotDb.createEmpty(out, props);
      process.stdout.write(`created ${standalone ? "standalone" : "snapshot"} iModel ${out}\n`);
    }

    const gcs = gcsProps(header);
    if (gcs && !db.geographicCoordinateSystem) {
      try {
        // on failure withEditTxn abandons, which also restores the in-memory GCS from the file (so later iModel-props
        // writes don't persist the rejected one)
        withEditTxn(db, "DgnKit: geographic coordinate system", (txn) => {
          db.setGeographicCoordinateSystem(gcs);
          txn.updateIModelProps();
        });
      } catch (e) {
        process.stdout.write(`warning: could not set GCS ${gcs.horizontalCRS?.id}: ${(e as Error).message}\n`);
      }
    }

    const t0 = Date.now();
    const report = await new ImxLoader(db, imx).run();
    process.stdout.write(`${ConnectorName}: ${report.models} model(s), ${report.categories} categor(ies); elements inserted ${report.inserted}, ` +
      `updated ${report.updated}, unchanged ${report.unchanged}, deleted ${report.deleted} in ${Date.now() - t0} ms\n`);
    const rel = report.relationships;
    if (rel.inserted + rel.updated + rel.unchanged + rel.deleted + rel.unresolved > 0)
      process.stdout.write(`${ConnectorName}: logic relationships inserted ${rel.inserted}, updated ${rel.updated}, unchanged ${rel.unchanged}, deleted ${rel.deleted}` +
        (rel.unresolved > 0 ? `, ${rel.unresolved} with an unknown end skipped` : "") + "\n");
    for (const w of report.warnings.slice(0, 20))
      process.stdout.write(`warning: ${w}\n`);
    if (report.warnings.length > 20)
      process.stdout.write(`… ${report.warnings.length - 20} more warnings\n`);
    db.close();
  } finally {
    await IModelHost.shutdown();
  }
  return 0;
}

main().then((code) => process.exit(code), (err) => {
  process.stderr.write(`error: ${err?.stack ?? err}\n`);
  process.exit(2);
});
