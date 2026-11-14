/*---------------------------------------------------------------------------------------------
 * Prints what an iModel contains — used to verify connector output (and to look at Bentley-produced iModels):
 *
 *   node dist/inspect.js <file.bim> [--elements N] [--profile <name>]
 *--------------------------------------------------------------------------------------------*/
import { IModelHost, SnapshotDb } from "@itwin/core-backend";
import { QueryRowFormat } from "@itwin/core-common";
import { startHost } from "./host";

async function rows(db: SnapshotDb, ecsql: string): Promise<any[]> {
  return db.createQueryReader(ecsql, undefined, { rowFormat: QueryRowFormat.UseECSqlPropertyIndexes }).toArray();
}

async function main(): Promise<void> {
  const file = process.argv[2];
  const nIdx = process.argv.indexOf("--elements");
  const sample = nIdx > 0 ? parseInt(process.argv[nIdx + 1], 10) : 3;
  await startHost(process.argv);
  const db = SnapshotDb.openFile(file);
  const out = (s: string) => process.stdout.write(s + "\n");
  try {
    out(`iModel  : ${db.name}  (${file})`);
    out(`extents : ${JSON.stringify(db.projectExtents.toJSON())}`);
    out(`GCS     : ${db.geographicCoordinateSystem ? JSON.stringify(db.geographicCoordinateSystem.horizontalCRS?.id ?? db.geographicCoordinateSystem.toJSON()) : "(none)"}`);
    out(`ECEF    : ${db.ecefLocation ? JSON.stringify(db.ecefLocation.origin.toJSON()) : "(none)"}`);
    out("\nschemas:");
    for (const r of await rows(db, "SELECT Name, VersionMajor, VersionWrite, VersionMinor FROM meta.ECSchemaDef ORDER BY Name"))
      out(`  ${r[0]} ${r[1]}.${r[2]}.${r[3]}`);
    out("\nsubjects / partitions / models:");
    for (const r of await rows(db, `SELECT m.ECInstanceId, ec_classname(m.ECClassId,'s:c'), e.CodeValue, ec_classname(e.ECClassId,'s:c'),
                                          (SELECT COUNT(*) FROM bis.Element x WHERE x.Model.Id = m.ECInstanceId)
                                     FROM bis.Model m JOIN bis.Element e ON e.ECInstanceId = m.ModeledElement.Id ORDER BY m.ECInstanceId`))
      out(`  ${r[0]} ${r[1].padEnd(28)} ← ${r[3].padEnd(30)} '${r[2] ?? ""}'  elements=${r[4]}`);
    for (const r of await rows(db, "SELECT ECInstanceId, CodeValue, Parent.Id FROM bis.Subject ORDER BY ECInstanceId"))
      out(`  subject ${r[0]} '${r[1]}' parent=${r[2] ?? "-"}`);
    out("\nelement classes:");
    for (const r of await rows(db, "SELECT ec_classname(ECClassId,'s:c') c, COUNT(*) n FROM bis.Element GROUP BY ECClassId ORDER BY n DESC"))
      out(`  ${String(r[1]).padStart(8)}  ${r[0]}`);
    out("\naspect classes:");
    for (const r of await rows(db, "SELECT ec_classname(ECClassId,'s:c') c, COUNT(*) n FROM bis.ElementAspect GROUP BY ECClassId ORDER BY n DESC"))
      out(`  ${String(r[1]).padStart(8)}  ${r[0]}`);
    out("\nprovenance kinds:");
    for (const r of await rows(db, "SELECT Kind, COUNT(*) FROM bis.ExternalSourceAspect GROUP BY Kind"))
      out(`  ${String(r[1]).padStart(8)}  ${r[0]}`);
    const bad = await rows(db, "SELECT COUNT(*) FROM bis.GeometricElement3d WHERE GeometryStream IS NULL");
    const bad2 = await rows(db, "SELECT COUNT(*) FROM bis.GeometricElement2d WHERE GeometryStream IS NULL");
    out(`\ngeometric elements without geometry: 3d=${bad[0][0]} 2d=${bad2[0][0]}`);
    out("\nsample elements:");
    for (const r of await rows(db, `SELECT ECInstanceId FROM bis.GeometricElement LIMIT ${sample}`)) {
      const p = db.elements.getElementProps({ id: r[0], wantGeometry: true }) as any;
      const kinds = (p.geom ?? []).map((g: any) => Object.keys(g)[0]).join(",");
      out(`  ${p.id} ${p.classFullName} cat=${p.category} label='${p.userLabel ?? ""}' placement=${JSON.stringify(p.placement)} geom=[${kinds}]`);
    }
  } finally {
    db.close();
    await IModelHost.shutdown();
  }
}

main().catch((e) => { process.stderr.write(`error: ${e?.stack ?? e}\n`); process.exit(2); });
