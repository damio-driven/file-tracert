# FileTracert

> **by FAD.iT** — Windows file cataloguing and organisation software.

FileTracert scans, catalogues, and organises files on local and removable drives.
It indexes selected file types and lets you search, move, rename, and organise them
even when drives are physically disconnected — queuing operations and executing them
when volumes come back online.

## Monorepo structure

```
filetracert/
├── src/
│   ├── backend/          .NET 10 — Web API + Windows Service
│   └── frontend/         Angular 21
└── tests/
    ├── FileTracert.Tests/ xUnit unit + integration
    └── e2e/              Playwright (step 12)
```

## Prerequisites

- .NET SDK 10.x
- Node LTS (v22+) + Angular CLI 21
- Windows (backend requires Win32 APIs)

## Build

**Backend:**
```bash
dotnet build src/backend/FileTracert.slnx
```

**Frontend:**
```bash
cd src/frontend
npm install
npm start
```

**Tests:**
```bash
dotnet test src/backend/FileTracert.slnx   # backend (xUnit)
cd src/frontend && npm test                # frontend (Vitest)
```

## Running the app (dev)

The UI talks to the token-protected loopback API. In development the two run
separately and `ng serve` proxies `/api` to the Host (same-origin → no CORS):

1. **Host** — run elevated (admin) so real volume/USN scanning works:
   ```bash
   dotnet run --project src/backend/FileTracert.Host   # listens on http://127.0.0.1:5005
   ```
2. **Frontend** — `proxy.conf.json` forwards `/api` and `/health` to the Host:
   ```bash
   cd src/frontend && npm start                        # http://localhost:4200
   ```
   The SPA fetches its API token from the Development-only `GET /api/dev/token`.

## Running the app (production-style)

The Host serves the built SPA as static files and injects the API token into
`index.html` (no dev-token endpoint exists outside Development):

```bash
cd src/frontend && npm run build      # outputs to ../backend/FileTracert.Host/wwwroot
dotnet run --project src/backend/FileTracert.Host
# open http://127.0.0.1:5005
```

`ng build` writes directly into the Host's `wwwroot/` (git-ignored). The token is
read from `<meta name="ft-token">` and attached as `X-FileTracert-Token` by an
HTTP interceptor.
