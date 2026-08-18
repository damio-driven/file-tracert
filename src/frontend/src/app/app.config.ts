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
import { RealtimeBridge } from './core/realtime/realtime-bridge';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZonelessChangeDetection(),
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([tokenInterceptor, errorInterceptor])),
    // Resolve the loopback API token, then open the hub. One initializer, not two: the
    // token rides in the hub's query string, and a connection opened before it resolves
    // is a guaranteed 401 (separate initializers are invoked in order but not awaited
    // in order, so ordering them is not enough).
    provideAppInitializer(() => {
      const config = inject(RuntimeConfigService);
      const realtime = inject(RealtimeBridge);
      return config.load().then(() => realtime.start());
    }),
  ],
};
