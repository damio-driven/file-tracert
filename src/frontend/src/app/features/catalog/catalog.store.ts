import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { CatalogApi } from '../../core/api/catalog-api.service';
import {
  CatalogChildrenDto, CatalogDirDto, CatalogFileDto, SelectedItem, VolumeDto,
} from '../../core/models/catalog.models';
import {
  addPageToSelection, isPageFullySelected, removePageFromSelection, selectionKey, toggleSelected,
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
   * Full SelectedItem objects (files AND folders), not bare ids: the selection
   * survives folder/page navigation and the picker always has name/size/volume/path
   * for every pick, even when it is no longer on the visible page (fix #6).
   */
  selectedItems: SelectedItem[];
}

const initial: CatalogState = {
  selectedVolume: null,
  breadcrumbs: [],
  children: null,
  loading: false,
  error: null,
  fileSkip: 0,
  fileTake: 50,
  selectedItems: [],
};

function toFileItem(file: CatalogFileDto, volumeId: number, dirPath: string): SelectedItem {
  return {
    kind: 'File', id: file.id, name: file.name, sizeBytes: file.sizeBytes, volumeId,
    relativePath: dirPath ? `${dirPath}\\${file.name}` : file.name,
  };
}

function toFolderItem(dir: CatalogDirDto, volumeId: number): SelectedItem {
  // Folders carry 0 bytes here — the subtree weight is computed server-side at preview.
  return {
    kind: 'Folder', id: dir.id, name: dir.name, sizeBytes: 0, volumeId,
    relativePath: dir.materializedPath,
  };
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
    currentDirPath: computed(() => store.children()?.currentDirectoryPath ?? ''),
    /** Keys of every selected item (File:id / Folder:id) for O(1) row-state lookup. */
    selectedKeys: computed(() => new Set(store.selectedItems().map(selectionKey))),
    selectionCount: computed(() => store.selectedItems().length),
    hasSelection: computed(() => store.selectedItems().length > 0),
    folderSelectionCount: computed(() =>
      store.selectedItems().filter(i => i.kind === 'Folder').length),
    /** The one pick when exactly one item is selected (rename targets a single entity). */
    singleSelection: computed(() =>
      store.selectedItems().length === 1 ? store.selectedItems()[0] : null),
    allPageSelected: computed(() =>
      isPageFullySelected(
        (store.children()?.files.items ?? []).map(f => `File:${f.id}`),
        store.selectedItems())),
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
          selectedItems: [],
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

      /**
       * `ProjectionChanged` push (§5): the `Pending*` overlay moved, so the badges and the
       * projected names/positions on screen are stale. A null `volumeId` means the change
       * spanned more than one volume, so it always applies. Nothing is reloaded when the
       * screen has no folder open — there is no view to invalidate.
       */
      invalidate(volumeId: number | null): void {
        const vol = store.selectedVolume();
        if (!vol || store.children() === null) return;
        if (volumeId !== null && volumeId !== vol.id) return;
        void loadChildren(store.currentDirId(), store.fileSkip());
      },

      clear(): void {
        patchState(store, { ...initial });
      },
      toggleSelection(file: CatalogFileDto): void {
        const vol = store.selectedVolume();
        if (!vol) return;
        patchState(store, {
          selectedItems: toggleSelected(
            store.selectedItems(), toFileItem(file, vol.id, store.currentDirPath())),
        });
      },
      toggleDirSelection(dir: CatalogDirDto): void {
        const vol = store.selectedVolume();
        if (!vol) return;
        patchState(store, {
          selectedItems: toggleSelected(store.selectedItems(), toFolderItem(dir, vol.id)),
        });
      },
      selectPage(): void {
        const vol = store.selectedVolume();
        if (!vol) return;
        const dirPath = store.currentDirPath();
        const pageFiles = (store.children()?.files.items ?? []).map(f => toFileItem(f, vol.id, dirPath));
        patchState(store, { selectedItems: addPageToSelection(store.selectedItems(), pageFiles) });
      },
      deselectPage(): void {
        const pageKeys = (store.children()?.files.items ?? []).map(f => `File:${f.id}`);
        patchState(store, { selectedItems: removePageFromSelection(store.selectedItems(), pageKeys) });
      },
      clearSelection(): void {
        patchState(store, { selectedItems: [] });
      },
    };
  }),
);
