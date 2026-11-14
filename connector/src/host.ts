/*---------------------------------------------------------------------------------------------
 * IModelHost startup shared by the connector and the inspector.
 *
 * iTwin.js locks a per-user *profile* directory (%LOCALAPPDATA%\iModelJs\profiles\<name>, holding the workspace cloud
 * cache) for the life of the process, so two processes on the same profile cannot run at once. A failed startup cannot
 * be retried in-process (IModelHost marks itself started before taking the lock), so instead of retrying we report the
 * conflict clearly; parallel jobs pass distinct `--profile` names.
 *--------------------------------------------------------------------------------------------*/
import { IModelHost } from "@itwin/core-backend";

/** Value of `--<name> <value>` in argv, if present. */
export function optionValue(args: readonly string[], name: string): string | undefined {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : undefined;
}

/** Exit code when the profile is held by another process (a known, retryable condition — no stack trace). */
export const ProfileInUseExitCode = 3;

export async function startHost(args: readonly string[]): Promise<void> {
  const profileName = optionValue(args, "profile");
  try {
    await IModelHost.startup(profileName ? { profileName } : {});
  } catch (e) {
    if (/already in use by another process/.test((e as Error).message)) {
      process.stderr.write(`error: another iTwin.js process is using the profile '${profileName ?? "default"}'. ` +
        `Wait for it to finish, or give each parallel run its own profile with --profile <name>.\n`);
      process.exit(ProfileInUseExitCode);
    }
    throw e;
  }
}
