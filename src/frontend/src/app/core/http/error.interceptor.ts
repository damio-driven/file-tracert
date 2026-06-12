import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import { Logger } from '../logging/logger.service';

/**
 * Centralizes HTTP failure handling: logs once with a normalized message and
 * rethrows a plain `Error` so stores can surface it without re-parsing the
 * `HttpErrorResponse` shape everywhere.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const logger = inject(Logger);

  return next(req).pipe(
    catchError((err: HttpErrorResponse) => {
      const message = describe(err);
      logger.error(`${req.method} ${req.url} failed: ${message}`);
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
