import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';

import { RealtimeService } from '../../../core/realtime/realtime.service';

/**
 * Says, in the titlebar, whether the push channel is alive.
 *
 * Silent while it works. The shell already carries a permanent "servizio attivo" tray, and
 * a second always-on badge for the healthy case would be chrome that never means anything.
 * It appears only when the screens have stopped receiving: reconnecting (amber, transient)
 * or offline (red, with the way out). The first connection attempt stays silent too —
 * flashing a warning at every cold start would train the user to ignore it.
 */
@Component({
  selector: 'ft-connection-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visible()) {
      <div
        class="conn"
        [class.conn--offline]="offline()"
        role="status"
        aria-live="polite"
        [attr.title]="hint()"
      >
        <span class="conn__dot" aria-hidden="true"></span>
        <span class="conn__label">{{ label() }}</span>

        @if (offline()) {
          <button type="button" class="conn__action" (click)="reconnect()">Riconnetti</button>
        }
      </div>
    }
  `,
  styleUrl: './connection-status.scss',
})
export class ConnectionStatus {
  private readonly realtime = inject(RealtimeService);

  protected readonly status = this.realtime.status;
  protected readonly offline = computed(() => this.status() === 'offline');
  protected readonly visible = computed(
    () => this.status() === 'reconnecting' || this.status() === 'offline',
  );

  protected readonly label = computed(() =>
    this.offline() ? 'aggiornamenti in pausa' : 'riconnessione…',
  );

  /**
   * The tooltip says the part that matters and does not fit: what is on screen is a
   * snapshot, not what the service knows now.
   */
  protected readonly hint = computed(() =>
    this.offline()
      ? 'Nessun aggiornamento in tempo reale: quello che vedi è l’ultimo dato ricevuto. Nuovo tentativo automatico ogni 15 secondi.'
      : 'Connessione al servizio interrotta, riconnessione in corso. I dati a schermo restano quelli dell’ultimo aggiornamento.',
  );

  protected reconnect(): void {
    void this.realtime.reconnectNow();
  }
}
