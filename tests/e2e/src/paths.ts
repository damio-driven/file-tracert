import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

/** `tests/e2e` — the root of this test project. */
export const e2eRoot = path.resolve(here, '..');

/** The repository root. */
export const repoRoot = path.resolve(e2eRoot, '..', '..');

/** Everything this suite writes lives here, and nothing this suite writes lives anywhere else. */
export const artifactsRoot = path.join(e2eRoot, '.artifacts');

export const frontendDir = path.join(repoRoot, 'src', 'frontend');

/**
 * The Host project directory, used as the working directory of the Host process. It is also the
 * content root, which is what makes `wwwroot` (where `ng build` puts the SPA) resolvable — the
 * Debug output folder has no copy of it.
 */
export const hostProjectDir = path.join(repoRoot, 'src', 'backend', 'FileTracert.Host');

export const hostExePath = path.join(
  hostProjectDir,
  'bin',
  'Debug',
  'net10.0-windows',
  'FileTracert.Host.exe',
);

export const solutionPath = path.join(repoRoot, 'src', 'backend', 'FileTracert.slnx');

export const startHostScript = path.join(e2eRoot, 'scripts', 'start-host.ps1');
export const stopHostScript = path.join(e2eRoot, 'scripts', 'stop-host.ps1');
