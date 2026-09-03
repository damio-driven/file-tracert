import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CatalogApi } from '../../core/api/catalog-api.service';
import { VolumesApi } from '../../core/api/volumes-api.service';
import { CatalogChildrenDto, CatalogDirDto, VolumeDto } from '../../core/models/catalog.models';
import { CatalogStore } from './catalog.store';

const mockVolume: VolumeDto = {
  id: 1, volumeGuid: '\\\\?\\Volume{x}\\', label: 'SSD', currentLetter: 'D:',
  fileSystem: 'NTFS', isRemovable: false, isOnline: true, lastSeenUtc: '2026-01-01T00:00:00Z',
  capacityBytes: 500_000_000_000, freeBytes: 200_000_000_000, fileCount: 1000,
  lastFullScanUtc: '2026-01-01T00:00:00Z', dataIsLive: true, kind: 'Fixed', isCatalogable: true,
};

const rootChildren: CatalogChildrenDto = {
  directories: {
    items: [
      { id: 10, name: 'Photos', materializedPath: 'Photos', childDirectoryCount: 2, fileCount: 50,
        projectedState: 'None', pendingJobId: null },
    ],
    totalCount: 1, skip: 0, take: 50,
  },
  files: { items: [], totalCount: 0, skip: 0, take: 50 },
  volumeIsOnline: true,
  volumeLabel: 'SSD',
  volumeLetter: 'D:',
  currentDirectoryId: null,
  currentDirectoryPath: null,
};

const photoChildren: CatalogChildrenDto = {
  directories: { items: [], totalCount: 0, skip: 0, take: 50 },
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
    expect(store.children()?.directories.items).toHaveLength(1);
    expect(store.breadcrumbs()).toHaveLength(0);
    expect(childrenSpy).toHaveBeenCalledWith(1, null, 0, 50, 0, 50);
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
    expect(store.children()?.directories.items).toHaveLength(1);
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
  const photosDir = rootChildren.directories.items[0]; // { id: 10, name: 'Photos', materializedPath: 'Photos' }

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

// ── Step 17: subfolders are paged on their own axis and APPENDED on screen ─────────────────────

describe('CatalogStore subfolder paging (step 17)', () => {
  function dirsFrom(from: number, count: number): CatalogDirDto[] {
    return Array.from({ length: count }, (_, i) => ({
      id: from + i, name: `d${String(from + i).padStart(3, '0')}`, materializedPath: `d${from + i}`,
      childDirectoryCount: 0, fileCount: 0, projectedState: 'None' as const, pendingJobId: null,
    }));
  }

  function paged(items: CatalogDirDto[], totalCount: number, dirId: number | null): CatalogChildrenDto {
    return {
      directories: { items, totalCount, skip: 0, take: items.length },
      files: { items: [], totalCount: 0, skip: 0, take: 50 },
      volumeIsOnline: true, volumeLabel: 'SSD', volumeLetter: 'D:',
      currentDirectoryId: dirId, currentDirectoryPath: dirId === null ? null : `d${dirId}`,
    };
  }

  /** A root with `total` subfolders; the spy answers exactly the slice the store asked for. */
  function setupWide(total: number, late?: Subject<CatalogChildrenDto>) {
    const childrenSpy = vi.fn(
      (_v: number, dirId: number | null, _skip: number, _take: number, dirSkip: number, dirTake: number) => {
        if (late && dirSkip > 0) return late.asObservable();
        if (dirId !== null) return of(paged(dirsFrom(0, 3), 3, dirId));
        return of(paged(dirsFrom(dirSkip, Math.max(0, Math.min(dirTake, total - dirSkip))), total, null));
      });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: CatalogApi, useValue: { children: childrenSpy } },
        { provide: VolumesApi, useValue: { list: () => of([mockVolume]), detail: () => of(null), rescan: () => of(null), setCatalogable: () => of(null) } },
      ],
    });
    return { store: TestBed.inject(CatalogStore), childrenSpy };
  }

  it('the first page shows 50 of 120 and says how many are still unlisted', async () => {
    const { store } = setupWide(120);
    await store.selectVolume(mockVolume);

    expect(store.children()?.directories.items).toHaveLength(50);
    expect(store.hasMoreDirectories()).toBe(true);
    expect(store.remainingDirectories()).toBe(70);
    expect(store.nextDirBatch()).toBe(50);
  });

  it('loadMoreDirectories appends the next page after the folders already shown', async () => {
    const { store, childrenSpy } = setupWide(120);
    await store.selectVolume(mockVolume);

    await store.loadMoreDirectories();
    expect(childrenSpy).toHaveBeenLastCalledWith(1, null, 0, 50, 50, 50);
    const names = store.children()!.directories.items.map(d => d.name);
    expect(names).toHaveLength(100);
    expect(names[0]).toBe('d000');
    expect(names[99]).toBe('d099');
    expect(store.nextDirBatch()).toBe(20);

    await store.loadMoreDirectories();
    expect(store.children()?.directories.items).toHaveLength(120);
    expect(store.hasMoreDirectories()).toBe(false);
    expect(store.loadingMoreDirs()).toBe(false);
  });

  it('a projection push re-reads the folders already on screen, not just the first page', async () => {
    const { store, childrenSpy } = setupWide(120);
    await store.selectVolume(mockVolume);
    await store.loadMoreDirectories();

    store.invalidate(1);
    await new Promise(resolve => setTimeout(resolve));

    expect(childrenSpy).toHaveBeenLastCalledWith(1, null, 0, 50, 0, 100);
    expect(store.children()?.directories.items).toHaveLength(100);
  });

  it('turning a file page keeps the subfolders already shown', async () => {
    const { store, childrenSpy } = setupWide(120);
    await store.selectVolume(mockVolume);
    await store.loadMoreDirectories();

    await store.loadFilePage(50);

    expect(childrenSpy).toHaveBeenLastCalledWith(1, null, 50, 50, 0, 100);
  });

  it('a page that lands after the user opened another folder is dropped (last request wins)', async () => {
    const late = new Subject<CatalogChildrenDto>();
    const { store } = setupWide(120, late);
    await store.selectVolume(mockVolume);

    const pending = store.loadMoreDirectories();
    await store.openDirectory(7, 'd7', 'd7');
    late.next(paged(dirsFrom(50, 50), 120, null));
    late.complete();
    await pending;

    expect(store.currentDirId()).toBe(7);
    expect(store.children()?.directories.items).toHaveLength(3);
    expect(store.loadingMoreDirs()).toBe(false);
  });
});
