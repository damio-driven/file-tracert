import { HttpErrorResponse } from '@angular/common/http';

/**
 * Turns an enqueue/preview failure into a clear Italian message for the UI.
 *
 * Since step 9c the queue never REFUSES an operation because another one is in the way: a
 * conflicting request is accepted and parked `Blocked(DependencyPending)`, so there is no 409
 * to translate any more. What is left is a 400 with `{ error }` for a request that is wrong in
 * itself (an invalid name, a missing volume, a folder moved into itself). This keeps the raw
 * HTTP error off the screen (CLAUDE.md §9: no silent catch, but surface something the user
 * understands, not a stack).
 */
export function operationErrorMessage(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    const body = e.error as { error?: string } | string | null;

    if (body && typeof body === 'object' && typeof body.error === 'string') return body.error;
    if (typeof body === 'string' && body.length > 0) return body;
    return `Errore del server (${e.status}).`;
  }
  return (e as Error)?.message ?? 'Errore sconosciuto.';
}
