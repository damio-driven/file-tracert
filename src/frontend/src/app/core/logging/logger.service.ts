import { Injectable } from '@angular/core';

/**
 * Thin logging seam. A single place to later route diagnostics somewhere other
 * than the console (e.g. the Host) without touching call sites.
 */
@Injectable({ providedIn: 'root' })
export class Logger {
  info(message: string): void {
    console.info(`[ft] ${message}`);
  }

  error(message: string): void {
    console.error(`[ft] ${message}`);
  }
}
