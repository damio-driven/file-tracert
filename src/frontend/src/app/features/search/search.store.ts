import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { SearchApi } from '../../core/api/search-api.service';
import {
  FileCategory, PagedResult, SearchRequest,
  SearchResultDto, SearchScope, SearchSort, SelectedItem,
} from '../../core/models/catalog.models';
import {
  addPageToSelection, isPageFullySelected, removePageFromSelection, toggleSelected,
} from '../../shared/selection/file-selection.util';

interface SearchFilters {
  category: FileCategory | null;
  extensions: string[] | null;
  sizeBytesMin: number | null;
  sizeBytesMax: number | null;
  modifiedFrom: string | null;
  modifiedTo: string | null;
  volumeId: number | null;
  onlineOnly: boolean;
}

interface SearchState {
  text: string;
  scope: SearchScope;
  sort: SearchSort;
  desc: boolean;
  filters: SearchFilters;
  results: PagedResult<SearchResultDto> | null;
  loading: boolean;
  error: string | null;
  currentSkip: number;
  take: number;
  /**
   * Full SelectedItem objects, not bare ids: the selection survives paging and the
   * picker always has name/size/volume for every picked file, even when it is no
   * longer on the visible page (fix #6). Search selects files only (kind = 'File').
   */
  selectedItems: SelectedItem[];
}

const defaultFilters: SearchFilters = {
  category: null,
  extensions: null,
  sizeBytesMin: null,
  sizeBytesMax: null,
  modifiedFrom: null,
  modifiedTo: null,
  volumeId: null,
  onlineOnly: false,
};

const initial: SearchState = {
  text: '',
  scope: 'Name',
  sort: 'Relevance',
  desc: false,
  filters: defaultFilters,
  results: null,
  loading: false,
  error: null,
  currentSkip: 0,
  take: 50,
  selectedItems: [],
};

function toSelectedItem(result: SearchResultDto): SelectedItem {
  return {
    kind: 'File',
    id: result.fileId,
    name: result.name,
    sizeBytes: result.sizeBytes,
    volumeId: result.volumeId,
    relativePath: result.relativePath,
  };
}

export const SearchStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withComputed((store) => ({
    hasResults: computed(() => (store.results()?.totalCount ?? 0) > 0),
    totalCount: computed(() => store.results()?.totalCount ?? 0),
    isCapped: computed(() => store.results()?.totalCount === 10000),
    selectedFileIds: computed(() => store.selectedItems().map(i => i.id)),
    selectionCount: computed(() => store.selectedItems().length),
    hasSelection: computed(() => store.selectedItems().length > 0),
    allPageSelected: computed(() =>
      isPageFullySelected(
        (store.results()?.items ?? []).map(f => `File:${f.fileId}`), store.selectedItems())),
  })),
  withMethods((store, api = inject(SearchApi)) => {
    function buildRequest(skip: number): SearchRequest {
      const s = store;
      return {
        text: s.text(),
        scope: s.scope(),
        sort: s.sort(),
        desc: s.desc(),
        skip,
        take: s.take(),
        ...s.filters(),
      };
    }

    // Latest wins: filters re-run the search on every change, and an earlier query over a
    // wider result set can land after a later, narrower one. Without this the grid would show
    // results that contradict the filters on screen.
    let latestRequest = 0;

    async function doSearch(skip: number): Promise<void> {
      const text = store.text().trim();
      if (!text) return;
      const request = ++latestRequest;
      patchState(store, { loading: true, error: null, currentSkip: skip });
      try {
        const results = await firstValueFrom(api.search(buildRequest(skip)));
        if (request !== latestRequest) return;
        patchState(store, { results, loading: false });
      } catch (e) {
        if (request !== latestRequest) return;
        patchState(store, { error: (e as Error).message, loading: false });
      }
    }

    return {
      setQuery(text: string): void {
        patchState(store, { text });
      },
      setScope(scope: SearchScope): void {
        patchState(store, { scope });
      },
      setSort(sort: SearchSort, desc = false): void {
        patchState(store, { sort, desc });
      },
      setFilters(filters: Partial<SearchFilters>): void {
        patchState(store, { filters: { ...store.filters(), ...filters } });
      },
      clearFilters(): void {
        patchState(store, { filters: defaultFilters });
      },
      async search(): Promise<void> {
        await doSearch(0);
      },
      async loadPage(skip: number): Promise<void> {
        await doSearch(skip);
      },
      /**
       * `ProjectionChanged` push (§5). Re-runs the current page of the current query so the
       * projected name/path and the badge follow the queue. Volume is not filtered on here:
       * a search spans every volume, so any overlay change can touch the visible rows.
       */
      invalidate(): void {
        if (store.results() === null) return;
        void doSearch(store.currentSkip());
      },

      clear(): void {
        patchState(store, { text: '', results: null, error: null, currentSkip: 0, selectedItems: [] });
      },
      toggleSelection(result: SearchResultDto): void {
        patchState(store, {
          selectedItems: toggleSelected(store.selectedItems(), toSelectedItem(result)),
        });
      },
      selectPage(): void {
        const pageFiles = (store.results()?.items ?? []).map(toSelectedItem);
        patchState(store, { selectedItems: addPageToSelection(store.selectedItems(), pageFiles) });
      },
      deselectPage(): void {
        const pageKeys = (store.results()?.items ?? []).map(f => `File:${f.fileId}`);
        patchState(store, { selectedItems: removePageFromSelection(store.selectedItems(), pageKeys) });
      },
      clearSelection(): void {
        patchState(store, { selectedItems: [] });
      },
    };
  }),
);
