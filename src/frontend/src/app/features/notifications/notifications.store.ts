import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { NotificationsApi } from '../../core/api/notifications-api.service';
import { NotificationDto } from '../../core/models/catalog.models';

interface NotificationsState {
  items: NotificationDto[];
  unread: number;
  loading: boolean;
  open: boolean;
  error: string | null;
}

const initial: NotificationsState = {
  items: [],
  unread: 0,
  loading: false,
  open: false,
  error: null,
};

/**
 * Notifications for the title-bar bell. The badge count and the panel list are
 * fetched on demand and after each mutation. At step 10 the SignalR client will
 * push into this same store and the bell reacts unchanged.
 */
export const NotificationsStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withComputed((store) => ({
    hasUnread: computed(() => store.unread() > 0),
  })),
  withMethods((store, api = inject(NotificationsApi)) => {
    async function refreshCount(): Promise<void> {
      try {
        const { unread } = await firstValueFrom(api.unreadCount());
        patchState(store, { unread });
      } catch {
        // The badge is best-effort; a failed count must not surface an error toast.
      }
    }

    async function loadList(): Promise<void> {
      patchState(store, { loading: true, error: null });
      try {
        const [page, count] = await Promise.all([
          firstValueFrom(api.list(0, 50, false)),
          firstValueFrom(api.unreadCount()),
        ]);
        patchState(store, { items: page.items, unread: count.unread, loading: false });
      } catch (e) {
        patchState(store, { error: (e as Error).message, loading: false });
      }
    }

    return {
      refreshCount,
      loadList,

      async toggle(): Promise<void> {
        const open = !store.open();
        patchState(store, { open });
        if (open) {
          await loadList();
        }
      },

      close(): void {
        patchState(store, { open: false });
      },

      async markRead(id: number): Promise<void> {
        await firstValueFrom(api.markRead(id));
        patchState(store, {
          items: store.items().map((n) => (n.id === id ? { ...n, isRead: true } : n)),
        });
        await refreshCount();
      },

      async dismiss(id: number): Promise<void> {
        await firstValueFrom(api.dismiss(id));
        patchState(store, { items: store.items().filter((n) => n.id !== id) });
        await refreshCount();
      },

      async markAllRead(): Promise<void> {
        const unreadIds = store.items().filter((n) => !n.isRead).map((n) => n.id);
        await Promise.all(unreadIds.map((id) => firstValueFrom(api.markRead(id))));
        patchState(store, {
          items: store.items().map((n) => ({ ...n, isRead: true })),
        });
        await refreshCount();
      },
    };
  }),
);
