import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CatalogApi } from '../../core/api/catalog-api.service';
import { VolumesApi } from '../../core/api/volumes-api.service';
import { CatalogChildrenDto, VolumeDto } from '../../core/models/catalog.models';
import { CatalogStore } from './catalog.store';

const mockVolume: VolumeDto = {
  id: 1, volumeGuid: '\\\\?\\Volume{x}\\', label: 'SSD', currentLetter: 'D:',
  fileSystem: 'NTFS', isRemovable: false, isOnline: true, lastSeenUtc: '2026-01-01T00:00:00Z',
  capacityBytes: 500_000_000_000, freeBytes: 200_000_000_000, fileCount: 1000,
  lastFullScanUtc: '2026-01-01T00:00:00Z', dataIsLive: true, kind: 'Fixed', isCatalogable: true,
};

const rootChildren: CatalogChildrenDto = {
  directories: [
    { id: 10, name: 'Photos', materializedPath: 'Photos', childDirectoryCount: 2, fileCount: 50,
      projectedState: 'None', pendingJobId: null },
  ],
  files: { items: [], totalCount: 0, skip: 0, take: 50 },
  volumeIsOnline: true,
  volumeLabel: 'SSD',
  volumeLetter: 'D:',
  currentDirectoryId: null,
  currentDirectoryPath: null,
};

const photoChildren: CatalogChildrenDto = {
  directories: [],
  files: {
    items: [{
      id: 1, name: 'beach.jpg', sizeBytes: 2048, modifiedUtc: '2026-01-01T00:00:00Z',
      category: 'Image', projectedState: 'None', pendingJobId: null,
    }],
    totalCount: 1,
    skip: 0,
    take: 50,
  },
  volumeIsOnline: true,
  volumeLabel: 'SSD',
  volumeLetter: 'D:',
  currentDirectoryId: 10,
  currentDirectoryPath: 'Photos',
};

function setup(childrenFn: (volId: number, dirId: number | null) => CatalogChildrenDto = () => rootChildren) {
  const childrenSpy = vi.fn((volId: number, dirId: number | null) => of(childrenFn(volId, dirId)));
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: CatalogApi, useValue: { children: childrenSpy } },
      { provide: VolumesApi, useValue: { list: () => of([mockVolume]), detail: () => of(null), rescan: () => of(null), setCatalogable: () => of(null) } },
    ],
  });
  return { store: TestBed.inject(CatalogStore), childrenSpy };
}

describe('CatalogStore', () => {
  it('initial state is empty', () => {
    const { store } = setup();
    expect(store.selectedVolume()).toBeNull();
    expect(store.breadcrumbs()).toHaveLength(0);
    expect(store.children()).toBeNull();
  });

  it('selectVolume loads root children', async () => {
    const { store, childrenSpy } = setup();
    await store.selectVolume(mockVolume);

    expect(store.selectedVolume()?.id).toBe(1);
    expect(store.children()?.directories).toHaveLength(1);
    expect(store.breadcrumbs()).toHaveLength(0);
    expect(childrenSpy).toHaveBeenCalledWith(1, null, 0, 50);
  });

  it('openDirectory pushes breadcrumb and loads children', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);

    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');

    expect(store.breadcrumbs()).toHaveLength(1);
    expect(store.breadcrumbs()[0].name).toBe('Photos');
    expect(store.children()?.files.totalCount).toBe(1);
  });

  it('navigateTo(-1) goes back to root', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');
    await store.navigateTo(-1);

    expect(store.breadcrumbs()).toHaveLength(0);
    expect(store.children()?.directories).toHaveLength(1);
  });

  it('currentDirId reflects last breadcrumb', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    expect(store.currentDirId()).toBeNull();
    await store.openDirectory(10, 'Photos', 'Photos');
    expect(store.currentDirId()).toBe(10);
  });

  it('volumeIsOnline reflects children response', async () => {
    const { store } = setup();
    await store.selectVolume(mockVolume);
    expect(store.volumeIsOnline()).toBe(true);
  });

  it('error state populated on API failure', async () => {
    const errorSpy = vi.fn(() => throwError(() => new Error('API down')));
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: CatalogApi, useValue: { children: errorSpy } },
        { provide: VolumesApi, useValue: { list: () => of([mockVolume]), detail: () => of(null), rescan: () => of(null), setCatalogable: () => of(null) } },
      ],
    });
    const store = TestBed.inject(CatalogStore);
    await store.selectVolume(mockVolume);
    expect(store.error()).toBe('API down');
    expect(store.loading()).toBe(false);
  });

  it('clear resets to initial state', async () => {
    const { store } = setup();
    await store.selectVolume(mockVolume);
    store.clear();
    expect(store.selectedVolume()).toBeNull();
    expect(store.children()).toBeNull();
    expect(store.breadcrumbs()).toHaveLength(0);
  });

  // ── mixed file + folder selection ───────────────────────────────────────────

  const beachFile = photoChildren.files.items[0];
  const photosDir = rootChildren.directories[0]; // { id: 10, name: 'Photos', materializedPath: 'Photos' }

  it('toggleSelection adds the full file (kind File, path from current dir) to selectedItems', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');

    store.toggleSelection(beachFile);

    expect(store.hasSelection()).toBe(true);
    expect(store.selectionCount()).toBe(1);
    expect(store.selectedItems()).toEqual([
      { kind: 'File', id: 1, name: 'beach.jpg', sizeBytes: 2048, volumeId: 1, relativePath: 'Photos\\beach.jpg' },
    ]);
    expect(store.selectedKeys().has('File:1')).toBe(true);
  });

  it('toggleDirSelection adds the folder (kind Folder, 0 bytes, its own path)', async () => {
    const { store } = setup();
    await store.selectVolume(mockVolume);

    store.toggleDirSelection(photosDir);

    expect(store.selectionCount()).toBe(1);
    expect(store.folderSelectionCount()).toBe(1);
    expect(store.selectedItems()).toEqual([
      { kind: 'Folder', id: 10, name: 'Photos', sizeBytes: 0, volumeId: 1, relativePath: 'Photos' },
    ]);
    expect(store.selectedKeys().has('Folder:10')).toBe(true);
  });

  it('a file and a folder that share an id are selected independently (keyed by kind+id)', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    // Select folder id=10, then a file that also has id... beachFile is id=1; craft a clash:
    store.toggleDirSelection({
      id: 1, name: 'Clash', materializedPath: 'Clash', childDirectoryCount: 0, fileCount: 0,
      projectedState: 'None', pendingJobId: null,
    });
    await store.openDirectory(10, 'Photos', 'Photos');
    store.toggleSelection(beachFile); // file id=1

    expect(store.selectionCount()).toBe(2);
    expect(store.selectedKeys().has('Folder:1')).toBe(true);
    expect(store.selectedKeys().has('File:1')).toBe(true);
  });

  it('singleSelection is the one pick, null when zero or many', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    expect(store.singleSelection()).toBeNull();

    store.toggleDirSelection(photosDir);
    expect(store.singleSelection()?.kind).toBe('Folder');

    await store.openDirectory(10, 'Photos', 'Photos');
    store.toggleSelection(beachFile);
    expect(store.singleSelection()).toBeNull();
  });

  it('toggleSelection removes an already-selected file', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');

    store.toggleSelection(beachFile);
    store.toggleSelection(beachFile);

    expect(store.selectedKeys().has('File:1')).toBe(false);
    expect(store.hasSelection()).toBe(false);
  });

  it('clearSelection empties selectedItems', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');

    store.toggleSelection(beachFile);
    store.clearSelection();

    expect(store.selectedItems()).toHaveLength(0);
  });

  it('selectPage selects all files on current page', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');

    store.selectPage();

    expect(store.selectedKeys().has('File:1')).toBe(true);
    expect(store.allPageSelected()).toBe(true);
  });

  it('deselectPage removes page files', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');

    store.selectPage();
    store.deselectPage();

    expect(store.selectedKeys().has('File:1')).toBe(false);
  });

  it('selectVolume resets selectedItems', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');
    store.toggleSelection(beachFile);

    await store.selectVolume(mockVolume);

    expect(store.selectedItems()).toHaveLength(0);
  });

  // FIX #6 — the selection must survive navigation WITH its full data: files picked in
  // folder A must reach the picker even while folder B is the one on screen.
  it('selection made in one folder keeps full file data after navigating to another', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');

    store.toggleSelection(beachFile);
    await store.navigateTo(-1); // back to root: beach.jpg is no longer on the visible page

    expect(store.selectionCount()).toBe(1);
    expect(store.selectedItems()).toEqual([
      { kind: 'File', id: 1, name: 'beach.jpg', sizeBytes: 2048, volumeId: 1, relativePath: 'Photos\\beach.jpg' },
    ]);
  });

  it('deselectPage only removes files of the visible page, not cross-folder picks', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);
    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');
    store.toggleSelection(beachFile);
    await store.navigateTo(-1); // root has no files

    store.deselectPage();

    expect(store.selectionCount()).toBe(1);
  });
});
