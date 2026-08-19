import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { LogsApi } from '../../core/api/logs-api.service';
import { httpErrorMessage } from '../../core/http/http-error';
import { LogEntryDto } from '../../core/models/catalog.models';

const PAGE_SIZE = 50;

interface LogsState {
  entries: LogEntryDto[];
  totalCount: number;
  skip: number;
  take: number;
  level: string | null; // minimum-level filter for the view
  category: string;
  search: string;
  currentLevel: string; // runtime logging level (the switch)
  loading: boolean;
  error: string | null;
}

const initial: LogsState = {
  entries: [],
  totalCount: 0,
  skip: 0,
  take: PAGE_SIZE,
  level: null,
  category: '',
  search: '',
  currentLevel: 'Information',
  loading: false,
  error: null,
};

/**
 * Backs the Log section: a server-paged, filterable view of the dedicated log DB
 * plus runtime control of the minimum logging level (persisted server-side).
 */
export const LogsStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withComputed((store) => ({
    page: computed(() => Math.floor(store.skip() / store.take()) + 1),
    pageCount: computed(() => Math.max(1, Math.ceil(store.totalCount() / store.take()))),
    hasPrev: computed(() => store.skip() > 0),
    hasNext: computed(() => store.skip() + store.take() < store.totalCount()),
  })),
  withMethods((store, api = inject(LogsApi)) => {
    async function load(): Promise<void> {
      patchState(store, { loading: true, error: null });
      try {
        const result = await firstValueFrom(
          api.getLogs({
            skip: store.skip(),
            take: store.take(),
            level: store.level(),
            category: store.category(),
            search: store.search(),
          }),
        );
        patchState(store, {
          entries: result.items,
          totalCount: result.totalCount,
          loading: false,
        });
      } catch (e) {
        patchState(store, { error: httpErrorMessage(e), loading: false });
      }
    }

    return {
      load,

      async init(): Promise<void> {
        try {
          const { level } = await firstValueFrom(api.getLevel());
          patchState(store, { currentLevel: level });
        } catch {
          // Non-fatal: keep the default until the user changes it.
        }
        await load();
      },

      async applyFilters(filters: { level?: string | null; category?: string; search?: string }): Promise<void> {
        patchState(store, { ...filters, skip: 0 });
        await load();
      },

      async nextPage(): Promise<void> {
        if (store.skip() + store.take() >= store.totalCount()) {
          return;
        }
        patchState(store, { skip: store.skip() + store.take() });
        await load();
      },

      async prevPage(): Promise<void> {
        if (store.skip() === 0) {
          return;
        }
        patchState(store, { skip: Math.max(0, store.skip() - store.take()) });
        await load();
      },

      async changeLevel(level: string): Promise<void> {
        const { level: applied } = await firstValueFrom(api.setLevel(level));
        patchState(store, { currentLevel: applied });
      },
    };
  }),
);
