import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
} from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { DashboardStore } from './features/dashboard/dashboard.store';
import { NotificationsStore } from './features/notifications/notifications.store';
import { ScanStatusStore } from './features/scans/scan-status.store';
import { ConnectionStatus } from './shared/components/connection-status/connection-status';
import { NotificationsBell } from './shared/components/notifications-bell/notifications-bell';
import { ToastHost } from './shared/components/toast-host/toast-host';
import { BytesPipe } from './shared/pipes/bytes.pipe';

const countFormatter = new Intl.NumberFormat('it-IT');

/** Desktop window shell: titlebar + left nav + scrolling routed main. */
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterOutlet, RouterLink, RouterLinkActive, BytesPipe,
    ConnectionStatus, NotificationsBell, ToastHost,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
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

  ngOnInit(): void {
    // One read each at startup, then the hub pushes (step 10c): the bell badge and the
    // scan flag have no timers behind them any more. A scan already running when the app
    // opens is why the seed read exists at all.
    void this.dashboard.load();
    void this.notifications.refreshCount();
    void this.scans.refresh();
  }
}
