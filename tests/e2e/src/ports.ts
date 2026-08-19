import net from 'node:net';

/**
 * The range this suite is allowed to bind. It deliberately excludes 5005, the port a developer's
 * own Host listens on: an end-to-end run must never take over — or be answered by — the instance
 * the user is working with.
 */
const FIRST_PORT = 5180;
const LAST_PORT = 5279;

/** Port 5005 is the development Host. Never ours. */
export const DEV_PORT = 5005;

// A guard, not decoration: it fires the moment someone widens the range over the developer's own
// Host instead of leaving that discovery to a confusing test run against the wrong service.
if (DEV_PORT >= FIRST_PORT && DEV_PORT <= LAST_PORT) {
  throw new Error(`The end-to-end port range ${FIRST_PORT}-${LAST_PORT} must not contain ${DEV_PORT}.`);
}

function isFree(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const server = net.createServer();
    server.once('error', () => resolve(false));
    server.once('listening', () => server.close(() => resolve(true)));
    // Loopback only, the same interface Kestrel will bind.
    server.listen(port, '127.0.0.1');
  });
}

/**
 * First free port in the dedicated range. The Host is started immediately afterwards and the
 * caller waits for it to answer, so a port that is taken in between surfaces as a Host that never
 * came up — not as a test talking to a stranger.
 */
export async function reserveHostPort(): Promise<number> {
  for (let port = FIRST_PORT; port <= LAST_PORT; port++) {
    if (await isFree(port)) {
      return port;
    }
  }
  throw new Error(`No free port in ${FIRST_PORT}-${LAST_PORT} for the end-to-end Host.`);
}
