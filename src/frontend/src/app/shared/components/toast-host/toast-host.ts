import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { ToastService } from '../../../core/toast/toast.service';

/**
 * Renders the active toast stack, bottom-right, above everything. Mounted once in
 * the app shell. Errors that reach the user land here so the client never fails
 * silently.
 */
@Component({
  selector: 'ft-toast-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="ft-toasts" role="region" aria-label="Notifiche di sistema" aria-live="polite">
      @for (t of toasts(); track t.id) {
        <div class="ft-toast ft-toast--{{ t.severity }}" role="alert">
          <span class="ft-toast__dot"></span>
          <span class="ft-toast__msg">{{ t.message }}</span>
          <button
            type="button"
            class="ft-toast__close"
            aria-label="Chiudi notifica"
            (click)="dismiss(t.id)"
          >
            ✕
          </button>
        </div>
      }
    </div>
  `,
  styleUrl: './toast-host.scss',
})
export class ToastHost {
  private readonly toastSvc = inject(ToastService);
  protected readonly toasts = this.toastSvc.toasts;

  protected dismiss(id: number): void {
    this.toastSvc.dismiss(id);
  }
}
