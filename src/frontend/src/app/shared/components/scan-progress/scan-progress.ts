import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { ScanStatusDto } from '../../../core/models/catalog.models';

const PHASE_LABELS: Record<ScanStatusDto['phase'], string> = {
  Enumerating: 'enumerazione',
  ReadingMetadata: 'lettura metadati',
  Writing: 'scrittura indice',
  Done: 'completata',
  Failed: 'errore',
};

const counts = new Intl.NumberFormat('it-IT');

/**
 * Compact in-row scan indicator: the current phase, a running element count, and a
 * slim indeterminate bar. There's no reliable total during a scan, so the bar shows
 * activity, not a percentage (honest progress over a fake one).
 */
@Component({
  selector: 'ft-scan-progress',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="sp" role="status">
      <span class="sp-label">
        {{ phaseLabel() }}
        @if (count(); as c) {
          <span class="sp-count mono">· {{ c }} elementi</span>
        }
      </span>
      <span class="sp-track"><span class="sp-fill"></span></span>
    </div>
  `,
  styleUrl: './scan-progress.scss',
})
export class ScanProgress {
  readonly status = input.required<ScanStatusDto>();

  protected readonly phaseLabel = computed(() => PHASE_LABELS[this.status().phase]);

  protected readonly count = computed(() => {
    const s = this.status();
    const n = s.phase === 'Writing' && s.itemsWritten > 0 ? s.itemsWritten : s.itemsSeen;
    return n > 0 ? counts.format(n) : '';
  });
}
