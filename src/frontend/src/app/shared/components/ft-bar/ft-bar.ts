import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type BarColor = 'teal' | 'amber' | 'lime' | 'blue';

/**
 * Thin horizontal bar for space usage or progress. `value` is a 0–100 percent;
 * `color` encodes meaning (teal ok / amber tight / lime low-use / blue progress).
 */
@Component({
  selector: 'ft-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<i [style.width.%]="clamped()" [style.background]="'var(--' + color() + ')'"></i>`,
  styleUrl: './ft-bar.scss',
  host: { class: 'ft-bar' },
})
export class FtBar {
  readonly value = input.required<number>();
  readonly color = input<BarColor>('teal');

  protected readonly clamped = computed(() => Math.max(0, Math.min(100, this.value())));
}
