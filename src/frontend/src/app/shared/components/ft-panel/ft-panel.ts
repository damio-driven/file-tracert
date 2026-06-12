import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * Bordered container with an optional header (title on the left, faint mono
 * caption on the right). Body is projected. Wraps tables and detail blocks.
 */
@Component({
  selector: 'ft-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (heading()) {
      <header class="head">
        <span class="title">{{ heading() }}</span>
        <ng-content select="[panel-header]" />
        @if (caption()) {<span class="caption mono">{{ caption() }}</span>}
      </header>
    }
    <div class="body">
      <ng-content />
    </div>
  `,
  styleUrl: './ft-panel.scss',
  host: { class: 'ft-panel' },
})
export class FtPanel {
  readonly heading = input<string>('');
  readonly caption = input<string>('');
}
