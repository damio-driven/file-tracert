import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { CatalogApi } from '../../../core/api/catalog-api.service';
import { QueueApi } from '../../../core/api/queue-api.service';
import { VolumesApi } from '../../../core/api/volumes-api.service';
import {
  CatalogChildrenDto, CatalogDirDto, CreateJobRequest, FeasibilityResult, SelectedItem,
} from '../../../core/models/catalog.models';
import { OperationPicker } from './operation-picker';

const items: SelectedItem[] = [
  { kind: 'File', id: 1, name: 'photo.jpg', sizeBytes: 1000, volumeId: 1, relativePath: 'photo.jpg' },
  { kind: 'File', id: 2, name: 'clip.mp4', sizeBytes: 2000, volumeId: 1, relativePath: 'clip.mp4' },
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
  const previewBatch = vi.fn((_reqs: CreateJobRequest[]) => of(feasibility));
  const children = vi.fn((_volumeId: number, dirId: number | null) => {
    if (dirId === null) return of(childrenResult([dir(10, 'Documenti'), dir(11, 'Archivio')]));
    if (dirId === 10) return of(childrenResult([dir(20, 'Foto')]));
    return of(childrenResult([]));
  });

  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: QueueApi, useValue: { enqueue, previewBatch } },
      { provide: CatalogApi, useValue: { children } },
      { provide: VolumesApi, useValue: { list: () => of([]) } },
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

  return { enqueue, previewBatch, children, cmp, fixture };
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
    const { enqueue, cmp, fixture } = setup();
    fixture.componentRef.setInput('items', [
      { kind: 'Folder', id: 7, name: 'Vacanze', sizeBytes: 0, volumeId: 1, relativePath: 'Vacanze' },
    ] satisfies SelectedItem[]);

    await cmp.openDirectory(dir(11, 'Archivio'));
    await cmp.enqueue();

    expect(enqueue).toHaveBeenCalledTimes(1);
    const req = enqueue.mock.calls[0][0];
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

  it('a failed enqueue does not emit completed', async () => {
    const { cmp, fixture, enqueue } = setup();
    enqueue.mockImplementationOnce(() => throwError(() => new Error('boom')));

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
