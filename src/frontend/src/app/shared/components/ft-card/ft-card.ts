import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type CardAccent = 'teal' | 'lime' | 'blue' | 'amber' | 'red';

/**
 * Dashboard stat card: uppercase key, big value with an optional small unit, a
 * faint meta line, and a soft accent glow top-right. Value/unit/meta are inputs
 * so the card stays a dumb presenter.
 */
@Component({
  selector: 'ft-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="key">{{ key() }}</div>
    <div class="value">
      {{ value() }}@if (unit()) {<small> {{ unit() }}</small>}
    </div>
    @if (meta()) {<div class="meta">{{ meta() }}</div>}
  `,
  styleUrl: './ft-card.scss',
  host: {
    '[class]': `'ft-card ft-card--' + accent()`,
  },
})
export class FtCard {
  readonly key = input.required<string>();
  readonly value = input.required<string>();
  readonly unit = input<string>('');
  readonly meta = input<string>('');
  readonly accent = input<CardAccent>('teal');
}
