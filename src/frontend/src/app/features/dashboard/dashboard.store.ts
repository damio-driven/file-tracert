import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { DashboardApi } from '../../core/api/dashboard-api.service';
import { httpErrorMessage } from '../../core/http/http-error';
import { DashboardStatsDto } from '../../core/models/catalog.models';

interface DashboardState {
  stats: DashboardStatsDto | null;
  loading: boolean;
  error: string | null;
}

const initial: DashboardState = { stats: null, loading: false, error: null };

/**
 * Dashboard aggregates. Fetch on load + manual refresh today; at step 10 the
 * SignalR client will patch this same store and the UI reacts unchanged.
 */
export const DashboardStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withMethods((store, api = inject(DashboardApi)) => ({
    async load(): Promise<void> {
      patchState(store, { loading: true, error: null });
      try {
        const stats = await firstValueFrom(api.getStats());
        patchState(store, { stats, loading: false });
      } catch (e) {
        patchState(store, { error: httpErrorMessage(e), loading: false });
      }
    },
  })),
);
