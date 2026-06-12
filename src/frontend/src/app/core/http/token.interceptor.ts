import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { RuntimeConfigService } from '../config/runtime-config.service';

const TOKEN_HEADER = 'X-FileTracert-Token';

/**
 * Attaches the loopback token to every same-origin `/api` request. Non-API
 * requests (assets, fonts) are left untouched.
 */
export const tokenInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api')) {
    return next(req);
  }

  const token = inject(RuntimeConfigService).token;
  if (!token) {
    return next(req);
  }

  return next(req.clone({ setHeaders: { [TOKEN_HEADER]: token } }));
};
