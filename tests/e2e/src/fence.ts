import path from 'node:path';

/**
 * The one thing that has to exist before an end-to-end test is allowed to queue anything.
 *
 * Step 12a only ever *read* the disk, so its sandbox protected the suite: it bounded what the
 * tests deleted. From here on the tests enqueue operations that the real `JobExecutionEngine`
 * carries out — moves, renames, folder creations — on a volume this suite deliberately makes
 * catalogable, which on a developer machine is the system volume. A wrong destination path is no
 * longer a failing assertion; it is a file of the user's that moved.
 *
 * So containment is checked, not arranged. Three layers, each answering a different way of
 * getting out:
 *
 *  1. **Perimeter** (`assertPerimeter`) — the catalog is confined to the sandbox folder: it is the
 *     only active watched root on the whole machine, so no volume ever gets scanned into the index
 *     and *no source id can name a file outside*. Asserted from the service's own answer, not from
 *     the fact that the test only asked for one root.
 *  2. **Destination** (`assertRequestStaysInside`) — every enqueue is inspected *before* it
 *     reaches the Host, and refused if its destination volume or path is not the sandbox's. This
 *     runs for requests the test makes through `Api` **and** for requests the SPA makes when a
 *     spec drives the move picker, because the browser context routes them through here too.
 *  3. **Audit** (`auditRecordedJobs`) — after the fact, before the Host is stopped, every job the
 *     product actually recorded is read back and checked: inside the sandbox, on the sandbox
 *     volume, intra-volume. Cross-volume is what recycles a source file, and this suite must never
 *     put anything in the recycle bin, so a cross-volume job here is a violation by itself.
 *
 * Layers 1 and 2 are the promise ("before execution"); layer 3 is the proof. Every one of them
 * fails loudly, naming the offending path — none of them quietly corrects anything.
 */
export class SandboxFence {
  /** The volume the sandbox sits on, learned when the test points the catalog at it. */
  private volumeId: number | null = null;

  /**
   * Enqueues the browser attempted and this fence refused. They are collected rather than thrown
   * because the refusal happens inside a route handler, where a throw would surface as an opaque
   * network error in the page instead of a named failure in the report.
   */
  private readonly browserViolations: string[] = [];

  constructor(
    /** The sandbox folder as the catalog spells it: relative to the volume root, no leading slash. */
    private readonly relativeRoot: string,
    /** The same folder as Windows spells it, for messages a human can act on. */
    readonly absoluteRoot: string,
  ) {}

  /**
   * Records the only volume an operation may name. Called by the scenario helper that makes the
   * sandbox volume catalogable, so the binding comes from the service's answer rather than from a
   * constant in a test.
   */
  bindVolume(volumeId: number): void {
    if (this.volumeId !== null && this.volumeId !== volumeId) {
      throw new Error(
        `${VIOLATION} the sandbox was already bound to volume ${this.volumeId}; refusing to rebind ` +
          `it to ${volumeId}. One test may only ever operate on one volume.`,
      );
    }
    this.volumeId = volumeId;
  }

  /** True once the sandbox volume is known — before that no enqueue can be cleared. */
  get isBound(): boolean {
    return this.volumeId !== null;
  }

  /** Records an enqueue the browser was stopped from sending. */
  recordBrowserViolation(violation: string): void {
    this.browserViolations.push(violation);
  }

  /**
   * Hands over what the browser was stopped from sending, and forgets it. The context teardown
   * calls this and fails the test on anything it gets back; the fence's own spec calls it first,
   * to assert on a refusal it provoked on purpose.
   */
  takeBrowserViolations(): string[] {
    return this.browserViolations.splice(0, this.browserViolations.length);
  }

  /**
   * The verdict on one enqueue request. Returns the reason it must not be sent, or null when it
   * stays inside. Kept separate from the throwing form because the browser route handler has to
   * both refuse the request and report why.
   */
  violationOf(request: JobRequestLike): string | null {
    if (this.volumeId === null) {
      return (
        'an operation was enqueued before the sandbox volume was known, so nothing here can ' +
        'tell whether its destination is inside the sandbox.'
      );
    }

    const type = String(request.type ?? '');
    if (!KNOWN_TYPES.has(type)) {
      return `unknown job type "${type}": this fence cannot tell where it would write.`;
    }

    if (request.targetVolumeId !== null && request.targetVolumeId !== undefined) {
      if (request.targetVolumeId !== this.volumeId) {
        return (
          `destination volume ${request.targetVolumeId} is not the sandbox volume ` +
          `${this.volumeId}.`
        );
      }
    }

    // Rename carries a leaf name, every other type carries a path relative to the volume root.
    if (type === 'RenameFile' || type === 'RenameFolder') {
      return this.leafViolation(request.newName, 'nuovo nome');
    }

    const target = request.targetRelativePath;
    if (typeof target !== 'string' || target.length === 0) {
      return `${type} carries no destination path.`;
    }
    return this.pathViolation(target);
  }

  /** As above, but the way a test wants it: nothing happens, loudly. */
  assertRequestStaysInside(request: JobRequestLike): void {
    const violation = this.violationOf(request);
    if (violation !== null) {
      throw new Error(
        `${VIOLATION} ${violation}\n` +
          `  request: ${JSON.stringify(request)}\n` +
          `  sandbox: ${this.absoluteRoot}`,
      );
    }
  }

  /**
   * Checks a path the catalog would resolve against the volume root. Rejects anything that is not
   * strictly inside the sandbox folder — including the folder's siblings, which a prefix compare
   * without the separator would happily accept ("…\files-2" starts with "…\files").
   */
  private pathViolation(relativePath: string): string | null {
    const normalized = normalize(relativePath);
    if (normalized === null) {
      return `destination "${relativePath}" is not a plain path relative to the volume root.`;
    }

    // Compared lower-cased: Windows paths are case-insensitive, and a destination that differs
    // only in case is the same folder — refusing it would be a false alarm, accepting a sibling
    // whose name merely starts the same way would be the real one (hence the separator).
    const candidate = normalized.toLowerCase();
    const root = normalize(this.relativeRoot)!.toLowerCase();
    const inside = candidate === root || candidate.startsWith(`${root}\\`);
    if (!inside) {
      return (
        `destination "${relativePath}" resolves outside the sandbox ` +
        `(would write to <volume root>\\${normalized}).`
      );
    }
    return null;
  }

  private leafViolation(name: unknown, what: string): string | null {
    if (typeof name !== 'string' || name.trim().length === 0) {
      return `${what} is empty.`;
    }
    if (/[\\/:*?"<>|]/.test(name) || name === '.' || name === '..') {
      return `${what} "${name}" is not a single file or folder name; a rename must stay put.`;
    }
    return null;
  }

  /**
   * Layer 1. Asks the service which folders it is watching, on every volume it knows, and refuses
   * to let the test continue unless the answer is "the sandbox, and nothing else". This is what
   * makes every catalogued id — every possible operation *source* — a file inside the sandbox.
   */
  async assertPerimeter(reader: PerimeterReader): Promise<void> {
    const offenders: string[] = [];
    for (const volume of await reader.volumes()) {
      const detail = await reader.volume(volume.id);
      for (const root of detail.watchedRoots) {
        // Inside the sandbox, not merely equal to it: a spec is free to watch a subfolder, and
        // everything under the sandbox is still the sandbox. Anything else — another volume, or a
        // path that walks out — is what makes the index able to name a file of the user's.
        const mine =
          volume.id === this.volumeId && this.pathViolation(root.relativePath) === null;
        if (!mine) {
          offenders.push(
            `volume ${volume.id} (${volume.currentLetter ?? volume.volumeGuid}) watches ` +
              `"${root.relativePath}"${root.isActive ? '' : ' (inactive)'}`,
          );
        }
      }
    }

    if (offenders.length > 0) {
      throw new Error(
        `${VIOLATION} the catalog is not confined to the sandbox, so a source id could name a ` +
          `file the user cares about:\n  ${offenders.join('\n  ')}\n  sandbox: ${this.absoluteRoot}`,
      );
    }
  }

  /**
   * Layer 3. Reads back every job the product recorded and checks where it went. Deliberately not
   * a re-run of layer 2's arithmetic on the same input: the input here is what the *service*
   * stored, which is the only record of what the engine was actually told to do.
   */
  auditRecordedJobs(jobs: readonly RecordedJob[]): void {
    const offenders: string[] = [];

    for (const job of jobs) {
      const where = `job #${job.id} (${job.type})`;

      if (!job.isIntraVolume) {
        offenders.push(
          `${where} is cross-volume: it would delete its source into the recycle bin, and this ` +
            `suite must never put anything there.`,
        );
      }
      for (const [label, volumeId] of [
        ['source volume', job.sourceVolumeId],
        ['target volume', job.targetVolumeId],
      ] as const) {
        if (volumeId !== null && volumeId !== undefined && volumeId !== this.volumeId) {
          offenders.push(`${where} names ${label} ${volumeId}, not the sandbox volume ${this.volumeId}.`);
        }
      }

      if (typeof job.sourcePath === 'string' && job.sourcePath.length > 0) {
        const violation = this.pathViolation(job.sourcePath);
        if (violation !== null) {
          offenders.push(`${where} source: ${violation}`);
        }
      }

      if (typeof job.targetPath === 'string' && job.targetPath.length > 0) {
        // Rename stores the new leaf name here, not a path (OperationJobDto.TargetPath).
        const violation =
          job.type === 'RenameFile' || job.type === 'RenameFolder'
            ? this.leafViolation(job.targetPath, 'stored new name')
            : this.pathViolation(job.targetPath);
        if (violation !== null) {
          offenders.push(`${where} target: ${violation}`);
        }
      }
    }

    if (offenders.length > 0) {
      throw new Error(
        `${VIOLATION} the service recorded work outside the sandbox:\n  ${offenders.join('\n  ')}\n` +
          `  sandbox: ${this.absoluteRoot}`,
      );
    }
  }
}

/** Prefix every refusal carries, so a violation is never mistaken for an ordinary failed assertion. */
const VIOLATION = '[SANDBOX FENCE]';

const KNOWN_TYPES = new Set(['CreateFolder', 'RenameFile', 'RenameFolder', 'MoveFile', 'MoveFolder']);

/** The shape of an enqueue body, as `CreateJobRequest` serializes it. */
export interface JobRequestLike {
  readonly type?: unknown;
  readonly sourceFileId?: number | null;
  readonly sourceDirectoryId?: number | null;
  readonly targetVolumeId?: number | null;
  readonly targetRelativePath?: unknown;
  readonly newName?: unknown;
}

/** The slice of `OperationJobDto` the audit reads. */
export interface RecordedJob {
  readonly id: number;
  readonly type: string;
  readonly isIntraVolume: boolean;
  readonly sourceVolumeId: number | null;
  readonly targetVolumeId: number | null;
  readonly sourcePath: string | null;
  readonly targetPath: string | null;
}

/** Just enough of `Api` for the perimeter check, so the fence does not depend on the whole class. */
export interface PerimeterReader {
  volumes(): Promise<readonly { id: number; volumeGuid: string; currentLetter: string | null }[]>;
  volume(id: number): Promise<{
    readonly watchedRoots: readonly { relativePath: string; isActive: boolean }[];
  }>;
}

/**
 * A volume-relative path in one spelling, or null when it is not one at all. Anything rooted
 * (`C:\…`, `\\server\share`, a leading separator) or containing `..` is refused rather than
 * canonicalised: the product never produces those, so seeing one means the test is doing
 * something the fence was written to stop.
 */
function normalize(relativePath: string): string | null {
  const value = relativePath.replace(/\//g, '\\');
  if (value.startsWith('\\') || /^[A-Za-z]:/.test(value)) {
    return null;
  }
  const segments = value.split('\\').filter((segment) => segment.length > 0);
  if (segments.some((segment) => segment === '..' || segment === '.')) {
    return null;
  }
  return segments.join('\\');
}

/** The absolute form of a volume-relative path, for assertions that look at the real filesystem. */
export function absoluteOf(driveRoot: string, relativePath: string): string {
  return path.join(driveRoot, relativePath);
}
