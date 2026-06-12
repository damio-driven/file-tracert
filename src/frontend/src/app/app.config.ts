import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';

import { routes } from './app.routes';
import { RuntimeConfigService } from './core/config/runtime-config.service';
import { errorInterceptor } from './core/http/error.interceptor';
import { tokenInterceptor } from './core/http/token.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZonelessChangeDetection(),
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([tokenInterceptor, errorInterceptor])),
    // Resolve the loopback API token before the first request goes out.
    provideAppInitializer(() => inject(RuntimeConfigService).load()),
  ],
};
