import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import { Logger } from '../logging/logger.service';
import { ToastService } from '../toast/toast.service';

/**
 * Set on a request's `HttpContext` to suppress the error toast for background
 * polls (e.g. the unread-count tick), which would otherwise spam toasts while the
 * service is unreachable. The error is still logged and rethrown.
 */
export const SUPPRESS_ERROR_TOAST = new HttpContextToken<boolean>(() => false);

/**
 * Centralizes HTTP failure handling: logs once, raises a visible toast (unless the
 * request opted out) so nothing fails silently in the client, and rethrows a plain
 * `Error` so stores can surface it without re-parsing the `HttpErrorResponse` shape.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const logger = inject(Logger);
  const toast = inject(ToastService);

  return next(req).pipe(
    catchError((err: HttpErrorResponse) => {
      const message = describe(err);
      logger.error(`${req.method} ${req.url} failed: ${message}`);

      if (!req.context.get(SUPPRESS_ERROR_TOAST)) {
        toast.error(message);
      }

      return throwError(() => new Error(message));
    }),
  );
};

function describe(err: HttpErrorResponse): string {
  if (err.status === 0) {
    return 'Servizio non raggiungibile';
  }
  if (err.status === 401) {
    return 'Token non valido o mancante';
  }
  if (err.status === 404) {
    return 'Risorsa non trovata';
  }
  return err.error?.message ?? err.message ?? `Errore ${err.status}`;
}
