import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
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
