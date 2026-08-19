import { HttpErrorResponse } from '@angular/common/http';

/**
 * The one place that turns a failed request into a sentence a person can read.
 *
 * Lives in `core/http/` because that is where the HTTP concern lives: the interceptor uses it
 * for the log line and the toast, every store uses it for its `error` signal, and the
 * enqueue/preview dialogs use it for the message they show inline. One implementation, so a
 * 400 reads the same wherever it surfaces.
 *
 * The backend's error shape is `{ error: "…" }` (see `OperationsController`), and that message
 * is written for the user — it wins over any generic label this function could invent. What is
 * deliberately NOT returned is `HttpErrorResponse.message`, i.e. "Http failure response for
 * /api/operations/enqueue: 400 Bad Request": it names the transport and hides the cause. The
 * URL still reaches the log, where it belongs.
 */
export function httpErrorMessage(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    // No response at all: the service is down or the port is closed. There is no body to read
    // and the status is a meaningless 0.
    if (e.status === 0) {
      return 'Servizio non raggiungibile';
    }

    const fromBody = bodyMessage(e.error);
    if (fromBody !== null) {
      return fromBody;
    }

    if (e.status === 401) {
      return 'Token non valido o mancante';
    }
    if (e.status === 404) {
      return 'Risorsa non trovata';
    }
    return `Errore ${e.status}`;
  }

  return (e as Error | null)?.message ?? 'Errore sconosciuto.';
}

/**
 * Reads the message out of a response body, covering the shapes this API actually produces:
 * our own `{ error }`, ASP.NET's ProblemDetails (`detail`/`title`), and a plain string.
 */
function bodyMessage(body: unknown): string | null {
  if (typeof body === 'string') {
    return body.trim().length > 0 ? body : null;
  }
  if (body === null || typeof body !== 'object') {
    return null;
  }

  const candidate = body as { error?: unknown; detail?: unknown; title?: unknown; message?: unknown };
  for (const value of [candidate.error, candidate.detail, candidate.title, candidate.message]) {
    if (typeof value === 'string' && value.trim().length > 0) {
      return value;
    }
  }
  return null;
}
