import { ChangeDetectionStrategy, Component, ViewEncapsulation, inject } from '@angular/core';
import { ConnectedPosition, OverlayModule } from '@angular/cdk/overlay';

import { NotificationSeverity } from '../../../core/models/catalog.models';
import { NotificationsStore } from '../../../features/notifications/notifications.store';
import { RelativeTimePipe } from '../../pipes/relative-time.pipe';
import { FtPill, PillVariant } from '../ft-pill/ft-pill';

/**
 * Title-bar bell: an unread badge plus an overlay panel listing background events
 * (scan failures, USN fallbacks…). The panel is rendered through a CDK overlay so
 * it escapes the app frame's `overflow: hidden`. Encapsulation is off so the
 * portaled panel keeps its styles; all classes are `ft-notif`-namespaced.
 */
@Component({
  selector: 'ft-notifications-bell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  imports: [OverlayModule, FtPill, RelativeTimePipe],
  templateUrl: './notifications-bell.html',
  styleUrl: './notifications-bell.scss',
})
export class NotificationsBell {
  protected readonly store = inject(NotificationsStore);

  protected readonly unread = this.store.unread;
  protected readonly open = this.store.open;
  protected readonly loading = this.store.loading;
  protected readonly items = this.store.items;
  protected readonly hasUnread = this.store.hasUnread;

  protected readonly positions: ConnectedPosition[] = [
    { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 10 },
    { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom', offsetY: -10 },
  ];

  protected variant(severity: NotificationSeverity): PillVariant {
    switch (severity) {
      case 'Error':
        return 'block';
      case 'Warning':
        return 'wait';
      default:
        return 'run';
    }
  }

  protected severityLabel(severity: NotificationSeverity): string {
    switch (severity) {
      case 'Error':
        return 'Errore';
      case 'Warning':
        return 'Avviso';
      default:
        return 'Info';
    }
  }
}
