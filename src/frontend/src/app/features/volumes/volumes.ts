import { ChangeDetectionStrategy, Component, effect, inject, OnInit, untracked } from '@angular/core';

import { VolumeDetailDto } from '../../core/models/catalog.models';
import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { FtPill } from '../../shared/components/ft-pill/ft-pill';
import { BytesPipe } from '../../shared/pipes/bytes.pipe';
import { DriveLetterPipe } from '../../shared/pipes/drive-letter.pipe';
import { RelativeTimePipe } from '../../shared/pipes/relative-time.pipe';
import { VolumesStore } from './volumes.store';

/** Volumi: a selectable list on the left, the technical detail panel on the right. */
@Component({
  selector: 'ft-volumes',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FtPanel, FtPill, BytesPipe, DriveLetterPipe, RelativeTimePipe],
  templateUrl: './volumes.html',
  styleUrl: './volumes.scss',
})
export class Volumes implements OnInit {
  private readonly store = inject(VolumesStore);

  protected readonly volumes = this.store.volumes;
  protected readonly selected = this.store.selected;
  protected readonly loading = this.store.loading;
  protected readonly detailLoading = this.store.detailLoading;
  protected readonly rescanningId = this.store.rescanningId;
  protected readonly error = this.store.error;

  constructor() {
    // Auto-select the first volume once the list arrives and nothing is selected.
    effect(() => {
      const list = this.volumes();
      const current = untracked(() => this.selected());
      if (list.length > 0 && current === null) {
        void this.store.select(list[0].id);
      }
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
