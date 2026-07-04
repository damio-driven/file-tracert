import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { CatalogApi } from '../../core/api/catalog-api.service';
import { CatalogChildrenDto, CatalogFileDto, SelectedFile, VolumeDto } from '../../core/models/catalog.models';
import {
  addPageToSelection, isPageFullySelected, removePageFromSelection, toggleSelected,
} from '../../shared/selection/file-selection.util';

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
  /**
   * Full SelectedFile objects, not bare ids: the selection survives folder/page
   * navigation and the picker always has name/size/volume for every picked file,
   * even when it is no longer on the visible page (fix #6).
   */
  selectedFiles: SelectedFile[];
}

const initial: CatalogState = {
  selectedVolume: null,
  breadcrumbs: [],
  children: null,
  loading: false,
  error: null,
  fileSkip: 0,
  fileTake: 50,
  selectedFiles: [],
};

function toSelectedFile(file: CatalogFileDto, volumeId: number): SelectedFile {
  return { fileId: file.id, name: file.name, sizeBytes: file.sizeBytes, volumeId };
}

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
    selectedFileIds: computed(() => store.selectedFiles().map(f => f.fileId)),
    selectionCount: computed(() => store.selectedFiles().length),
    hasSelection: computed(() => store.selectedFiles().length > 0),
    allPageSelected: computed(() =>
      isPageFullySelected((store.children()?.files.items ?? []).map(f => f.id), store.selectedFiles())),
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
          selectedFiles: [],
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
      toggleSelection(file: CatalogFileDto): void {
        const vol = store.selectedVolume();
        if (!vol) return;
        patchState(store, {
          selectedFiles: toggleSelected(store.selectedFiles(), toSelectedFile(file, vol.id)),
        });
      },
      selectPage(): void {
        const vol = store.selectedVolume();
        if (!vol) return;
        const pageFiles = (store.children()?.files.items ?? []).map(f => toSelectedFile(f, vol.id));
        patchState(store, { selectedFiles: addPageToSelection(store.selectedFiles(), pageFiles) });
      },
      deselectPage(): void {
        const pageIds = (store.children()?.files.items ?? []).map(f => f.id);
        patchState(store, { selectedFiles: removePageFromSelection(store.selectedFiles(), pageIds) });
      },
      clearSelection(): void {
        patchState(store, { selectedFiles: [] });
      },
    };
  }),
);
