import { access, mkdir, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';

import { SandboxFence } from './fence.js';
import { artifactsRoot } from './paths.js';

/** One folder of the seeded tree: a name, how many files, and their extension. */
export interface FolderSpec {
  readonly name: string;
  readonly files: number;
  readonly extension: string;
}

export interface TreeSpec {
  readonly folders: readonly FolderSpec[];
  /** Every seeded file has exactly this size, so the byte totals a screen shows are predictable. */
  readonly fileSizeBytes: number;
}

/**
 * The tree the other tests reason about: twelve files of 200 bytes each (2400 B → "2,4 KB"),
 * split across three categories so a filter change has something to include and something to leave
 * out.
 */
export const DEFAULT_TREE: TreeSpec = {
  fileSizeBytes: 200,
  folders: [
    { name: 'foto', files: 5, extension: '.jpg' },
    { name: 'documenti', files: 4, extension: '.txt' },
    { name: 'musica', files: 3, extension: '.mp3' },
  ],
};

export interface SeededTree {
  readonly fileCount: number;
  readonly totalBytes: number;
  /** Files whose extension belongs to the Image category — what the "Immagini" filter keeps. */
  readonly imageCount: number;
}

const IMAGE_EXTENSIONS = new Set(['.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp']);

/**
 * A disposable folder tree on a real volume, plus the identity the API needs to watch it.
 *
 * Everything it creates lives under `tests/e2e/.artifacts`, and `dispose` refuses to delete
 * anything that does not. From step 12b on, tests also queue operations the product carries out
 * for real on this tree — so the sandbox carries the {@link SandboxFence} that decides whether an
 * operation is allowed to be sent at all.
 */
export class Sandbox {
  private constructor(
    /** Directory holding the Host database and log for this test. Never inside the watched tree. */
    readonly workDir: string,
    /** Absolute path of the folder that gets registered as a watched root. */
    readonly filesDir: string,
    /** The same folder, expressed the way the catalog does: relative to the volume root. */
    readonly volumeRelativePath: string,
    /** Root of the volume the sandbox sits on, e.g. `C:\`. */
    readonly driveRoot: string,
    private readonly caseDir: string,
    /** The containment rule for every operation this test queues. */
    readonly fence: SandboxFence,
  ) {}

  /** The path segments from the volume root down to (and including) the watched folder. */
  get pathSegments(): string[] {
    return this.volumeRelativePath.split('\\');
  }

  /** An absolute path inside the watched tree, for assertions that look at the real filesystem. */
  absolute(...segments: string[]): string {
    const resolved = path.resolve(this.filesDir, ...segments);
    const root = path.resolve(this.filesDir);
    if (resolved !== root && !resolved.startsWith(root + path.sep)) {
      throw new Error(`Refusing to name ${resolved}: it is not inside ${root}.`);
    }
    return resolved;
  }

  /** The same path as the catalog spells it: relative to the volume root. */
  relative(...segments: string[]): string {
    return path.relative(this.driveRoot, this.absolute(...segments)).replace(/\//g, '\\');
  }

  /** True when the path exists on disk. The end-state proof of a move is the filesystem. */
  async exists(...segments: string[]): Promise<boolean> {
    try {
      await access(this.absolute(...segments));
      return true;
    } catch {
      return false;
    }
  }

  /** When a seeded file was last written, as the filesystem recorded it. */
  async modifiedAt(...segments: string[]): Promise<Date> {
    return (await stat(this.absolute(...segments))).mtime;
  }

  /** Creates a folder inside the watched tree. */
  async makeDir(...segments: string[]): Promise<string> {
    const dir = this.absolute(...segments);
    await mkdir(dir, { recursive: true });
    return dir;
  }

  /** Writes one file inside the watched tree, with content of a known length. */
  async writeFileOfSize(sizeBytes: number, ...segments: string[]): Promise<string> {
    const file = this.absolute(...segments);
    await mkdir(path.dirname(file), { recursive: true });
    await writeFile(file, 'x'.repeat(sizeBytes));
    return file;
  }

  /** Removes something from inside the watched tree — never from anywhere else. */
  async remove(...segments: string[]): Promise<void> {
    await rm(this.absolute(...segments), { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }

  static async create(slug: string): Promise<Sandbox> {
    const caseDir = path.join(artifactsRoot, slug);
    const filesDir = path.join(caseDir, 'files');
    const workDir = path.join(caseDir, 'host');

    await removeInsideArtifacts(caseDir);
    await mkdir(filesDir, { recursive: true });
    await mkdir(path.join(workDir, 'db'), { recursive: true });

    const parsed = path.parse(filesDir);
    const driveRoot = parsed.root;
    const relative = path.relative(driveRoot, filesDir).replace(/\//g, '\\');

    return new Sandbox(
      workDir,
      filesDir,
      relative,
      driveRoot,
      caseDir,
      new SandboxFence(relative, filesDir, driveRoot),
    );
  }

  /** Writes the tree and returns what a screen should end up saying about it. */
  async seed(spec: TreeSpec = DEFAULT_TREE): Promise<SeededTree> {
    const content = 'x'.repeat(spec.fileSizeBytes);
    let fileCount = 0;
    let imageCount = 0;

    for (const folder of spec.folders) {
      const dir = path.join(this.filesDir, folder.name);
      await mkdir(dir, { recursive: true });
      for (let i = 1; i <= folder.files; i++) {
        await writeFile(path.join(dir, `${folder.name}-${String(i).padStart(4, '0')}${folder.extension}`), content);
        fileCount++;
        if (IMAGE_EXTENSIONS.has(folder.extension)) {
          imageCount++;
        }
      }
    }

    return { fileCount, totalBytes: fileCount * spec.fileSizeBytes, imageCount };
  }

  /** Removes every byte this sandbox put on disk. Nothing outside `.artifacts` is reachable. */
  async dispose(): Promise<void> {
    await removeInsideArtifacts(this.caseDir);
  }
}

/**
 * The only deletion this suite performs, and the one place the containment rule is written.
 * Both the setup and the teardown go through it: a guard that protects one of the two protects
 * nothing.
 */
async function removeInsideArtifacts(target: string): Promise<void> {
  const resolved = path.resolve(target);
  const allowed = path.resolve(artifactsRoot);
  if (resolved === allowed || !resolved.startsWith(allowed + path.sep)) {
    throw new Error(`Refusing to delete ${resolved}: it is not inside ${allowed}.`);
  }
  await rm(resolved, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
