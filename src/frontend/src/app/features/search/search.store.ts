import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { SearchApi } from '../../core/api/search-api.service';
import {
  FileCategory, PagedResult, SearchRequest,
  SearchResultDto, SearchScope, SearchSort,
} from '../../core/models/catalog.models';

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
  selectedFileIds: number[];
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
  selectedFileIds: [],
};

export const SearchStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withComputed((store) => ({
    hasResults: computed(() => (store.results()?.totalCount ?? 0) > 0),
    totalCount: computed(() => store.results()?.totalCount ?? 0),
    isCapped: computed(() => store.results()?.totalCount === 10000),
    selectionCount: computed(() => store.selectedFileIds().length),
    hasSelection: computed(() => store.selectedFileIds().length > 0),
    allPageSelected: computed(() => {
      const items = store.results()?.items ?? [];
      if (items.length === 0) return false;
      const sel = store.selectedFileIds();
      return items.every(f => sel.includes(f.fileId));
    }),
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

    async function doSearch(skip: number): Promise<void> {
      const text = store.text().trim();
      if (!text) return;
      patchState(store, { loading: true, error: null, currentSkip: skip });
      try {
        const results = await firstValueFrom(api.search(buildRequest(skip)));
        patchState(store, { results, loading: false });
      } catch (e) {
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
      clear(): void {
        patchState(store, { text: '', results: null, error: null, currentSkip: 0, selectedFileIds: [] });
      },
      toggleSelection(fileId: number): void {
        const ids = store.selectedFileIds();
        patchState(store, {
          selectedFileIds: ids.includes(fileId) ? ids.filter(x => x !== fileId) : [...ids, fileId],
        });
      },
      selectPage(): void {
        const items = store.results()?.items ?? [];
        const existing = store.selectedFileIds();
        const pageIds = items.map(f => f.fileId);
        const merged = [...new Set([...existing, ...pageIds])];
        patchState(store, { selectedFileIds: merged });
      },
      deselectPage(): void {
        const pageIds = new Set((store.results()?.items ?? []).map(f => f.fileId));
        patchState(store, { selectedFileIds: store.selectedFileIds().filter(id => !pageIds.has(id)) });
      },
      clearSelection(): void {
        patchState(store, { selectedFileIds: [] });
      },
    };
  }),
);
