import { Injectable, inject } from '@angular/core';

import { CatalogStore } from '../../features/catalog/catalog.store';
import { DashboardStore } from '../../features/dashboard/dashboard.store';
import { NotificationsStore } from '../../features/notifications/notifications.store';
import { QueueStore } from '../../features/queue/queue.store';
import { ScanStatusStore } from '../../features/scans/scan-status.store';
import { SearchStore } from '../../features/search/search.store';
import { VolumesStore } from '../../features/volumes/volumes.store';
import { RealtimeService } from './realtime.service';

/**
 * The one place that knows both SignalR and the stores. Components never see either: the
 * hub patches the same SignalStores the screens already read, so a screen written before
 * step 10c reacts to a push without a line of change (§8).
 *
 * It lives in `core/realtime/` rather than inside each store so that the fan-out is
 * readable in one screen, and so a store stays testable without a connection.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeBridge {
  private readonly realtime = inject(RealtimeService);
  private readonly queue = inject(QueueStore);
  private readonly volumes = inject(VolumesStore);
  private readonly scans = inject(ScanStatusStore);
  private readonly notifications = inject(NotificationsStore);
  private readonly catalog = inject(CatalogStore);
  private readonly search = inject(SearchStore);
  private readonly dashboard = inject(DashboardStore);

  private started = false;

  /** Registers every handler, then opens the connection. Called once, by the initializer. */
  start(): Promise<void> {
    if (this.started) {
      return Promise.resolve();
    }
    this.started = true;

    this.realtime.on('JobProgress', (m) => this.queue.applyProgress(m));
    this.realtime.on('JobStateChanged', (m) => this.queue.applyStateChanged(m));
    this.realtime.on('VolumeStatusChanged', (m) => this.volumes.applyVolumeStatus(m));
    this.realtime.on('ScanProgress', (m) => this.scans.applyScanProgress(m));
    this.realtime.on('NotificationRaised', () => this.notifications.applyRaised());
    this.realtime.on('ProjectionChanged', (m) => {
      this.catalog.invalidate(m.volumeId);
      this.search.invalidate();
    });

    this.realtime.onReconnected(() => this.refreshAll());

    return this.realtime.start();
  }

  /**
   * Refill after a gap. Messages emitted while the socket was down are never replayed, so
   * a reconnection without this leaves the UI wrong forever — quietly, which is worse than
   * being visibly offline. Every call is a no-op on a store that holds nothing yet, so this
   * refreshes what is on screen and nothing else.
   */
  private refreshAll(): void {
    // The shell always shows these two.
    void this.dashboard.load();
    void this.scans.refresh();
    void this.notifications.refreshCount();

    // The rest only if the user has actually been there: a reconnection must not fetch
    // three screens nobody opened.
    if (this.volumes.volumes().length > 0) {
      void this.volumes.loadList();
    }
    if (this.queue.result() !== null) {
      void this.queue.refresh();
    }
    this.catalog.invalidate(null);
    this.search.invalidate();
  }
}
