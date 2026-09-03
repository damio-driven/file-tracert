import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { SetupApi } from '../../core/api/setup-api.service';
import { VolumesApi } from '../../core/api/volumes-api.service';
import { FolderNodeDto, PagedResult, VolumeDetailDto, WatchedRootDto } from '../../core/models/catalog.models';
import { SetupStore } from './setup.store';

const fotoRoot: WatchedRootDto = { id: 7, relativePath: 'Foto', isActive: true, effectiveFilter: 'Immagini' };
const topFolders: PagedResult<FolderNodeDto> = {
  items: [{ name: 'Foto', relativePath: 'Foto', hasChildren: true }], totalCount: 1, skip: 0, take: 50,
};

function configure(api: Partial<SetupApi>, volumesApi: Partial<VolumesApi> = {}) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: SetupApi, useValue: api },
      { provide: VolumesApi, useValue: volumesApi },
    ],
  });
  return TestBed.inject(SetupStore);
}

describe('SetupStore', () => {
  it('caches folders per path on lazy browse', async () => {
    const browse = vi.fn(() => of(topFolders));
    const store = configure({ browse });
    store.init(1);

    await store.loadFolders('');
    await store.loadFolders('');

    expect(browse).toHaveBeenCalledTimes(1);
    expect(store.foldersAt('')).toEqual(topFolders.items);
    expect(store.remainingFoldersAt('')).toBe(0);
  });

  it('adds a root and keeps it in state', async () => {
    const store = configure({ browse: () => of(topFolders), createRoot: () => of(fotoRoot) });
    store.init(1);

    await store.addRoot('Foto');

    expect(store.roots()).toContainEqual(fotoRoot);
  });

  it('loads existing roots from the volume detail', async () => {
    const detail = {
      id: 1, watchedRoots: [{ id: 7, relativePath: 'Foto', isActive: true, effectiveFilter: 'Immagini' }],
    } as never as VolumeDetailDto;
    const store = configure({}, { detail: () => of(detail) });
    store.init(1);

    await store.loadRoots();

    expect(store.roots()).toHaveLength(1);
    expect(store.roots()[0].relativePath).toBe('Foto');
  });

  it('surfaces the reconcile flag when saving the filter', async () => {
    const store = configure({
      getFilter: () => of({ allowedExtensions: ['jpg'], excludedPaths: [] }),
      putFilter: () => of({ includedCount: 1, excludedCount: 0, needsScan: true }),
    });
    store.init(1);

    await store.saveFilter({ allowedExtensions: ['jpg', 'png'], excludedPaths: [] });

    expect(store.lastReconcile()?.needsScan).toBe(true);
  });

  it('surfaces the reconcile outcome of switching a root back on', async () => {
    const store = configure({
      updateRoot: () => of({
        root: fotoRoot,
        reconcile: { includedCount: 12, excludedCount: 3, needsScan: true },
      }),
    });
    store.init(1);

    await store.toggleRoot(7, true);

    // Switching a folder on re-includes its rows with no rescan, and leaves behind whatever
    // was never indexed while it was off — both facts belong on screen.
    expect(store.lastReconcile()?.includedCount).toBe(12);
    expect(store.lastReconcile()?.needsScan).toBe(true);
  });
});

describe('SetupStore folder paging (step 17)', () => {
  function folder(i: number): FolderNodeDto {
    return { name: `f${String(i).padStart(3, '0')}`, relativePath: `f${i}`, hasChildren: false };
  }

  it('loadMoreFolders appends the next page of one level and leaves the others alone', async () => {
    const browse = vi.fn((_v: number, path: string, skip = 0) => of({
      items: path === '' ? Array.from({ length: Math.min(50, 120 - skip) }, (_, i) => folder(skip + i)) : [folder(900)],
      totalCount: path === '' ? 120 : 1, skip, take: 50,
    }));
    const store = configure({ browse });
    store.init(1);
    await store.loadFolders('');
    await store.loadFolders('Foto');

    expect(store.foldersAt('')).toHaveLength(50);
    expect(store.remainingFoldersAt('')).toBe(70);

    await store.loadMoreFolders('');

    expect(browse).toHaveBeenLastCalledWith(1, '', 50);
    expect(store.foldersAt('')).toHaveLength(100);
    expect(store.foldersAt('')[0].name).toBe('f000');
    expect(store.foldersAt('')[99].name).toBe('f099');
    expect(store.remainingFoldersAt('')).toBe(20);
    expect(store.isBrowsingMore('')).toBe(false);
    expect(store.foldersAt('Foto')).toHaveLength(1);
  });
});
