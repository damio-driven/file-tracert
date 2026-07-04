import { HttpErrorResponse } from '@angular/common/http';

/**
 * Turns an enqueue/preview failure into a clear Italian message for the UI. The queue
 * guards reject with structured payloads — a 409 `EntityAlreadyPending` (the same entity,
 * or an overlapping folder subtree, already has a pending op — guard C2) and a 400 with
 * `{ error }` for validation. This keeps the raw HTTP error off the screen (CLAUDE.md §9:
 * no silent catch, but surface something the user understands, not a stack).
 */
export function operationErrorMessage(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    const body = e.error as { error?: string; entityType?: string } | string | null;

    if (body && typeof body === 'object') {
      if (body.entityType === 'Directory') {
        return 'Questa cartella (o una cartella sopra/sotto di essa) ha già ' +
          'un\'operazione in coda. Attendi che si concluda o annullala.';
      }
      if (body.entityType === 'File') {
        return 'Questo file ha già un\'operazione in coda. ' +
          'Attendi che si concluda o annullala.';
      }
      if (typeof body.error === 'string') return body.error;
    }
    if (typeof body === 'string' && body.length > 0) return body;
    return `Errore del server (${e.status}).`;
  }
  return (e as Error)?.message ?? 'Errore sconosciuto.';
}
