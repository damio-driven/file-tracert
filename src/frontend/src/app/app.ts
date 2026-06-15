import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
} from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { DashboardStore } from './features/dashboard/dashboard.store';
import { NotificationsStore } from './features/notifications/notifications.store';
import { ScanStatusStore } from './features/scans/scan-status.store';
import { NotificationsBell } from './shared/components/notifications-bell/notifications-bell';
import { ToastHost } from './shared/components/toast-host/toast-host';
import { BytesPipe } from './shared/pipes/bytes.pipe';

const countFormatter = new Intl.NumberFormat('it-IT');

/** How often the bell re-checks the unread count (no SignalR until step 10). */
const NOTIFICATION_POLL_MS = 30_000;

/** Desktop window shell: titlebar + left nav + scrolling routed main. */
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, BytesPipe, NotificationsBell, ToastHost],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit, OnDestroy {
  private readonly dashboard = inject(DashboardStore);
  private readonly notifications = inject(NotificationsStore);
  private readonly scans = inject(ScanStatusStore);

  protected readonly stats = this.dashboard.stats;
  protected readonly serviceDown = computed(() => this.dashboard.error() !== null);
  protected readonly fileCountLabel = computed(() => {
    const n = this.stats()?.totalFiles;
    return n == null ? '—' : countFormatter.format(n);
  });

  protected readonly scanning = this.scans.isScanning;
  protected readonly scanCount = this.scans.activeCount;

  private pollHandle: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    // App-level load: powers the nav footer and the Dashboard screen alike.
    void this.dashboard.load();

    // Seed the bell badge, then refresh it on a light interval until SignalR (step 10).
    void this.notifications.refreshCount();
    this.pollHandle = setInterval(() => void this.notifications.refreshCount(), NOTIFICATION_POLL_MS);

    // Scan-progress polling (adaptive cadence); the global indicator reads its signals.
    this.scans.start();
  }

  ngOnDestroy(): void {
    if (this.pollHandle !== null) {
      clearInterval(this.pollHandle);
    }
    this.scans.stop();
  }
}
