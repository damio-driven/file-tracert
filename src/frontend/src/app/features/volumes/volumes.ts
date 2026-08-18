import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  OnInit,
  signal,
  untracked,
} from '@angular/core';
import { LowerCasePipe } from '@angular/common';
import { Router } from '@angular/router';

import { ScanStatusStore } from '../scans/scan-status.store';
import { VolumeDetailDto, VolumeKind } from '../../core/models/catalog.models';
import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { FtPill } from '../../shared/components/ft-pill/ft-pill';
import { ScanProgress } from '../../shared/components/scan-progress/scan-progress';
import { BytesPipe } from '../../shared/pipes/bytes.pipe';
import { DriveLetterPipe } from '../../shared/pipes/drive-letter.pipe';
import { RelativeTimePipe } from '../../shared/pipes/relative-time.pipe';
import { VolumesStore } from './volumes.store';

/** Italian label for a volume kind chip; null when the kind needs no badge (plain fixed disk). */
const KIND_LABELS: Record<VolumeKind, string | null> = {
  Fixed: null,
  Removable: 'Rimovibile',
  Cloud: 'Cloud',
  System: 'Sistema',
  Unknown: 'Sconosciuto',
};

/** Why a volume is excluded by default, by kind. */
const EXCLUDED_REASON: Partial<Record<VolumeKind, string>> = {
  Cloud: 'Unità cloud/virtuale, esclusa dalla catalogazione',
  System: 'Partizione di sistema, esclusa dalla catalogazione',
};

/** Volumi: a selectable list on the left, the technical detail panel on the right. */
@Component({
  selector: 'ft-volumes',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FtPanel, FtPill, ScanProgress, LowerCasePipe, BytesPipe, DriveLetterPipe, RelativeTimePipe],
  templateUrl: './volumes.html',
  styleUrl: './volumes.scss',
})
export class Volumes implements OnInit {
  private readonly store = inject(VolumesStore);
  private readonly scans = inject(ScanStatusStore);
  private readonly router = inject(Router);

  protected readonly catalogable = this.store.catalogable;
  protected readonly excluded = this.store.excluded;
  protected readonly selected = this.store.selected;
  protected readonly loading = this.store.loading;
  protected readonly detailLoading = this.store.detailLoading;
  protected readonly rescanningId = this.store.rescanningId;
  protected readonly togglingId = this.store.togglingId;
  protected readonly error = this.store.error;
  protected readonly scanByVolume = this.scans.byVolume;

  protected readonly showExcluded = signal(false);

  constructor() {
    // Auto-select the first catalogable volume once the list arrives and nothing is selected.
    effect(() => {
      const list = this.catalogable();
      const current = untracked(() => this.selected());
      if (list.length > 0 && current === null) {
        void this.store.select(list[0].id);
      }
    });

    // When the active scans drain (a scan just finished), refresh so counts catch up.
    let previousScanning = false;
    effect(() => {
      const scanning = this.scans.isScanning();
      if (previousScanning && !scanning) {
        void this.store.loadList();
      }
      previousScanning = scanning;
    });
  }

  ngOnInit(): void {
    void this.store.loadList();
  }

  protected select(id: number): void {
    if (this.selected()?.id !== id) {
      void this.store.select(id);
    }
  }

  protected rescan(id: number): void {
    void this.store.rescan(id);
    // The tracker pushes a frame as soon as the scan registers; this one read only covers
    // the case where the hub is down, so the spinner still appears on a degraded connection.
    void this.scans.refresh();
  }

  protected reenable(id: number): void {
    void this.store.setCatalogable(id, true);
  }

  protected exclude(id: number): void {
    void this.store.setCatalogable(id, false);
  }

  protected toggleExcluded(): void {
    this.showExcluded.update((v) => !v);
  }

  protected openSetup(volumeId: number): void {
    void this.router.navigate(['/setup'], { queryParams: { volume: volumeId } });
  }

  protected kindLabel(kind: VolumeKind): string | null {
    return KIND_LABELS[kind];
  }

  protected excludedReason(kind: VolumeKind): string {
    return EXCLUDED_REASON[kind] ?? 'Escluso manualmente dalla catalogazione';
  }

  protected roots(paths: { relativePath: string }[]): string {
    return paths.length > 0 ? paths.map((r) => r.relativePath).join('  ,  ') : '—';
  }

  protected filterSummary(filters: { effectiveFilter: string }[]): string {
    const distinct = [...new Set(filters.map((r) => r.effectiveFilter))];
    return distinct.length > 0 ? distinct.join(' · ') : '—';
  }

  protected captionFor(v: VolumeDetailDto): string {
    const seen = this.rel.transform(v.lastSeenUtc);
    const letter = v.currentLetter ?? '—';
    return v.isOnline
      ? `montato su ${letter} · visto ${seen}`
      : `scollegato · visto ${seen} · in passato su ${letter}`;
  }

  private readonly rel = new RelativeTimePipe();
}
