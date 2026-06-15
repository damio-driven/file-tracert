import { Injectable, signal } from '@angular/core';

export type ToastSeverity = 'error' | 'warning' | 'info' | 'success';

export interface Toast {
  readonly id: number;
  readonly message: string;
  readonly severity: ToastSeverity;
}

/**
 * Transient on-screen messages. Errors that reach the user (failed API calls)
 * raise a toast so nothing fails silently in the client. Auto-dismiss after a
 * few seconds; dismissable by hand.
 */
@Injectable({ providedIn: 'root' })
export class ToastService {
  /** Errors linger longer than informational toasts — they matter more. */
  private static readonly TTL: Record<ToastSeverity, number> = {
    error: 8000,
    warning: 6000,
    info: 4000,
    success: 4000,
  };

  private readonly _toasts = signal<readonly Toast[]>([]);
  readonly toasts = this._toasts.asReadonly();

  private nextId = 0;

  show(message: string, severity: ToastSeverity = 'info'): number {
    const id = ++this.nextId;
    this._toasts.update((list) => [...list, { id, message, severity }]);

    const ttl = ToastService.TTL[severity];
    if (ttl > 0 && typeof setTimeout !== 'undefined') {
      setTimeout(() => this.dismiss(id), ttl);
    }
    return id;
  }

  error(message: string): number {
    return this.show(message, 'error');
  }

  dismiss(id: number): void {
    this._toasts.update((list) => list.filter((t) => t.id !== id));
  }
}
