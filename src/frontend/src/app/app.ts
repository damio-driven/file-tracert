import { ChangeDetectionStrategy, Component, computed, inject, OnInit } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { DashboardStore } from './features/dashboard/dashboard.store';
import { BytesPipe } from './shared/pipes/bytes.pipe';

const countFormatter = new Intl.NumberFormat('it-IT');

/** Desktop window shell: titlebar + left nav + scrolling routed main. */
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, BytesPipe],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  private readonly dashboard = inject(DashboardStore);

  protected readonly stats = this.dashboard.stats;
  protected readonly serviceDown = computed(() => this.dashboard.error() !== null);
  protected readonly fileCountLabel = computed(() => {
    const n = this.stats()?.totalFiles;
    return n == null ? '—' : countFormatter.format(n);
  });

  ngOnInit(): void {
    // App-level load: powers the nav footer and the Dashboard screen alike.
    void this.dashboard.load();
  }
}
