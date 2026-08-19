import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import { httpErrorMessage } from './http-error';
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
 * request opted out) so nothing fails silently in the client, and rethrows the ORIGINAL
 * `HttpErrorResponse`.
 *
 * C17 — it used to rethrow a fresh `Error` carrying only the message. That erased the status
 * and the body one layer above the code that needs them, so every `instanceof
 * HttpErrorResponse` downstream was false and the structured handling of a 400 was dead code
 * nobody could see was dead. Callers that only want the sentence call `httpErrorMessage`,
 * which is the same function this interceptor uses for its toast, so what the toast says and
 * what the screen says can never drift apart.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const logger = inject(Logger);
  const toast = inject(ToastService);

  return next(req).pipe(
    catchError((err: HttpErrorResponse) => {
      const message = httpErrorMessage(err);
      logger.error(`${req.method} ${req.url} failed: ${message}`);

      if (!req.context.get(SUPPRESS_ERROR_TOAST)) {
        toast.error(message);
      }

      return throwError(() => err);
    }),
  );
};
