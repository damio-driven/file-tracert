import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type PillVariant = 'on' | 'off' | 'run' | 'block' | 'wait' | 'done' | 'idle';

/**
 * Status pill: a colored dot + a text label. Status is never color-only — the
 * label always reads (Online / Scollegato / Bloccato…), so it stays legible to
 * color-blind users. Label comes via content projection.
 */
@Component({
  selector: 'ft-pill',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="dot"></span><ng-content />`,
  styleUrl: './ft-pill.scss',
  host: {
    class: 'ft-pill',
    '[class]': `'ft-pill ft-pill--' + variant()`,
  },
})
export class FtPill {
  readonly variant = input<PillVariant>('idle');
}
