import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { EntityPendingState } from '../../../core/models/catalog.models';

/**
 * Marks a Catalog/Search row that the queue is about to change (§5 projection model).
 * The row already shows the projected name, folder and volume; this says why it looks
 * different from the disk, and links to the job that will make it true.
 *
 * One colour for the whole family (amber = waiting, same as the queue's own "In attesa"
 * pill): the three states differ in *what* is queued, not in urgency, and the label
 * carries that. Status is never colour-only here either.
 */
const LABELS: Record<EntityPendingState, string | null> = {
  None: null,
  PendingCreate: 'In creazione',
  PendingRename: 'In rinomina',
  PendingMove: 'In spostamento',
};

@Component({
  selector: 'ft-projection-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    @if (label(); as text) {
      @if (jobId(); as id) {
        <a
          class="badge"
          [routerLink]="['/queue']"
          [queryParams]="{ job: id }"
          [attr.title]="text + ' · apri l\\'operazione nella Coda'"
        >
          <span class="dot" aria-hidden="true"></span>{{ text }}
        </a>
      } @else {
        <span class="badge" [attr.title]="text + ' · operazione in coda'">
          <span class="dot" aria-hidden="true"></span>{{ text }}
        </span>
      }
    }
  `,
  styleUrl: './ft-projection-badge.scss',
})
export class FtProjectionBadge {
  /** Serialized EntityPendingState. 'None' renders nothing at all. */
  readonly state = input.required<EntityPendingState>();

  /** Job that owns the overlay. Without it the badge is a dead end, so it stays inert. */
  readonly jobId = input<number | null>(null);

  protected readonly label = computed(() => LABELS[this.state()] ?? null);
}
