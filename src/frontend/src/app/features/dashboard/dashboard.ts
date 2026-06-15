import { ChangeDetectionStrategy, Component, computed, inject, OnInit } from '@angular/core';

import { VolumeDto } from '../../core/models/catalog.models';
import { FtBar } from '../../shared/components/ft-bar/ft-bar';
import { FtCard } from '../../shared/components/ft-card/ft-card';
import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { FtPill } from '../../shared/components/ft-pill/ft-pill';
import { ScanProgress } from '../../shared/components/scan-progress/scan-progress';
import { volumeBarColor, volumeUsedPercent } from '../../shared/volume-display';
import { BytesPipe } from '../../shared/pipes/bytes.pipe';
import { DriveLetterPipe } from '../../shared/pipes/drive-letter.pipe';
import { RelativeTimePipe } from '../../shared/pipes/relative-time.pipe';
import { DashboardStore } from './dashboard.store';
import { ScanStatusStore } from '../scans/scan-status.store';
import { VolumesStore } from '../volumes/volumes.store';

const bytes = new BytesPipe();
const compact = new Intl.NumberFormat('it-IT', { notation: 'compact', maximumFractionDigits: 2 });

/** Dashboard: four stat cards + a live/stale volumes table. */
@Component({
  selector: 'ft-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FtCard, FtPanel, FtPill, FtBar, ScanProgress, BytesPipe, DriveLetterPipe, RelativeTimePipe],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class Dashboard implements OnInit {
  private readonly dashboard = inject(DashboardStore);
  private readonly volumesStore = inject(VolumesStore);
  private readonly scans = inject(ScanStatusStore);

  protected readonly stats = this.dashboard.stats;
  protected readonly loading = this.dashboard.loading;
  protected readonly error = this.dashboard.error;
  protected readonly volumes = this.volumesStore.catalogable;
  protected readonly volumesLoading = this.volumesStore.loading;
  protected readonly scanByVolume = this.scans.byVolume;

  protected readonly cards = computed(() => {
    const s = this.stats();
    if (!s) {
      return null;
    }
    const offline = s.volumesTotal - s.volumesOnline;
    const [pendingNum, pendingUnit] = split(bytes.transform(s.pendingBytes));
    return {
      files: {
        value: compact.format(s.totalFiles),
        meta: `su ${s.volumesTotal} volumi · ${bytes.transform(s.totalBytes)}`,
      },
      volumes: {
        value: `${s.volumesOnline}`,
        unit: `/ ${s.volumesTotal} online`,
        meta: offline > 0 ? `${offline} scollegato` : 'tutti online',
      },
      queue: {
        value: `${s.queuedJobs}`,
        meta: `${s.runningJobs} in corso · ${s.blockedJobs} bloccato`,
      },
      pending: {
        value: pendingNum,
        unit: pendingUnit,
        meta: 'in attesa di spazio o volume',
      },
    };
  });

  ngOnInit(): void {
    void this.dashboard.load();
    void this.volumesStore.loadList();
  }

  protected refresh(): void {
    void this.dashboard.load();
    void this.volumesStore.loadList();
  }

  protected usedPercent(v: VolumeDto): number {
    return volumeUsedPercent(v);
  }

  protected barColor(v: VolumeDto) {
    return volumeBarColor(v);
  }
}

function split(formatted: string): [string, string] {
  const i = formatted.lastIndexOf(' ');
  return i < 0 ? [formatted, ''] : [formatted.slice(0, i), formatted.slice(i + 1)];
}
