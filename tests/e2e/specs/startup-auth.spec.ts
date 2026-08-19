import { expect, test } from '../src/fixtures.js';
import { serviceTray } from '../src/screens.js';

/**
 * The product as the user starts it: the Host serves the built SPA from its own origin, stamps
 * the loopback token into the HTML it serves, and refuses every request that does not carry it.
 */
test.describe('Avvio e autenticazione', () => {
  test('la shell si carica e le schermate ricevono i dati dal servizio', async ({ page }) => {
    await page.goto('/');

    await expect(page).toHaveURL(/\/dashboard$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();

    // The tray is the shell's own verdict on the service: it flips to "non raggiungibile" the
    // moment a call fails, so reading "servizio attivo" is the screen saying the API answered.
    const tray = serviceTray(page);
    await expect(tray).toHaveText(/servizio attivo/);
    await expect(tray).not.toHaveClass(/tray--down/);

    // The four cards only render once the payload is in; while it is not, the screen shows
    // skeletons instead. Seeing them is seeing an authenticated GET /api/dashboard succeed.
    await expect(page.locator('.cards ft-card')).toHaveCount(4);
    await expect(page.locator('.ft-error')).toHaveCount(0);
  });

  test('il token arriva al browser dal meta tag che il Host timbra su index.html', async ({
    page,
    playwright,
    host,
  }) => {
    await page.goto('/');

    const stamped = await page.locator('meta[name="ft-token"]').getAttribute('content');
    expect(stamped, 'the placeholder was never replaced').not.toBe('__FT_TOKEN__');
    expect(stamped ?? '').toMatch(/^[0-9A-F]{64}$/);

    // And it is a token that works: the value the browser was handed, sent back through the auth
    // middleware. Comparing it with a second read of the same tag would only prove it is stable.
    const asBrowser = await playwright.request.newContext({
      baseURL: host.baseURL,
      extraHTTPHeaders: { 'X-FileTracert-Token': stamped! },
    });
    try {
      expect((await asBrowser.get('/api/dashboard')).status()).toBe(200);
    } finally {
      await asBrowser.dispose();
    }
  });

  test('senza token il servizio risponde 401, con il token risponde 200', async ({
    playwright,
    host,
    api,
  }) => {
    const anonymous = await playwright.request.newContext({ baseURL: host.baseURL });
    try {
      for (const path of ['/health', '/api/dashboard', '/api/volumes']) {
        expect((await anonymous.get(path)).status(), `${path} without a token`).toBe(401);
      }

      // The hub is protected too, and its handshake is where a WebSocket client would knock.
      expect((await anonymous.post('/hubs/events/negotiate?negotiateVersion=1')).status()).toBe(401);

      // A wrong token is not a missing token: it must fail the same way.
      const wrong = await playwright.request.newContext({
        baseURL: host.baseURL,
        extraHTTPHeaders: { 'X-FileTracert-Token': 'F'.repeat(64) },
      });
      try {
        expect((await wrong.get('/api/dashboard')).status()).toBe(401);
      } finally {
        await wrong.dispose();
      }
    } finally {
      await anonymous.dispose();
    }

    // Same requests, same Host, with the token the browser was handed: served.
    const stats = await api.dashboard();
    expect(stats.totalFiles).toBe(0);
  });
});
