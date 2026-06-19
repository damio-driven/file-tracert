import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { CatalogApi } from '../../core/api/catalog-api.service';
import { CatalogChildrenDto, VolumeDto } from '../../core/models/catalog.models';

interface Breadcrumb {
  id: number;
  name: string;
  path: string;
}

interface CatalogState {
  selectedVolume: VolumeDto | null;
  breadcrumbs: Breadcrumb[];
  children: CatalogChildrenDto | null;
  loading: boolean;
  error: string | null;
  fileSkip: number;
  fileTake: number;
  selectedFileIds: number[];
}

const initial: CatalogState = {
  selectedVolume: null,
  breadcrumbs: [],
  children: null,
  loading: false,
  error: null,
  fileSkip: 0,
  fileTake: 50,
  selectedFileIds: [],
};

export const CatalogStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withComputed((store) => ({
    currentDirId: computed(() => {
      const crumbs = store.breadcrumbs();
      return crumbs.length > 0 ? crumbs[crumbs.length - 1].id : null;
    }),
    volumeIsOnline: computed(() => store.children()?.volumeIsOnline ?? false),
    totalFiles: computed(() => store.children()?.files.totalCount ?? 0),
    canGoUp: computed(() => store.breadcrumbs().length > 0),
    selectionCount: computed(() => store.selectedFileIds().length),
    hasSelection: computed(() => store.selectedFileIds().length > 0),
    allPageSelected: computed(() => {
      const items = store.children()?.files.items ?? [];
      if (items.length === 0) return false;
      const sel = store.selectedFileIds();
      return items.every(f => sel.includes(f.id));
    }),
  })),
  withMethods((store, api = inject(CatalogApi)) => {
    async function loadChildren(dirId: number | null, fileSkip: number): Promise<void> {
      const vol = store.selectedVolume();
      if (!vol) return;
      patchState(store, { loading: true, error: null });
      try {
        const children = await firstValueFrom(api.children(vol.id, dirId, fileSkip, store.fileTake()));
        patchState(store, { children, fileSkip, loading: false });
      } catch (e) {
        patchState(store, { error: (e as Error).message, loading: false });
      }
    }

    return {
      async selectVolume(volume: VolumeDto): Promise<void> {
        patchState(store, {
          selectedVolume: volume,
          breadcrumbs: [],
          children: null,
          fileSkip: 0,
          selectedFileIds: [],
        });
        await loadChildren(null, 0);
      },

      async openDirectory(dirId: number, name: string, path: string): Promise<void> {
        const crumbs = [...store.breadcrumbs(), { id: dirId, name, path }];
        patchState(store, { breadcrumbs: crumbs, fileSkip: 0 });
        await loadChildren(dirId, 0);
      },

      async navigateTo(index: number): Promise<void> {
        if (index < 0) {
          patchState(store, { breadcrumbs: [], fileSkip: 0 });
          await loadChildren(null, 0);
        } else {
          const crumbs = store.breadcrumbs().slice(0, index + 1);
          const dirId = crumbs[crumbs.length - 1]?.id ?? null;
          patchState(store, { breadcrumbs: crumbs, fileSkip: 0 });
          await loadChildren(dirId, 0);
        }
      },

      async loadFilePage(skip: number): Promise<void> {
        await loadChildren(store.currentDirId(), skip);
      },

      clear(): void {
        patchState(store, { ...initial });
      },
      toggleSelection(fileId: number): void {
        const ids = store.selectedFileIds();
        patchState(store, {
          selectedFileIds: ids.includes(fileId) ? ids.filter(x => x !== fileId) : [...ids, fileId],
        });
      },
      selectPage(): void {
        const items = store.children()?.files.items ?? [];
        const existing = store.selectedFileIds();
        const pageIds = items.map(f => f.id);
        patchState(store, { selectedFileIds: [...new Set([...existing, ...pageIds])] });
      },
      deselectPage(): void {
        const pageIds = new Set((store.children()?.files.items ?? []).map(f => f.id));
        patchState(store, { selectedFileIds: store.selectedFileIds().filter(id => !pageIds.has(id)) });
      },
      clearSelection(): void {
        patchState(store, { selectedFileIds: [] });
      },
    };
  }),
);
