# Move Dialog Folder-Tree Picker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the free-text "Cartella destinazione" path input in the move dialog (`OperationPicker`) with a browsable folder tree (breadcrumb + folder list) plus an inline "new folder" affordance, so the user never types a path by hand.

**Architecture:** Frontend-only. `OperationPicker` gains local navigation state (`crumbs`, `newFolderSegments`, `dirChildren`) driven by the existing, offline-capable `CatalogApi.children()` endpoint (same one the Catalogo screen already uses). The destination path sent to preview/enqueue is derived by joining real crumbs + virtual (not-yet-created) folder segments with `\`, unchanged from the wire-format the backend already expects.

**Tech Stack:** Angular 21 (standalone, OnPush, signals), Vitest.

## Global Constraints

- No backend changes. `POST /operations/preview` and `POST /operations` keep taking a plain `targetRelativePath: string` (`CreateJobRequest`).
- Do not reuse the real-filesystem browse endpoint (`FoldersController` / `SetupApi.browse`) — it 409s when the volume is offline, and the move dialog must support offline targets (per the project's projection/estimate model).
- Manual path entry is fully removed — no advanced/manual-mode fallback.
- Path segments join with backslash (`\`), matching `QueueService.JoinPath` on the backend and the existing placeholder convention.
- Per project CLAUDE.md, all UI work must go through the `impeccable` skill before being considered done.
- Spec reference: `docs/superpowers/specs/2026-07-02-move-folder-picker-design.md`.

---

### Task 1: Folder-tree picker in `OperationPicker`

**Files:**
- Modify: `src/frontend/src/app/shared/components/operation-picker/operation-picker.ts`
- Modify: `src/frontend/src/app/shared/components/operation-picker/operation-picker.html`
- Modify: `src/frontend/src/app/shared/components/operation-picker/operation-picker.scss`
- Modify: `src/frontend/src/app/shared/components/operation-picker/operation-picker.spec.ts`

**Interfaces:**
- Consumes: `CatalogApi.children(volumeId: number, directoryId: number | null, skip = 0, take = 50): Observable<CatalogChildrenDto>` (`core/api/catalog-api.service.ts`) — only `result.directories: CatalogDirDto[]` is used. `CatalogDirDto` = `{ id: number; name: string; materializedPath: string; childDirectoryCount: number; fileCount: number }` (`core/models/catalog.models.ts`). `VolumeDto` = `{ id, isOnline, lastSeenUtc, label, currentLetter, ... }` (same file). `RelativeTimePipe` (`shared/pipes/relative-time.pipe.ts`, pipe name `relativeTime`).
- Produces: nothing consumed elsewhere — `OperationPicker` is a leaf dialog component opened from `catalog.html`/`search` with `[files]` input and `(closed)` output, both unchanged.

This task replaces the plain-text folder field with real navigation state. Because the template's `[(ngModel)]` binding and the component's data model change together (a getter-only `targetFolder` can't be two-way-bound), the `.ts`/`.html`/`.scss` changes land in one step — splitting them would leave an intermediate state that doesn't compile.

- [ ] **Step 1: Write the failing tests**

Replace the full contents of `operation-picker.spec.ts`:

```typescript
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { CatalogApi } from '../../../core/api/catalog-api.service';
import { QueueApi } from '../../../core/api/queue-api.service';
import { VolumesApi } from '../../../core/api/volumes-api.service';
import {
  CatalogChildrenDto, CatalogDirDto, CreateJobRequest, FeasibilityResult, SelectedFile,
} from '../../../core/models/catalog.models';
import { OperationPicker } from './operation-picker';

const files: SelectedFile[] = [
  { fileId: 1, name: 'photo.jpg', sizeBytes: 1000, volumeId: 1 },
  { fileId: 2, name: 'clip.mp4', sizeBytes: 2000, volumeId: 1 },
];

const feasibility: FeasibilityResult = {
  feasible: true, requiredBytes: 1000, reservedBytes: 0,
  availableEstimateBytes: 9000, deficitBytes: 0, estimateIsLive: true, blockingVolumeId: null,
};

function dir(id: number, name: string): CatalogDirDto {
  return { id, name, materializedPath: name, childDirectoryCount: 0, fileCount: 0 };
}

function childrenResult(directories: CatalogDirDto[]): CatalogChildrenDto {
  return {
    directories,
    files: { items: [], totalCount: 0, skip: 0, take: 50 },
    volumeIsOnline: true,
    volumeLabel: 'Dati',
    volumeLetter: 'D:',
    currentDirectoryId: null,
    currentDirectoryPath: null,
  };
}

function setup() {
  const enqueue = vi.fn((_req: CreateJobRequest) => of({} as never));
  const preview = vi.fn((_req: CreateJobRequest) => of(feasibility));
  const children = vi.fn((_volumeId: number, dirId: number | null) => {
    if (dirId === null) return of(childrenResult([dir(10, 'Documenti'), dir(11, 'Archivio')]));
    if (dirId === 10) return of(childrenResult([dir(20, 'Foto')]));
    return of(childrenResult([]));
  });

  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: QueueApi, useValue: { enqueue, preview } },
      { provide: CatalogApi, useValue: { children } },
      { provide: VolumesApi, useValue: { list: () => of([]) } },
      { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
    ],
  });

  const fixture = TestBed.createComponent(OperationPicker);
  fixture.componentRef.setInput('files', files);
  const cmp = fixture.componentInstance as unknown as {
    targetVolumeId: number | null;
    canSubmit: boolean;
    targetFolder: string;
    crumbs: { (): { id: number; name: string }[]; set(v: { id: number; name: string }[]): void };
    newFolderSegments: { (): string[] };
    dirChildren: { (): CatalogDirDto[] };
    newFolderName: string;
    openDirectory(dir: CatalogDirDto): Promise<void>;
    navigateToRoot(): Promise<void>;
    navigateToCrumb(index: number): Promise<void>;
    navigateToVirtualCrumb(index: number): void;
    openNewFolderInput(): void;
    confirmNewFolder(): void;
    enqueue(): Promise<void>;
    runPreview(): Promise<void>;
  };
  cmp.targetVolumeId = 1;

  return { enqueue, preview, children, cmp };
}

describe('OperationPicker target path', () => {
  it('root selection (no folder chosen) is a valid, empty-path target', async () => {
    const { enqueue, cmp } = setup();

    expect(cmp.canSubmit).toBe(true);
    expect(cmp.targetFolder).toBe('');

    await cmp.enqueue();
    expect(enqueue.mock.calls[0][0].targetRelativePath).toBe('');
  });

  it('descending into a real folder fetches its children and extends the path', async () => {
    const { children, cmp } = setup();

    await cmp.openDirectory(dir(10, 'Documenti'));

    expect(children).toHaveBeenCalledWith(1, 10);
    expect(cmp.crumbs()).toEqual([{ id: 10, name: 'Documenti' }]);
    expect(cmp.dirChildren()).toEqual([dir(20, 'Foto')]);
    expect(cmp.targetFolder).toBe('Documenti');
  });

  it('navigating to an ancestor crumb truncates the path and refetches', async () => {
    const { children, cmp } = setup();

    await cmp.openDirectory(dir(10, 'Documenti'));
    await cmp.openDirectory(dir(20, 'Foto'));
    expect(cmp.targetFolder).toBe('Documenti\\Foto');

    children.mockClear();
    await cmp.navigateToCrumb(0);

    expect(children).toHaveBeenCalledWith(1, 10);
    expect(cmp.crumbs()).toEqual([{ id: 10, name: 'Documenti' }]);
    expect(cmp.targetFolder).toBe('Documenti');
  });

  it('creating a new folder appends a virtual segment without calling the catalog API', async () => {
    const { children, cmp } = setup();

    await cmp.openDirectory(dir(10, 'Documenti'));
    children.mockClear();

    cmp.openNewFolderInput();
    cmp.newFolderName = 'Foto 2025';
    cmp.confirmNewFolder();

    expect(children).not.toHaveBeenCalled();
    expect(cmp.newFolderSegments()).toEqual(['Foto 2025']);
    expect(cmp.dirChildren()).toEqual([]);
    expect(cmp.targetFolder).toBe('Documenti\\Foto 2025');
  });

  it('new folders can nest, and a virtual-crumb click drops only the deeper one', async () => {
    const { children, cmp } = setup();

    cmp.openNewFolderInput();
    cmp.newFolderName = 'A';
    cmp.confirmNewFolder();
    cmp.openNewFolderInput();
    cmp.newFolderName = 'B';
    cmp.confirmNewFolder();
    expect(cmp.targetFolder).toBe('A\\B');

    children.mockClear();
    cmp.navigateToVirtualCrumb(0);

    expect(children).not.toHaveBeenCalled();
    expect(cmp.newFolderSegments()).toEqual(['A']);
    expect(cmp.targetFolder).toBe('A');
  });

  it('enqueue sends the derived folder only, without appending the file name', async () => {
    const { enqueue, cmp } = setup();

    await cmp.openDirectory(dir(11, 'Archivio'));
    await cmp.enqueue();

    expect(enqueue).toHaveBeenCalledTimes(2);
    expect(enqueue.mock.calls[0][0].targetRelativePath).toBe('Archivio');
    expect(enqueue.mock.calls[1][0].targetRelativePath).toBe('Archivio');
  });

  it('preview sends the derived folder only, without appending the file name', async () => {
    const { preview, cmp } = setup();

    await cmp.openDirectory(dir(11, 'Archivio'));
    await cmp.runPreview();

    expect(preview).toHaveBeenCalledTimes(1);
    expect(preview.mock.calls[0][0].targetRelativePath).toBe('Archivio');
  });
});
```

- [ ] **Step 2: Run the tests to confirm they fail**

Run: `npx vitest run operation-picker.spec.ts` (from `src/frontend`)
Expected: FAIL — `cmp.crumbs`, `cmp.dirChildren`, `cmp.openDirectory`, etc. are `undefined` (`TypeError: cmp.openDirectory is not a function` or similar), and `CatalogApi` isn't injected by the component yet so the `children` mock is never wired.

- [ ] **Step 3: Implement the component, template and styles**

Replace the full contents of `operation-picker.ts`:

```typescript
import {
  ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { CatalogApi } from '../../../core/api/catalog-api.service';
import { QueueApi } from '../../../core/api/queue-api.service';
import { VolumesStore } from '../../../features/volumes/volumes.store';
import { BytesPipe } from '../../pipes/bytes.pipe';
import { RelativeTimePipe } from '../../pipes/relative-time.pipe';
import { FtPill } from '../ft-pill/ft-pill';
import {
  CatalogDirDto, FeasibilityResult, SelectedFile, VolumeDto,
} from '../../../core/models/catalog.models';

interface FolderCrumb {
  id: number;
  name: string;
}

@Component({
  selector: 'ft-operation-picker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, BytesPipe, RelativeTimePipe, FtPill],
  templateUrl: './operation-picker.html',
  styleUrl: './operation-picker.scss',
})
export class OperationPicker implements OnInit {
  readonly files = input.required<SelectedFile[]>();
  readonly closed = output<void>();

  protected readonly volumes = inject(VolumesStore);
  private readonly api = inject(QueueApi);
  private readonly catalogApi = inject(CatalogApi);
  private readonly router = inject(Router);

  protected targetVolumeId: number | null = null;

  protected readonly crumbs = signal<FolderCrumb[]>([]);
  protected readonly newFolderSegments = signal<string[]>([]);
  protected readonly dirChildren = signal<CatalogDirDto[]>([]);
  protected readonly loadingDirs = signal(false);
  protected readonly newFolderInputOpen = signal(false);
  protected newFolderName = '';

  protected readonly preview = signal<FeasibilityResult | null>(null);
  protected readonly previewing = signal(false);
  protected readonly enqueueing = signal(false);
  protected readonly enqueued = signal(false);
  protected readonly enqueuedCount = signal(0);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.volumes.loadList();
    const online = this.volumes.catalogable().find(v => v.isOnline);
    if (online) {
      this.targetVolumeId = online.id;
      void this.loadChildren(null);
    }
  }

  protected get totalBytes(): number {
    return this.files().reduce((s, f) => s + f.sizeBytes, 0);
  }

  protected get targetVolume(): VolumeDto | undefined {
    return this.volumes.catalogable().find(v => v.id === this.targetVolumeId);
  }

  /** Real crumbs (existing folders) + virtual segments (not created yet), joined for the API. */
  protected get targetFolder(): string {
    return [...this.crumbs().map(c => c.name), ...this.newFolderSegments()].join('\\');
  }

  protected get canSubmit(): boolean {
    return this.targetVolumeId !== null;
  }

  protected async onVolumeChange(): Promise<void> {
    this.preview.set(null);
    this.crumbs.set([]);
    this.newFolderSegments.set([]);
    this.newFolderInputOpen.set(false);
    await this.loadChildren(null);
  }

  protected async openDirectory(dir: CatalogDirDto): Promise<void> {
    this.preview.set(null);
    this.crumbs.update(c => [...c, { id: dir.id, name: dir.name }]);
    await this.loadChildren(dir.id);
  }

  protected async navigateToRoot(): Promise<void> {
    this.preview.set(null);
    this.crumbs.set([]);
    this.newFolderSegments.set([]);
    await this.loadChildren(null);
  }

  protected async navigateToCrumb(index: number): Promise<void> {
    this.preview.set(null);
    const target = this.crumbs()[index];
    this.crumbs.update(c => c.slice(0, index + 1));
    this.newFolderSegments.set([]);
    await this.loadChildren(target.id);
  }

  /** Virtual crumbs never hit the API — dropping the deeper ones is a pure client-side truncation. */
  protected navigateToVirtualCrumb(index: number): void {
    this.preview.set(null);
    this.newFolderSegments.update(s => s.slice(0, index + 1));
  }

  protected openNewFolderInput(): void {
    this.newFolderName = '';
    this.newFolderInputOpen.set(true);
  }

  protected cancelNewFolder(): void {
    this.newFolderInputOpen.set(false);
    this.newFolderName = '';
  }

  protected confirmNewFolder(): void {
    const name = this.newFolderName.trim();
    if (!name) return;
    this.preview.set(null);
    this.newFolderSegments.update(s => [...s, name]);
    this.dirChildren.set([]);
    this.newFolderInputOpen.set(false);
    this.newFolderName = '';
  }

  private async loadChildren(dirId: number | null): Promise<void> {
    if (this.targetVolumeId === null || this.newFolderSegments().length > 0) {
      this.dirChildren.set([]);
      return;
    }
    this.loadingDirs.set(true);
    try {
      const result = await firstValueFrom(this.catalogApi.children(this.targetVolumeId, dirId));
      this.dirChildren.set(result.directories);
    } catch (e) {
      this.error.set((e as Error).message);
      this.dirChildren.set([]);
    } finally {
      this.loadingDirs.set(false);
    }
  }

  protected async runPreview(): Promise<void> {
    if (!this.canSubmit) return;
    const first = this.files()[0];
    const folder = this.targetFolder;

    this.previewing.set(true);
    this.preview.set(null);
    this.error.set(null);
    try {
      // Send the destination folder only — the backend appends the file name.
      const result = await firstValueFrom(this.api.preview({
        type: 'MoveFile',
        sourceFileId: first.fileId,
        sourceDirectoryId: null,
        targetVolumeId: this.targetVolumeId!,
        targetRelativePath: folder,
        newName: null,
      }));
      this.preview.set(result);
    } catch (e) {
      this.error.set((e as Error).message);
    } finally {
      this.previewing.set(false);
    }
  }

  protected async enqueue(): Promise<void> {
    if (!this.canSubmit) return;
    const folder = this.targetFolder;

    this.enqueueing.set(true);
    this.error.set(null);
    let count = 0;
    try {
      for (const file of this.files()) {
        // Send the destination folder only — the backend appends the file name.
        await firstValueFrom(this.api.enqueue({
          type: 'MoveFile',
          sourceFileId: file.fileId,
          sourceDirectoryId: null,
          targetVolumeId: this.targetVolumeId!,
          targetRelativePath: folder,
          newName: null,
        }));
        count++;
      }
      this.enqueuedCount.set(count);
      this.enqueued.set(true);
    } catch (e) {
      this.error.set((e as Error).message);
    } finally {
      this.enqueueing.set(false);
    }
  }

  protected goToQueue(): void {
    this.closed.emit();
    void this.router.navigate(['/queue']);
  }

  protected close(): void {
    this.closed.emit();
  }

  protected onBackdropClick(event: MouseEvent): void {
    if ((event.target as Element).classList.contains('picker-backdrop')) {
      this.close();
    }
  }
}
```

Replace the full contents of `operation-picker.html`:

```html
<div class="picker-backdrop" (click)="onBackdropClick($event)" role="dialog" aria-modal="true" aria-label="Sposta file">
  <div class="picker-modal">

    <!-- Header -->
    <div class="picker-header">
      <div class="picker-title">
        Sposta {{ files().length === 1 ? '1 file' : files().length + ' file' }}
      </div>
      <button class="picker-close" type="button" (click)="close()" aria-label="Chiudi">✕</button>
    </div>

    @if (enqueued()) {
      <!-- Success state -->
      <div class="picker-body">
        <div class="enqueued-state">
          <div class="enqueued-icon">✓</div>
          <div class="enqueued-title">
            {{ enqueuedCount() === 1 ? '1 operazione accodata' : enqueuedCount() + ' operazioni accodate' }}
          </div>
          <div class="enqueued-sub">
            Le operazioni vengono eseguite appena il volume destinazione è disponibile.
          </div>
          <button type="button" class="ft-btn ft-btn--primary" (click)="goToQueue()">
            Vai alla Coda →
          </button>
        </div>
      </div>

    } @else {

      <div class="picker-body">

        <!-- File summary -->
        <div class="file-summary">
          <div class="file-summary-names">
            @for (f of files().slice(0, 3); track f.fileId) {
              <span class="file-chip mono">{{ f.name }}</span>
            }
            @if (files().length > 3) {
              <span class="file-chip-more txt-faint">+{{ files().length - 3 }} altri</span>
            }
          </div>
          <div class="file-summary-size txt-faint">
            Totale: <span class="mono">{{ totalBytes | bytes }}</span>
          </div>
        </div>

        <!-- Target volume -->
        <div class="picker-field">
          <label class="picker-label" for="picker-vol">Volume destinazione</label>
          <select id="picker-vol" class="picker-select" [(ngModel)]="targetVolumeId" (ngModelChange)="onVolumeChange()">
            <option [ngValue]="null" disabled>Scegli un volume…</option>
            @for (v of volumes.catalogable(); track v.id) {
              <option [ngValue]="v.id">
                {{ v.label ?? '(senza etichetta)' }}
                {{ v.currentLetter ? '(' + v.currentLetter + ')' : '' }}
                {{ v.isOnline ? '' : '— offline' }}
              </option>
            }
          </select>
        </div>

        <!-- Target folder -->
        <div class="picker-field">
          <label class="picker-label" id="picker-folder-label">Cartella destinazione</label>

          @if (targetVolume && !targetVolume.isOnline) {
            <div class="folder-offline-notice">
              <span class="offline-icon">⚠</span>
              <span>
                Struttura da ultima scansione · dati aggiornati al
                <span class="mono">{{ targetVolume.lastSeenUtc | relativeTime }}</span>
              </span>
            </div>
          }

          <div class="folder-picker" aria-labelledby="picker-folder-label">
            <nav class="folder-crumbs" aria-label="Percorso cartella destinazione">
              <button
                type="button"
                class="folder-crumb folder-crumb--root"
                [class.active]="crumbs().length === 0 && newFolderSegments().length === 0"
                (click)="navigateToRoot()"
              >
                Radice
              </button>
              @for (c of crumbs(); track c.id; let i = $index) {
                <span class="folder-crumb-sep" aria-hidden="true">›</span>
                <button
                  type="button"
                  class="folder-crumb"
                  [class.active]="i === crumbs().length - 1 && newFolderSegments().length === 0"
                  (click)="navigateToCrumb(i)"
                >
                  {{ c.name }}
                </button>
              }
              @for (seg of newFolderSegments(); track $index; let i = $index) {
                <span class="folder-crumb-sep" aria-hidden="true">›</span>
                <button
                  type="button"
                  class="folder-crumb folder-crumb--new"
                  [class.active]="i === newFolderSegments().length - 1"
                  (click)="navigateToVirtualCrumb(i)"
                >
                  {{ seg }}
                </button>
              }
            </nav>

            <div class="folder-list">
              @if (newFolderSegments().length > 0) {
                <div class="folder-empty txt-faint">
                  Cartella nuova — verrà creata al momento dello spostamento.
                </div>
              } @else if (loadingDirs()) {
                <div class="folder-empty txt-faint">Caricamento…</div>
              } @else if (dirChildren().length === 0) {
                <div class="folder-empty txt-faint">Cartella vuota</div>
              } @else {
                @for (dir of dirChildren(); track dir.id) {
                  <button
                    type="button"
                    class="folder-row"
                    [title]="dir.materializedPath"
                    (click)="openDirectory(dir)"
                  >
                    <span class="folder-row-icon" aria-hidden="true">▤</span>
                    <span class="folder-row-name" [title]="dir.name">{{ dir.name }}</span>
                    <span class="folder-row-meta mono txt-faint">
                      @if (dir.childDirectoryCount > 0) {
                        <span>{{ dir.childDirectoryCount }} cartelle</span>
                      }
                      @if (dir.fileCount > 0) {
                        <span>{{ dir.fileCount }} file</span>
                      }
                    </span>
                  </button>
                }
              }
            </div>

            @if (newFolderInputOpen()) {
              <div class="new-folder-row">
                <input
                  class="new-folder-input mono"
                  type="text"
                  placeholder="Nome cartella"
                  [(ngModel)]="newFolderName"
                  (keydown.enter)="confirmNewFolder()"
                  autocomplete="off"
                  spellcheck="false"
                />
                <button type="button" class="ft-btn ft-btn--ghost" (click)="confirmNewFolder()">Crea</button>
                <button type="button" class="ft-btn ft-btn--ghost" (click)="cancelNewFolder()">Annulla</button>
              </div>
            } @else {
              <button type="button" class="new-folder-toggle" (click)="openNewFolderInput()">
                + Nuova cartella
              </button>
            }
          </div>

          <span class="picker-hint txt-faint">
            Percorso: <span class="mono">{{ targetFolder || '(radice volume)' }}</span> ·
            la cartella viene creata se non esiste.
          </span>
        </div>

        <!-- Preview -->
        <div class="picker-preview-row">
          <button
            type="button"
            class="ft-btn ft-btn--ghost"
            [disabled]="!canSubmit || previewing()"
            (click)="runPreview()"
          >
            {{ previewing() ? '…' : 'Verifica spazio' }}
          </button>

          @if (preview(); as p) {
            <div class="feasibility-chip" [class.feasibility-chip--ok]="p.feasible" [class.feasibility-chip--bad]="!p.feasible">
              @if (p.feasible) {
                <ft-pill variant="done">Fattibile</ft-pill>
                <span class="feasibility-detail txt-faint">
                  Richiesto {{ p.requiredBytes | bytes }} ·
                  Disponibile {{ p.availableEstimateBytes | bytes }}
                  @if (!p.estimateIsLive) { <span class="stale-flag" title="Stima su dati offline">~</span> }
                </span>
              } @else {
                <ft-pill variant="block">Spazio insufficiente</ft-pill>
                <span class="feasibility-detail txt-faint">
                  Mancano {{ p.deficitBytes | bytes }}
                  @if (!p.estimateIsLive) { <span class="stale-flag" title="Stima su dati offline">~</span> }
                </span>
              }
            </div>
          }
        </div>

        <!-- Error -->
        @if (error()) {
          <div class="picker-error">
            <span class="picker-error-icon">⚠</span>
            <span class="mono">{{ error() }}</span>
          </div>
        }

      </div>

      <!-- Footer -->
      <div class="picker-footer">
        <button type="button" class="ft-btn ft-btn--ghost" (click)="close()" [disabled]="enqueueing()">
          Annulla
        </button>
        <button
          type="button"
          class="ft-btn ft-btn--primary"
          [disabled]="!canSubmit || enqueueing()"
          (click)="enqueue()"
        >
          {{ enqueueing() ? 'Accodamento…' : 'Accoda →' }}
        </button>
      </div>

    }

  </div>
</div>
```

Append to `operation-picker.scss` (after the existing `.picker-hint` block, before `// Preview row`):

```scss
// Folder-tree picker
.folder-offline-notice {
  display: flex;
  align-items: center;
  gap: var(--sp-2);
  padding: var(--sp-2) var(--sp-3);
  background: color-mix(in srgb, var(--amber) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--amber) 30%, transparent);
  border-radius: var(--r-ctl);
  font-size: 12px;
  color: var(--amber);
}

.offline-icon {
  font-size: 13px;
  flex-shrink: 0;
}

.folder-picker {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--line);
  border-radius: var(--r-panel);
  overflow: hidden;
  background: var(--panel-2);
}

.folder-crumbs {
  display: flex;
  align-items: center;
  gap: 2px;
  flex-wrap: wrap;
  padding: var(--sp-2) var(--sp-3);
  border-bottom: 1px solid var(--line);
}

.folder-crumb-sep {
  color: var(--txt-faint);
  font-size: 12px;
  padding: 0 2px;
  user-select: none;
}

.folder-crumb {
  display: inline-flex;
  align-items: center;
  height: 24px;
  padding: 0 var(--sp-2);
  background: transparent;
  border: 1px solid transparent;
  border-radius: var(--r-ctl);
  color: var(--txt-dim);
  font-size: 12.5px;
  font-weight: 500;
  cursor: pointer;
  max-width: 160px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  transition: background var(--t-fast), color var(--t-fast), border-color var(--t-fast);

  &:hover:not(.active) {
    color: var(--txt);
    background: var(--panel-3);
    border-color: var(--line);
  }

  &--root {
    color: var(--teal);
    font-weight: 600;
  }

  &--new {
    color: var(--amber);
  }

  &.active {
    color: var(--txt);
    background: var(--panel-3);
    border-color: var(--line);
    cursor: default;
  }
}

.folder-list {
  display: flex;
  flex-direction: column;
  max-height: 180px;
  overflow-y: auto;
}

.folder-empty {
  padding: var(--sp-4) var(--sp-3);
  text-align: center;
  font-size: 12.5px;
}

.folder-row {
  display: flex;
  align-items: center;
  gap: var(--sp-2);
  padding: var(--sp-2) var(--sp-3);
  background: transparent;
  border: none;
  border-bottom: 1px solid var(--line-soft);
  cursor: pointer;
  text-align: left;
  transition: background var(--t-fast);

  &:last-child {
    border-bottom: none;
  }

  &:hover {
    background: var(--panel-3);
  }
}

.folder-row-icon {
  font-size: 15px;
  color: var(--amber);
  flex-shrink: 0;
}

.folder-row-name {
  font-size: 13px;
  color: var(--txt);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
}

.folder-row-meta {
  display: flex;
  gap: 6px;
  font-size: 11px;
  flex-shrink: 0;
}

.new-folder-row {
  display: flex;
  align-items: center;
  gap: var(--sp-2);
  padding: var(--sp-2) var(--sp-3);
  border-top: 1px solid var(--line);
}

.new-folder-input {
  flex: 1;
  height: 30px;
  padding: 0 var(--sp-2);
  background: var(--panel);
  border: 1px solid var(--line);
  border-radius: var(--r-ctl);
  color: var(--txt);
  font-size: 12.5px;
  outline: none;

  &:focus {
    border-color: var(--teal);
    box-shadow: 0 0 0 2px var(--teal-dim);
  }

  &::placeholder {
    color: var(--txt-faint);
  }
}

.new-folder-toggle {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--sp-2) var(--sp-3);
  background: transparent;
  border: none;
  border-top: 1px solid var(--line);
  color: var(--teal);
  font-size: 12.5px;
  font-weight: 500;
  cursor: pointer;
  transition: background var(--t-fast);

  &:hover {
    background: var(--panel-3);
  }
}
```

- [ ] **Step 4: Run the tests to confirm they pass**

Run: `npx vitest run operation-picker.spec.ts` (from `src/frontend`)
Expected: PASS — all 7 tests green.

- [ ] **Step 5: Impeccable pass on the new markup/styles**

Per project CLAUDE.md ("Per tutto il lavoro UI usare la skill impeccable"), invoke the `impeccable` skill against the new folder-picker section of `operation-picker.html`/`operation-picker.scss` (breadcrumb, folder list, new-folder row) for a visual/UX polish pass. Apply any concrete adjustments it recommends, then re-run Step 4's test command to confirm nothing broke.

- [ ] **Step 6: Full frontend test suite**

Run: `npx ng test --watch=false` (from `src/frontend`)
Expected: PASS — no regressions in other components (Catalogo, Search, Queue) that render `ft-operation-picker`.

- [ ] **Step 7: Commit**

```bash
git add src/frontend/src/app/shared/components/operation-picker/operation-picker.ts src/frontend/src/app/shared/components/operation-picker/operation-picker.html src/frontend/src/app/shared/components/operation-picker/operation-picker.scss src/frontend/src/app/shared/components/operation-picker/operation-picker.spec.ts
git commit -m "feat(frontend): replace manual path input with folder-tree picker in move dialog"
```

---

## Self-Review Notes

- **Spec coverage:** breadcrumb navigation ✓ (Step 3 template + Step 1 tests), folder list with counts ✓, inline new-folder creation + nesting ✓, offline banner ✓, path derivation incl. root case ✓, `canSubmit` relaxed to volume-only ✓, no backend changes ✓, manual input fully removed ✓.
- **Placeholder scan:** none — every step has literal file contents or an exact command.
- **Type consistency:** `CatalogDirDto`, `VolumeDto`, `CatalogChildrenDto` match `core/models/catalog.models.ts` verbatim; `CatalogApi.children(volumeId, directoryId, skip, take)` signature matches `core/api/catalog-api.service.ts`; test mock only implements the 2-arg call shape actually used by `loadChildren`.
