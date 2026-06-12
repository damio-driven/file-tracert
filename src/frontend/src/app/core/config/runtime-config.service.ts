import { Injectable } from '@angular/core';

/** Placeholder the Host stamps the real token over when it serves index.html. */
const TOKEN_PLACEHOLDER = '__FT_TOKEN__';

/**
 * Resolves the loopback API token once at startup.
 *
 * - **Production:** the Host serves index.html with the token baked into
 *   `<meta name="ft-token">`; we read it from there (same-origin, a page on
 *   another origin can't read this document's body).
 * - **Development:** `ng serve` serves a static index with the placeholder
 *   untouched, so we fall back to the Dev-only `GET /api/dev/token` endpoint
 *   (reached same-origin via the dev proxy).
 */
@Injectable({ providedIn: 'root' })
export class RuntimeConfigService {
  private apiToken: string | null = null;

  get token(): string | null {
    return this.apiToken;
  }

  /** Called by an app initializer before the first API call is made. */
  async load(): Promise<void> {
    const fromMeta = this.readMetaToken();
    if (fromMeta) {
      this.apiToken = fromMeta;
      return;
    }

    this.apiToken = await this.fetchDevToken();
  }

  private readMetaToken(): string | null {
    const content = document
      .querySelector('meta[name="ft-token"]')
      ?.getAttribute('content')
      ?.trim();

    if (!content || content === TOKEN_PLACEHOLDER) {
      return null;
    }
    return content;
  }

  private async fetchDevToken(): Promise<string | null> {
    try {
      const res = await fetch('/api/dev/token');
      if (!res.ok) {
        return null;
      }
      const body = (await res.json()) as { token: string };
      return body.token ?? null;
    } catch {
      // Host not reachable yet (dev). The interceptor will simply send no token.
      return null;
    }
  }
}
