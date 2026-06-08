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
dotnet test src/backend/FileTracert.slnx
```
