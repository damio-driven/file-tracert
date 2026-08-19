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
 * How long a burst of queue transitions is collected before the cards are re-read. Enqueuing
 * fifty files raises fifty `JobStateChanged`, and a running job changes state several times on
 * its way through: one aggregate per second is invisible to the eye and bounds the cost.
 */
const QUEUE_REFRESH_COALESCE_MS = 1000;

/**
 * Dashboard aggregates, re-read on load, on reconnection, and after queue activity.
 */
export const DashboardStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withMethods((store, api = inject(DashboardApi)) => {
    async function load(): Promise<void> {
      patchState(store, { loading: true, error: null });
      try {
        const stats = await firstValueFrom(api.getStats());
        patchState(store, { stats, loading: false });
      } catch (e) {
        patchState(store, { error: httpErrorMessage(e), loading: false });
      }
    }

    let refreshHandle: ReturnType<typeof setTimeout> | null = null;

    return {
      load,

      /**
       * A queue transition moves three of the four cards. `JobStateChanged` carries a job id
       * and a state, not the job's bytes, so there is nothing to patch in memory: the choice
       * is a re-read or a card that quietly stays wrong, and a card that is wrong is worse
       * than a request. Coalesced, and skipped entirely while the store holds no stats — the
       * first load either has not happened yet or failed, and hammering a service that is
       * down once per transition helps nobody.
       */
      scheduleRefresh(): void {
        if (store.stats() === null || refreshHandle !== null) {
          return;
        }
        refreshHandle = setTimeout(() => {
          refreshHandle = null;
          void load();
        }, QUEUE_REFRESH_COALESCE_MS);
      },
    };
  }),
);
