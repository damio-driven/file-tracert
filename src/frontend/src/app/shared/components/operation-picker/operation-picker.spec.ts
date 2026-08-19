import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { CatalogApi } from '../../../core/api/catalog-api.service';
import { QueueApi } from '../../../core/api/queue-api.service';
import { VolumesApi } from '../../../core/api/volumes-api.service';
import {
  CatalogChildrenDto, CatalogDirDto, CreateJobRequest, FeasibilityResult, SelectedItem, VolumeDto,
} from '../../../core/models/catalog.models';
import { validateLeafName } from '../../validation/name.util';
import { OperationPicker } from './operation-picker';

const items: SelectedItem[] = [
  { kind: 'File', id: 1, name: 'photo.jpg', sizeBytes: 1000, volumeId: 1, relativePath: 'photo.jpg' },
  { kind: 'File', id: 2, name: 'clip.mp4', sizeBytes: 2000, volumeId: 1, relativePath: 'clip.mp4' },
];

const feasibility: FeasibilityResult = {
  feasible: true, requiredBytes: 1000, reservedBytes: 0,
  availableEstimateBytes: 9000, deficitBytes: 0, marginBytes: 30, estimateIsLive: true,
  blockingVolumeId: null,
};

function dir(id: number, name: string): CatalogDirDto {
  return {
    id, name, materializedPath: name, childDirectoryCount: 0, fileCount: 0,
    projectedState: 'None', pendingJobId: null,
  };
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

function setup(volumes: VolumeDto[] = []) {
  const enqueueBatch = vi.fn((reqs: CreateJobRequest[]) =>
    of(reqs.map((_, i) => ({ id: i + 1, blockReason: 'None' })) as never));
  const previewBatch = vi.fn((_reqs: CreateJobRequest[]) => of(feasibility));
  const volumeList = vi.fn(() => of(volumes));
  const children = vi.fn((_volumeId: number, dirId: number | null) => {
    if (dirId === null) return of(childrenResult([dir(10, 'Documenti'), dir(11, 'Archivio')]));
    if (dirId === 10) return of(childrenResult([dir(20, 'Foto')]));
    return of(childrenResult([]));
  });

  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: QueueApi, useValue: { enqueueBatch, previewBatch } },
      { provide: CatalogApi, useValue: { children } },
      { provide: VolumesApi, useValue: { list: volumeList } },
      { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
    ],
  });

  const fixture = TestBed.createComponent(OperationPicker);
  fixture.componentRef.setInput('items', items);
  // (fixture exposed for output subscriptions in the UX tests)
  const cmp = fixture.componentInstance as unknown as {
    targetVolumeId: number | null;
    canSubmit: boolean;
    targetFolder: string;
    crumbs: { (): { id: number; name: string }[]; set(v: { id: number; name: string }[]): void };
    newFolderSegments: { (): string[] };
    dirChildren: { (): CatalogDirDto[] };
    error: { (): string | null };
    enqueuedCount: { (): number };
    waitingCount: { (): number };
    parkedOnResourceCount: { (): number };
    newFolderName: string;
    openDirectory(dir: CatalogDirDto): Promise<void>;
    navigateToRoot(): Promise<void>;
    navigateToCrumb(index: number): Promise<void>;
    navigateToVirtualCrumb(index: number): void;
    openNewFolderInput(): void;
    confirmNewFolder(): void;
    cancelNewFolder(): void;
    onNewFolderInput(): void;
    newFolderError: { (): string | null };
    enqueue(): Promise<void>;
    runPreview(): Promise<void>;
    ngOnInit(): Promise<void>;
    loadVolumes(): Promise<void>;
    volumesLoading: { (): boolean };
    volumesError: { (): string | null };
    noDestinations: boolean;
  };
  cmp.targetVolumeId = 1;

  return { enqueueBatch, previewBatch, children, volumeList, cmp, fixture };
}

describe('OperationPicker target path', () => {
  it('root selection (no folder chosen) is a valid, empty-path target', async () => {
    const { enqueueBatch, cmp } = setup();

    expect(cmp.canSubmit).toBe(true);
    expect(cmp.targetFolder).toBe('');

    await cmp.enqueue();
    expect(enqueueBatch.mock.calls[0][0][0].targetRelativePath).toBe('');
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

  // C25 — one user gesture is ONE request, whatever the size of the selection.
  it('enqueue sends the whole selection in a single batch call', async () => {
    const { enqueueBatch, cmp } = setup();

    await cmp.openDirectory(dir(11, 'Archivio'));
    await cmp.enqueue();

    expect(enqueueBatch).toHaveBeenCalledTimes(1);
    const batch = enqueueBatch.mock.calls[0][0];
    expect(batch).toHaveLength(2);
    expect(batch.map(r => r.sourceFileId)).toEqual([1, 2]);
    expect(batch.every(r => r.targetRelativePath === 'Archivio')).toBe(true);
  });

  // FIX #5 — the preview must evaluate the WHOLE batch, not just the first file.
  it('preview sends one request per selected file, all with the derived folder', async () => {
    const { previewBatch, cmp } = setup();

    await cmp.openDirectory(dir(11, 'Archivio'));
    await cmp.runPreview();

    expect(previewBatch).toHaveBeenCalledTimes(1);
    const batch = previewBatch.mock.calls[0][0];
    expect(batch).toHaveLength(2);
    expect(batch.map(r => r.sourceFileId)).toEqual([1, 2]);
    expect(batch.every(r => r.targetRelativePath === 'Archivio')).toBe(true);
  });

  // Folder ops (step 8): a selected folder becomes a MoveFolder carrying sourceDirectoryId,
  // and the backend weighs its subtree at preview time.
  it('a selected folder is enqueued as MoveFolder with sourceDirectoryId', async () => {
    const { enqueueBatch, cmp, fixture } = setup();
    fixture.componentRef.setInput('items', [
      { kind: 'Folder', id: 7, name: 'Vacanze', sizeBytes: 0, volumeId: 1, relativePath: 'Vacanze' },
    ] satisfies SelectedItem[]);

    await cmp.openDirectory(dir(11, 'Archivio'));
    await cmp.enqueue();

    expect(enqueueBatch).toHaveBeenCalledTimes(1);
    const req = enqueueBatch.mock.calls[0][0][0];
    expect(req.type).toBe('MoveFolder');
    expect(req.sourceDirectoryId).toBe(7);
    expect(req.sourceFileId).toBeNull();
    expect(req.targetRelativePath).toBe('Archivio');
  });

  it('preview mixes MoveFile and MoveFolder per item kind', async () => {
    const { previewBatch, cmp, fixture } = setup();
    fixture.componentRef.setInput('items', [
      { kind: 'File', id: 3, name: 'a.jpg', sizeBytes: 10, volumeId: 1, relativePath: 'a.jpg' },
      { kind: 'Folder', id: 9, name: 'Dir', sizeBytes: 0, volumeId: 1, relativePath: 'Dir' },
    ] satisfies SelectedItem[]);

    await cmp.runPreview();

    const batch = previewBatch.mock.calls[0][0];
    expect(batch.map(r => r.type)).toEqual(['MoveFile', 'MoveFolder']);
    expect(batch[1].sourceDirectoryId).toBe(9);
  });

  // UX — "Annulla" must not wipe the selection: the picker signals a successful enqueue
  // via `completed`; a plain close emits only `closed`, and the parent keeps the selection.
  it('a successful enqueue emits completed; a plain close does not', async () => {
    const { cmp, fixture } = setup();

    const completed = vi.fn();
    const closed = vi.fn();
    fixture.componentInstance.completed.subscribe(completed);
    fixture.componentInstance.closed.subscribe(closed);

    (cmp as unknown as { close(): void }).close();
    expect(closed).toHaveBeenCalledTimes(1);
    expect(completed).not.toHaveBeenCalled();

    await cmp.enqueue();
    expect(completed).toHaveBeenCalledTimes(1);
  });

  // Step 9c: a conflict with a queued job is no longer a 409. The operation IS accepted,
  // parked behind the job that holds the entity — so the confirmation must say "waiting",
  // not report a clean success the user will not see happen.
  // A batch now weighs as ONE demand on the target, so its tail can legitimately come back
  // parked for space or for an unplugged volume — states a per-file enqueue never produced.
  it('counts the operations parked on space or on a volume, not only the dependent ones', async () => {
    const { cmp, enqueueBatch } = setup();
    enqueueBatch.mockImplementationOnce(() => of([
      { id: 1, blockReason: 'None' },
      { id: 2, blockReason: 'InsufficientSpace' },
      { id: 3, blockReason: 'TargetVolumeOffline' },
      { id: 4, blockReason: 'DependencyPending' },
    ] as never));

    await cmp.enqueue();

    expect(cmp.enqueuedCount()).toBe(4);
    expect(cmp.parkedOnResourceCount()).toBe(2);
    expect(cmp.waitingCount()).toBe(1);
  });

  it('counts the operations that came back queued-but-waiting', async () => {
    const { cmp, enqueueBatch } = setup();
    enqueueBatch.mockImplementationOnce(() =>
      of([{ id: 1, blockReason: 'DependencyPending' }, { id: 2, blockReason: 'None' }] as never));

    await cmp.enqueue();

    expect(cmp.enqueuedCount()).toBe(2);
    expect(cmp.waitingCount()).toBe(1);
  });

  it('a failed enqueue does not emit completed', async () => {
    const { cmp, fixture, enqueueBatch } = setup();
    enqueueBatch.mockImplementationOnce(() => throwError(() => new Error('boom')));

    const completed = vi.fn();
    fixture.componentInstance.completed.subscribe(completed);

    await cmp.enqueue();

    expect(completed).not.toHaveBeenCalled();
  });

  it('a stale fetch error is cleared once a subsequent navigation succeeds', async () => {
    const { children, cmp } = setup();
    children.mockImplementationOnce(() => throwError(() => new Error('boom')));

    await cmp.openDirectory(dir(10, 'Documenti'));
    expect(cmp.error()).toBe('boom');

    await cmp.navigateToRoot();

    expect(cmp.error()).toBeNull();
    expect(cmp.dirChildren()).toEqual([dir(10, 'Documenti'), dir(11, 'Archivio')]);
  });
});

function volume(id: number, label: string, isOnline = true): VolumeDto {
  return {
    id, volumeGuid: '\\\\?\\Volume{' + id + '}\\', label, currentLetter: 'D:', fileSystem: 'NTFS',
    isRemovable: false, isOnline, lastSeenUtc: new Date().toISOString(),
    capacityBytes: 1000, freeBytes: 900, fileCount: 0, lastFullScanUtc: null,
    dataIsLive: isOnline, isStale: !isOnline, kind: 'Fixed', isCatalogable: true,
  };
}

// C27 — the dialog used to fire `loadList()` and read `catalogable()` on the next line, so a
// cold store opened it empty: no volume, no tree, a dead "Accoda" button and no explanation.
describe('OperationPicker cold open', () => {
  it('waits for the volume list before deciding what to preselect', async () => {
    const listed = new Subject<VolumeDto[]>();
    const { cmp, children, volumeList } = setup();
    cmp.targetVolumeId = null;
    volumeList.mockImplementationOnce(() => listed.asObservable());

    const init = cmp.ngOnInit();
    expect(cmp.volumesLoading()).toBe(true);
    expect(cmp.targetVolumeId).toBeNull();

    listed.next([volume(4, 'Archivio')]);
    listed.complete();
    await init;

    expect(cmp.volumesLoading()).toBe(false);
    expect(cmp.targetVolumeId).toBe(4);
    // ...and the folder tree of that volume was actually asked for.
    expect(children).toHaveBeenCalledWith(4, null);
  });

  it('says why it is empty when the volume list cannot be read, and can try again', async () => {
    const { cmp, volumeList } = setup();
    cmp.targetVolumeId = null;
    volumeList.mockImplementationOnce(() => throwError(() => new Error('Servizio non raggiungibile')));

    await cmp.ngOnInit();

    expect(cmp.volumesError()).toBe('Servizio non raggiungibile');
    expect(cmp.canSubmit).toBe(false);

    volumeList.mockImplementationOnce(() => of([volume(7, 'Foto')]));
    await cmp.loadVolumes();

    expect(cmp.volumesError()).toBeNull();
    expect(cmp.targetVolumeId).toBe(7);
  });

  it('distinguishes "no destination exists" from "still loading"', async () => {
    const { cmp } = setup();
    cmp.targetVolumeId = null;

    await cmp.ngOnInit();

    expect(cmp.volumesLoading()).toBe(false);
    expect(cmp.volumesError()).toBeNull();
    expect(cmp.noDestinations).toBe(true);
  });

  it('does not flash a loading state over a list it already has', async () => {
    const { cmp } = setup([volume(4, 'Archivio')]);
    cmp.targetVolumeId = null;
    await cmp.ngOnInit();          // warms the root-scoped store

    const second = cmp.loadVolumes();
    expect(cmp.volumesLoading()).toBe(false);
    await second;
  });
});

// K14 — the inline "new folder" name used to be checked for emptiness only, while the rename /
// new-folder dialog ran `validateLeafName`. `foo\bar` was accepted in one place and refused in
// the other, for the same field of the same app.
describe('OperationPicker new folder name', () => {
  it('refuses a name with a path separator, in the words the other dialog uses', () => {
    const { cmp } = setup();

    cmp.openNewFolderInput();
    cmp.newFolderName = 'foto\\2025';
    cmp.confirmNewFolder();

    expect(cmp.newFolderError()).toBe(validateLeafName('foto\\2025'));
    expect(cmp.newFolderError()).toContain('separatori di percorso');
    expect(cmp.newFolderSegments()).toEqual([]);
  });

  it('refuses the characters Windows forbids', () => {
    const { cmp } = setup();

    cmp.openNewFolderInput();
    cmp.newFolderName = 'foto?2025';
    cmp.confirmNewFolder();

    expect(cmp.newFolderError()).toBe('Il nome contiene caratteri non consentiti.');
    expect(cmp.newFolderSegments()).toEqual([]);
  });

  it('drops the complaint as soon as the name is being fixed, and accepts a valid one', () => {
    const { cmp } = setup();

    cmp.openNewFolderInput();
    cmp.newFolderName = '..';
    cmp.confirmNewFolder();
    expect(cmp.newFolderError()).not.toBeNull();

    cmp.newFolderName = 'Foto 2025';
    cmp.onNewFolderInput();
    expect(cmp.newFolderError()).toBeNull();

    cmp.confirmNewFolder();
    expect(cmp.newFolderSegments()).toEqual(['Foto 2025']);
  });
});

// 11b split RequiredBytes from MarginBytes, but the deficit still INCLUDES the margin. Showing
// the deficit alone gives a number the user cannot find anywhere in their own file sizes.
describe('OperationPicker space verdict', () => {
  it('names the margin alongside the requirement when the batch fits', async () => {
    const { cmp, fixture } = setup();

    await cmp.runPreview();
    await fixture.whenStable();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('di margine');
    expect(text).toContain('disponibile');
  });

  it('breaks the deficit down into requirement + margin when it does not fit', async () => {
    const { cmp, fixture, previewBatch } = setup();
    previewBatch.mockImplementationOnce(() => of({
      feasible: false, requiredBytes: 1000, reservedBytes: 0, availableEstimateBytes: 400,
      deficitBytes: 630, marginBytes: 30, estimateIsLive: true, blockingVolumeId: 2,
    }));

    await cmp.runPreview();
    await fixture.whenStable();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Mancano');
    expect(text).toContain('di margine di sicurezza');
  });

  it('says in words, not with a symbol, that the figure is a last-known one', async () => {
    const { cmp, fixture, previewBatch } = setup();
    previewBatch.mockImplementationOnce(() => of({ ...feasibility, estimateIsLive: false }));

    await cmp.runPreview();
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('ultimo dato noto');
  });
});
