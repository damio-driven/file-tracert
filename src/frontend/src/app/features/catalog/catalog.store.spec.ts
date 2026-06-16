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
  lastFullScanUtc: '2026-01-01T00:00:00Z', dataIsLive: true, isStale: false,
  kind: 'Fixed', isCatalogable: true,
};

const rootChildren: CatalogChildrenDto = {
  directories: [
    { id: 10, name: 'Photos', materializedPath: 'Photos', childDirectoryCount: 2, fileCount: 50 },
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
    items: [{ id: 1, name: 'beach.jpg', sizeBytes: 2048, modifiedUtc: '2026-01-01T00:00:00Z', category: 'Image', projectedState: 'None' }],
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
});
