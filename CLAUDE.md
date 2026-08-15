# FileTracert — Project Brief (CLAUDE.md)

> **by FAD.iT** · Software di catalogazione e organizzazione file su Windows.
> Questo documento è il brief operativo per Claude Code. Contiene decisioni
> architetturali già concordate: **rispettarle**, non re-litigarle.
> Quando un punto non è coperto qui, chiedere prima di improvvisare.

---

## 1. Obiettivo

Applicazione Windows che **scansiona, cataloga e organizza** i file dei drive
locali e rimovibili dell'utente. Indicizza i file selezionati su un database e
permette di **cercarli e spostarli/rinominarli/organizzarli anche quando i drive
sono fisicamente scollegati**, accodando le operazioni ed eseguendole appena i
volumi coinvolti tornano disponibili.

Uso personale/multimediale: si indicizzano solo i tipi di file scelti dall'utente
(immagini, video, audio, documenti…), ignorando i file di sistema.

### Tre nodi tecnici che definiscono il prodotto
1. **Identità dei volumi indipendente dalla lettera** (Volume GUID).
2. **Indicizzazione efficiente e incrementale** (USN Journal su NTFS).
3. **Coda di operazioni durevole** che lavora anche con drive offline e valida
   la fattibilità (spazio) prima di eseguire.

---

## 2. Stack tecnologico

### Backend
- **.NET 10**
- **EF Core 10** + **`Microsoft.Data.Sqlite`** (provider SQLite)
- **`EFCore.BulkExtensions.Sqlite`** (versione allineata a EF Core 10) per l'ingest massivo
- ASP.NET Core Web API + **Windows Service** (unico processo host)
- **SignalR** per il real-time
- Win32 interop via P/Invoke (volumi, USN Journal, device notifications)

### Frontend
- **Angular 21** (standalone-first, zoneless, OnPush + signals)
- **`@ngrx/signals`** (SignalStore) per lo state management
- **`@microsoft/signalr`** client
- **Angular CDK** (Virtual Scroll, Overlay) — solo le primitive scomode
- **SCSS design system custom** (NESSUN framework CSS, niente Bootstrap)
- Layout in **flexbox**
- ⚠️ **Per tutto il lavoro UI usare la skill `impeccable`.**

### Test
- Backend: **xUnit** — unit + integration test, copertura completa
- Frontend: **Vitest** (integrato in Angular 21) — unit + component test
- E2E: **Playwright** sui flussi critici

---

## 3. Architettura backend — layout solution

### Monorepo
Repo unico con backend (.NET) e frontend (Angular) sotto `src/`, il `CLAUDE.md`
nella **root** (descrive entrambi i mondi).

```
filetracert/                         (root repo — qui sta questo CLAUDE.md)
├── src/
│   ├── backend/
│   │   ├── FileTracert.sln
│   │   ├── Directory.Build.props
│   │   ├── FileTracert.Contracts/    // net10.0          — DTO, enum, port interfaces, SignalR msg. Nessuna dipendenza.
│   │   ├── FileTracert.Data/         // net10.0          — DbContext, entity, IEntityTypeConfiguration, migrations
│   │   ├── FileTracert.Platform/     // net10.0-windows  — Interop Win32 (implementa le port interface)
│   │   ├── FileTracert.Business/     // net10.0          — Service + Orchestrator, queue engine, space ledger
│   │   └── FileTracert.Host/         // net10.0-windows  — Web API + Windows Service + SignalR hub + BackgroundServices
│   └── frontend/                     // Angular 21
└── tests/
    ├── FileTracert.Tests/            // net10.0-windows  — xUnit unit + integration (nella .sln)
    └── e2e/                          // Playwright (node)
```

5 progetti .NET + 2 di test, nessuna over-ingegnerizzazione.

### Grafo dei riferimenti (RISPETTARE)
```
Contracts  → (nessuno)
Data       → Contracts
Platform   → Contracts
Business   → Contracts, Data
Host       → Contracts, Data, Platform, Business
Tests      → Contracts, Data, Platform, Business, Host
```

### Regole di layering
- `Data` non conosce `Business`. `Business` non conosce `Host`.
- `Contracts` è lo **shared kernel**: DTO, enum, request/response, messaggi
  SignalR **e le port interface verso la piattaforma** (`IVolumeProbe`,
  `IUsnReader`, `IFileMover`, `IDeviceWatcher`) con i loro DTO di scambio.
  Non dipende da nulla.
- **Tutta** la P/Invoke vive in `Platform`, che **implementa** le port interface
  definite in `Contracts`. `Business` dipende solo da `Contracts` + `Data`
  (mai da `Platform`): resta `net10.0` puro, non vede chiamate native, è
  testabile con mock. `Host` wira le implementazioni `Platform` in DI.
- I pezzi legati a SQLite (FTS5, quirk UPSERT del bulk) sono isolati dietro
  `IFileSearchIndex` e `IBulkIndexWriter` (in `Contracts`, implementati in `Data`).
  **Data layer provider-agnostic**: un domani il passaggio a SQL Server/Express
  deve essere connection string + swap di un paio di adapter, non un rewrite.
- **TFM**: `Contracts`, `Data`, `Business` = `net10.0`; `Platform`, `Host`,
  `Tests` = `net10.0-windows`. Non uniformare tutto a `-windows`.

### Persistenza ibrida
- **EF Core per quasi tutto** (modello, query, operazioni normali).
- **`EFCore.BulkExtensions` per l'hot path** di indicizzazione, incapsulato in
  `BulkIndexWriter : IBulkIndexWriter`.
  - Prima scansione = `BulkInsert` puro su tabella vuota (caso ideale).
  - Sync incrementale USN = insert + update insieme → su SQLite la combinazione
    `BulkInsertOrUpdate` + identity automatica è limitata: **splittare le liste**
    insert/update. Tenere questa complessità *dentro* `BulkIndexWriter`.
  - Richiede `SQLitePCLRaw.bundle_e_sqlite3` + `SQLitePCL.Batteries.Init()` all'avvio.
- **WAL mode** attivo sul database.

### Transazioni
- Usare transazioni esplicite dove serve coerenza multi-tabella (transizioni di
  stato dei job, sync incrementale, riserve nel ledger). Pattern stile
  `ExecuteInTransactionAsync`.

### Hosting / processo
- **Windows Service elevato** (serve per USN/MFT e operazioni sui file).
- **Kestrel su loopback** (`127.0.0.1`), porta fissa. Niente binding esterno.
- **Security locale**: token generato all'avvio, richiesto in header dalle
  chiamate UI. Single-user, sufficiente.
- BackgroundServices: `ScanWorker`, `UsnSyncWorker`, `DeviceWatcherWorker`,
  `QueueProcessorWorker`.

### Concorrenza
- Queue processor MVP = **sequenziale** (FIFO, un job alla volta).
- **Space ledger = singleton thread-safe**: le richieste di *preview* arrivano
  dai thread API mentre il processor muta le prenotazioni. Proteggere con lock
  fine o `Channel`.

### CancellationToken / shutdown pulito
- `RequestAborted` sugli endpoint API.
- `ApplicationStopping` linkato (`CreateLinkedTokenSource`) nei BackgroundService.
- Il queue processor alla cancellazione **fa checkpoint dello stato e si ferma
  in modo pulito**: nessun file mezzo-copiato orfano, nessun `.fadit-partial`
  scambiato per completo.

---

## 4. Concetti chiave del dominio

### Identità volumi
La lettera (`E:`, `F:`) è effimera e **non è mai la chiave**. La chiave è il
**Volume GUID path** (`\\?\Volume{GUID}\`), scritto sul volume, stabile a
remount/cambio lettera/spegnimento.
- Enumerazione: `FindFirstVolume`/`FindNextVolume` (cattura anche partizioni
  **senza lettera**) oppure WMI `Win32_Volume`.
- Lettera corrente: `GetVolumePathNamesForVolumeName` (può tornare zero mount).
- Metadati: `GetVolumeInformation` (label, serial, filesystem).
- **Lavoriamo a livello di partizione**, non di disco fisico. Topologia disco
  (`Win32_DiskDrive → Partition → LogicalDisk`) salvata solo come metadato
  descrittivo (`PhysicalDiskId`), MAI come parte della chiave.
- Volumi riformattati / merge → **fase 2**.

### Motori di scansione (entrambi)
- **NTFS → USN Change Journal**: prima indicizzazione con `FSCTL_ENUM_USN_DATA`
  (cammino MFT, ricostruzione path da `ParentFileReferenceNumber`); incrementale
  con `FSCTL_READ_USN_JOURNAL` persistendo l'ultimo USN. Richiede admin + NTFS.
- **exFAT/FAT32 → enumerazione + diff** (size/timestamp). Niente journal.
- **No FileSystemWatcher** (inaffidabile sotto carico). Riconciliazione via USN.

### Filtri tipo file
- Applicati **dentro la pipeline di scansione**, non in post → l'index nasce
  snello e i delta USN li rispettano.
- Allow-list per estensione o **categoria** + esclusione attributi (System,
  Hidden) e path (`Windows\`, `Program Files\`, `$Recycle.Bin`, `AppData`).
- Cambiare filtro a posteriori → **riconciliazione**: i file ora esclusi si
  marcano `IsIncluded = false` (soft), NON si cancellano, così riallargare il
  filtro non richiede ri-scansione.

### Coda operazioni durevole
- Ogni job è una **macchina a stati con checkpoint persistiti**, non atomica.
- Move cross-volume: `Pending → SpaceReserved → Copying → Verifying →
  DeletingSource → Completed`.
- **Idempotenza di ogni step**: copia sempre su `*.fadit-partial`, rename
  atomico solo dopo verify; verify (size + hash se attivo) PRIMA del delete;
  delete nel cestino (mai hard-delete) → undo possibile.
- Al riavvio: ricarica tutti i job non terminali, riprende dall'ultimo checkpoint.

### Fattibilità a spazio (space ledger)
- Non basta `AvailableFreeSpace` all'enqueue: più job competono per lo stesso
  target, e alcuni job *liberano* spazio (delete del source).
- Spazio pianificabile = `free_fisico − Σ(riserve job target) + Σ(liberazioni
  job che precedono in coda)`.
- La fattibilità dipende dall'**ordine**: per l'MVP si valuta **in ordine di
  coda** (FIFO), accumulando l'effetto dei job precedenti.
- **Non rifiutare mai un job all'enqueue**: marcarlo `Blocked` con il delta
  mancante. I `Blocked` vengono **rivalutati** a ogni evento (job completato,
  volume montato, refresh).
- **Prima dell'esecuzione reale**: ricontrollo *hard* del free space + margine
  (2–5%). Mai copiare sulla fiducia di una stima.
- Target offline all'enqueue → stima su ultimo valore noto + flag
  `EstimateIsLive = false`; verifica reale al mount.

---

## 5. Modello di proiezione (CRITICO)

Il Catalogo **non è una fotografia del disco**: è una **vista proiettata** =
stato fisico (ultima scansione) **+ overlay delle operazioni in coda**.

- Accodare un'operazione muta **immediatamente** la proiezione. Se accodo lo
  spostamento di un file e poi lo cerco, lo trovo **già nella destinazione**,
  con un badge di stato ad-hoc (es. "in spostamento").
- Le **operazioni successive si validano contro la proiezione**, non contro il
  disco: se creo in coda la cartella `X` e poi ci sposto dentro dei file, il
  secondo job sa che `X` esiste anche se fisicamente non esiste ancora.
- L'overlay è **inline** sulle righe `Files`/`Directories` (campi `Pending*`),
  niente tabella separata → zero join sul path caldo di Catalogo/Ricerca.
- **Nome proiettato** (`PendingName ?? Name`) → è ciò che si indicizza in FTS5
  (la ricerca trova il file rinominato). **Path proiettato** → calcolato al volo
  risalendo i parent con gli overlay applicati (set pendente piccolo, costo
  trascurabile sui risultati paginati).
- Un rename-cartella non tocca l'FTS (i nomi file non cambiano), cambia solo i
  path proiettati a display.

### Operazioni accodabili
`CreateFolder`, `RenameFile`, `RenameFolder`, `MoveFile`, `MoveFolder`
(`Copy` → fase 2).
- Rename e move **intra-volume** (anche cartelle) = metadati, **istantanei**,
  O(1), nessuna prenotazione spazio.
- **Move cartella cross-volume** = unico caso che esplode in molti
  `OperationJobItems` (copia ricorsiva + verify + delete del sottoalbero).
- `CreateFolder` = mkdir banale in esecuzione; in proiezione la cartella "esiste
  già" (riga `Directories` con `IsMaterialized = false`).

### Dipendenze tra job
- `DependsOnJobId?` rilevata **automaticamente** quando un'operazione ha come
  target un'entità ancora pendente.
- Esecuzione e fattibilità rispettano l'ordine delle dipendenze.
- **Una sola operazione pendente per entità** (MVP): la seconda è `Blocked`
  finché la prima non si risolve. *(Chaining → fase 2, vedi §11.)*
- Cancellare un prerequisito (es. `CreateFolder`) → i dipendenti vanno
  **`Blocked` con `DependencyCancelled`** (restano in coda, riattivabili),
  NON cancellati a cascata.

---

## 6. Schema dati

### Convenzioni trasversali
- Tutte le date in **UTC**. `CreatedUtc`/`UpdatedUtc` su entità mutabili via
  `SaveChangesInterceptor` + interfaccia `IAuditable` (zero codice ripetuto).
- **Path sempre relativi alla radice del volume**; l'assoluto si risolve a
  runtime via `Volume → mount point corrente`.
- Niente hard-delete dal DB: flag di stato (`IsIncluded`, `IsPresent`) per
  distinguere "non c'è più sul disco" da "escluso dal filtro".
- Configurazioni EF in classi `IEntityTypeConfiguration` separate. **Nessuna
  data annotation sulle entità.**

### Dominio Catalogo

**Volumes**
`Id` · `VolumeGuid` (unique) · `SerialNumber?` · `Label?` · `FileSystem` ·
`IsRemovable` · `PhysicalDiskId?` · `LastDriveLetter?` · `CapacityBytes` ·
`FreeBytesLastKnown` · `LastSeenUtc` · `IsOnline` · `ScanEngine` ·
`Kind` (VolumeKind) · `IsCatalogable` (default da Kind; override utente preservato) ·
`LastUsn?` · `LastFullScanUtc?` + audit.

**WatchedRoots**
`Id` · `VolumeId`→Volumes · `RelativePath` · `IsActive` · `FilterOverrideJson?`
+ audit.

**Directories**
`Id` · `VolumeId` · `ParentId?`→self · `Name` · `MaterializedPath` (denormalizzato,
aggiornato in cascata sui rename) · `UsnFileRef?` · `IsMaterialized` ·
`PendingName?` · `PendingParentId?` · `PendingState` · `PendingJobId?` + audit.

**Files**
`Id` · `VolumeId` · `DirectoryId`→Directories · `Name` · `Extension` (lower) ·
`Category` (derivata e persistita) · `SizeBytes` · `CreatedUtc`/`ModifiedUtc`
(del file) · `Attributes` · `UsnFileRef?` · `QuickHash?` (size + primi/ultimi KB)
· `Hash?` (full, lazy) · `IsIncluded` · `IsPresent` · `LastIndexedUtc` ·
`PendingName?` · `PendingDirectoryId?` · `PendingState` · `PendingJobId?` + audit.
Indici: `(VolumeId, DirectoryId)`, `Extension`, `Category`, `SizeBytes`,
`ModifiedUtc`, `UsnFileRef` unique per volume.

**FileSearchIndex** — tabella virtuale **FTS5** (DB principale), colonne `name`
(nome proiettato) + `path` (path relativo completo), `rowid = Files.Id`, tokenizer
`unicode61` accent-insensitive (prefix query supportate). Creata via SQL raw in
migration. **Sync esplicito** via `IFileSearchIndex` (no trigger): popolata negli
stessi batch del `BulkIndexWriter`; `RebuildAsync` per il backfill. La ricerca
supporta scope **solo nome** (colonna `name`) o **path completo** (entrambe le
colonne), scelto dall'utente in UI. SQLite-specific, dietro `IFileSearchIndex`.

### Dominio Operazioni

**OperationJobs**
`Id` · `Type` (JobType) · `State` · `BlockReason` · `SourceVolumeId?` ·
`TargetVolumeId?` · `TargetRelativePath?` · `IsIntraVolume` · `TotalBytes` ·
`BytesProcessed` · `RequiredBytesTarget` · `FreedBytesSource` · `EstimateIsLive`
· `SequenceOrder` (FIFO) · `DependsOnJobId?` · `RetryCount` · `ErrorMessage?` ·
`CreatedUtc`/`StartedUtc?`/`CompletedUtc?` + audit.

**OperationJobItems**
`Id` · `JobId`→OperationJobs · `FileId?` · `SourceRelativePath` (snapshot) ·
`TargetRelativePath` · `SizeBytes` · `State` (JobItemState) · `TempPath?`
(`.fadit-partial`) · `BytesCopied` · `Hash?` · `ErrorMessage?`.

**SpaceLedgerEntries** (esplicito)
`Id` · `JobId` · `VolumeId` · `DeltaBytes` (+riserva / −liberazione) · `IsActive`.

### Configurazione

**ExtensionCategories**
`Extension` (PK) · `Category`. Seed con le estensioni comuni.

**AppSettings** — singleton tipizzato: filtro default, esclusioni path, token API
loopback, margine % spazio, `MinimumLogLevel`, retention log.

### Diagnostica

**LogEntries** — su **DB log dedicato** (`filetracert-logs.db`, non il DB
principale): `Id` · `TimestampUtc` · `Level` · `Category` · `Message` ·
`Exception?` (full) · `EventId?` · `Scope?`. Scritta da un `ILoggerProvider`
custom con coda non bloccante; retention/trim periodico.

**Notifications** — DB principale (basso volume, errori di background visibili in
UI): `Id` · `TimestampUtc` · `Severity` (Info|Warning|Error) · `Source` ·
`Title` · `Message` (eccezione reale) · `VolumeId?` · `IsRead` · `IsDismissed` + audit.

### Enum
- `VolumeScanEngine` { UsnJournal, Enumeration }
- `VolumeKind` { Fixed, Removable, Cloud, System, Unknown }
- `FileCategory` { Image, Video, Audio, Document, Archive, Other }
- `JobType` { CreateFolder, RenameFile, RenameFolder, MoveFile, MoveFolder } *(Copy → fase 2)*
- `JobState` { Pending, SpaceReserved, Copying, Verifying, DeletingSource, Completed, Blocked, Failed, Cancelled }
- `JobBlockReason` { None, InsufficientSpace, TargetVolumeOffline, SourceVolumeOffline, NameCollision, DependencyPending, DependencyCancelled }
- `JobItemState` { Pending, Copying, Copied, Verified, Done, Failed, Skipped }
- `EntityPendingState` { None, PendingCreate, PendingRename, PendingMove }

---

## 7. API surface (linee guida)

- **Paging/sort/filtro lato server ovunque** (milioni di righe). Pattern condiviso
  `PagedRequest` / `PagedResult<T>` (skip/take o cursore, `sortBy`).
- **Endpoint preview/dry-run**: `POST /operations/preview` esegue la logica del
  ledger e ritorna la fattibilità **senza creare il job** (riusa il motore
  dell'enqueue). Serve alla UI per dire "ci sta / non ci sta / stima offline"
  prima di confermare.
- DTO con **freschezza del dato**: `lastSeenUtc`, `isStale`/`dataIsLive`,
  `estimateIsLive` dove i dati provengono da un volume offline.
- Fattibilità come **oggetto**, non booleano:
  `{ requiredBytes, reservedBytes, availableEstimateBytes, deficitBytes,
  estimateIsLive, blockingVolumeId }`.
- Navigazione catalogo = **albero lazy** (figli on-demand), distinta dalla ricerca.

### SignalR hub — messaggi tipizzati
`VolumeStatusChanged`, `JobProgress`, `JobStateChanged`, `ScanProgress`,
`ProjectionChanged` (per refresh del Catalogo/Ricerca quando l'overlay cambia).

---

## 8. Architettura frontend

### Struttura (standalone-first)
```
src/app/
├── core/            // servizi singleton, interceptor, auth token, signalr client
├── shared/          // componenti riutilizzabili, direttive, pipe
├── styles/          // _tokens.scss, _mixins.scss, _utilities.scss
└── features/        // feature lazy-loaded per dominio
    ├── dashboard/
    ├── volumes/
    ├── catalog/
    ├── search/
    └── queue/
```
- **Feature lazy-loaded** per dominio (route `loadComponent`/`loadChildren`).
  NIENTE NgModule.
- **OnPush + signals** ovunque, zoneless.
- **`@ngrx/signals`** SignalStore per dominio; gli eventi SignalR aggiornano i
  signal → la UI reagisce da sola.
- Componenti riutilizzabili in `shared/`, zero duplicazione.

### Design system SCSS (custom, niente Bootstrap)
- `_tokens.scss` — CSS variables: palette (teal `#2ec4b6`, lime `#a8e063`,
  amber/blue/red di stato), spacing scale, radius, tipografia.
- `_mixins.scss` — `@mixin panel`, `@mixin pill($color)`, `@mixin flex-center`…
- `_utilities.scss` — classi flex riutilizzabili: `.flex`, `.flex-col`,
  `.items-center`, `.justify-between`, `.gap-2/3/4`, spacing.
- Classi componente condivise: `.ft-panel`, `.ft-btn`, `.ft-pill`, `.ft-card`.
- **Layout in flexbox**. Liste enormi (Catalogo/Ricerca) → **CDK Virtual Scroll**.
- Tipografia: IBM Plex Sans + IBM Plex Mono (per path, GUID, valori tecnici).
- **Tema dark**. Vedi mockup di riferimento (`filetracert-mockup.html`).
- ⚠️ **Usare la skill `impeccable` per la realizzazione del frontend.**

### Schermate (6 + 1 fase 2)
1. **Dashboard** — card riassuntive + tabella volumi con stato live/stale.
2. **Volumi** — dettaglio per volume (GUID, serial, FS, disco fisico, cartelle,
   filtri, indice/USN), azioni (ri-scansiona, modifica cartelle/filtri).
3. **Catalogo** — browser ad albero lazy che **funziona offline**; selezione
   multipla → accoda operazione; badge di stato proiettato.
4. **Ricerca** — FTS5 sul nome proiettato + filtri (categoria, dimensione, data,
   volume, solo-online); risultati con volume e stato.
5. **Coda** — tabella job con stato, progress, colonna fattibilità (delta
   mancante, volume in attesa).
6. **Regole** — fase 2.

---

## 9. Standard di codice

- Best practices .NET 10 / Angular 21.
- **Niente codice duplicato**: estrarre helper riutilizzabili, dividere in
  metodi per singolo compito.
- Transazioni dove serve coerenza.
- Async/await end-to-end con CancellationToken propagato.
- Naming chiaro, niente abbreviazioni criptiche.
- Commenti dove la logica non è ovvia (USN, ledger, proiezione).
- **No silent catch**: mai sopprimere un'eccezione in silenzio. La cattura per
  resilienza è consentita (es. `ScanWorker` che prosegue sugli altri volumi se
  uno fallisce), ma ogni `catch` deve (1) loggare l'eccezione completa
  (messaggio + stack + inner) e (2) se l'errore riguarda un'azione/aspettativa
  dell'utente, farlo risalire a video (risposta API o riga in `Notifications`).
  Resilienza sì, silenzio no.

---

## 10. Ordine di implementazione suggerito (MVP)

0. **Scaffold** monorepo + solution (5+2 progetti, riferimenti, TFM, file di
   supporto, scaffold Angular). Skeleton-only, deve compilare a vuoto.
   → vedi `TASK-step0-scaffold.md`.
1. `Data` + entity + configurations + migrations + seed `ExtensionCategories`.
2. `Platform`: `IVolumeProbe` (enumerazione volumi + identità GUID).
3. `Platform`: `IUsnReader` (full enum + incrementale) e fallback enumerazione.
4. `Business`: scan pipeline + `BulkIndexWriter` + filtri.
5. `Host`: Windows Service skeleton + Web API + `ScanWorker`.
6. Frontend: shell + Dashboard + Volumi (consumano l'indice).
7. Catalogo + Ricerca (FTS5) **read-only**.
8. `Business`: space ledger + queue state machine + `QueueProcessorWorker`.
9. Modello di proiezione (overlay inline + dipendenze).
10. `IDeviceWatcher` + rivalutazione job + SignalR end-to-end.
11. Catalogo/Coda **write** (accoda operazioni, preview/dry-run).
12. Test completi (xUnit, Vitest, Playwright).

---

## 11. Roadmap fase 2 (NON dimenticare)

- **Chaining di operazioni multiple sulla stessa entità** *(esplicitamente
  richiesto)* — la proiezione riflette il netto di più op concatenate, oltre il
  vincolo MVP "una sola op pendente per entità".
- **Scheduling intelligente della coda** — riordino dei job per massimizzare le
  esecuzioni possibili (es. anticipare un delete che sblocca più move). È
  ottimizzazione combinatoria.
- **Motore di suggerimenti** — sia regole esplicite confermate dall'utente
  (es. `.raw` > 6 mesi → volume Archivio), sia pattern *inferiti* dallo storico
  delle operazioni.
- **FavoriteTargets** — destinazioni recenti/preferite per "Sposta in…".
- **Operazione Copy** (oltre a Move).
- **Volumi riformattati / merge** e identità via serial come segnale secondario.
  *(Nota: per i drive cloud/virtuali si è scelta l'**esclusione** di default — step
  6.7; il riaggancio-per-firma di un volume con GUID cambiato resta qui in fase 2.)*
- **Path UNC / di rete** (modello di identità diverso).
- Eventuale **upgrade ad Angular 22** quando assestata.

### Debiti tecnici noti e datati
- **Classificazione Cloud non affidabile** *(emerso allo step 6.7)* — Google Drive
  File Stream (online e offline) resta classificato `Unknown` invece di `Cloud`
  nonostante la regola `HasPhysicalExtents=null && PhysicalDiskId=null → Cloud`.
  Workaround attuale: **esclusione manuale** (`IsCatalogable=false`), che funziona.
  Da chiudere: (1) loggare i segnali grezzi che arrivano al `VolumeClassifier`
  durante il sync (`HasPhysicalExtents`, `PhysicalDiskId`, `DriveType`, `Kind`
  risultante) per capire su quale ramo cade davvero; (2) i volumi **offline** non
  vengono mai ri-sondati → mantengono la classificazione vecchia: serve una
  riconciliazione dai dati già persistiti. Non urgente finché l'esclusione manuale
  copre il caso.
- **Re-scan idempotente vs proiezione + contesa di lock** *(introdotto allo step 4;
  aggravato allo step 8)* — lo `ScanService.PersistAsync` avvolge delete-all
  volume + BulkInsert dell'intero volume in **un'unica transazione**, che tiene il
  write-lock unico di SQLite per **minuti** durante gli scan grossi (es. C:). Due
  conseguenze: (1) il truncate-per-volume cancella gli overlay `Pending*` a ogni
  re-scan → va sostituito con un **merge** che preserva l'overlay; (2) la
  transazione monolitica causa **`SQLITE_BUSY`** sugli altri writer
  (VolumeSyncWorker, API) → `database is locked`. Mitigato allo step 8 con
  WalCheckpointWorker + `busy_timeout` 15s (cerotto, NON cura). **Allo step 9 il
  rework deve: preservare l'overlay E spezzare la transazione in blocchi corti**
  (commit per batch, rilascio del write-lock tra i blocchi) così il sync/API non
  attendono minuti. È lo stesso punto di codice: farlo una volta sola.
- **Re-scan idempotente vs proiezione** *(introdotto allo step 4)* — vedi la voce
  sopra: matching per `UsnFileRef`/path, update invece di delete+insert dei record
  con stato pendente. Da affrontare obbligatoriamente prima di considerare la
  proiezione completa.

---

## 12. Riferimenti
- Mockup UI navigabile: `filetracert-mockup.html`.
- Primo task (scaffold): `TASK-step0-scaffold.md`.
- Palette brand FAD.iT: teal `#2ec4b6`, lime `#a8e063`.

---

## Come si esegue un task
- **Il piano è il file TASK.** Non usare la skill `writing-plans`: il documento di
  task È il piano. Implementa direttamente, per commit.
- **Un commit per preoccupazione.** Segui lo split di commit suggerito nel task;
  se un file porta più fix, usa staging a livello di hunk (`git add -p`).
- **Riverifica le righe sull'HEAD** prima di editare: i file:riga nei task/review
  possono essere datati. Conferma prima di toccare.
- **Sessioni pulite e segmentate.** Un task per sessione; per task densi, fermati
  ai checkpoint indicati. Non affrontare tutto in un'unica risposta (limite output).
- **Niente scope creep.** Se un fix richiede un pezzo di un altro work package,
  fai il minimo indispensabile e **segnalalo**; non anticipare interi WP.

## Test (non negoziabile)
- **RED prima del GREEN:** ogni fix ha un test che riproduce il bug e fallisce
  PRIMA del fix, poi diventa verde.
- **Contro l'implementazione reale, mai mock del componente sotto esame.** Un test
  che mocka il ledger non testa il ledger. Engine + ledger + Win32FileMover + SQLite
  veri (su sandbox temp per la suite).
- **Verifica sul ferro via harness, NON manuale.** L'utente non fa più collaudi a
  mano. Ogni comportamento nuovo/fixato va coperto da uno scenario in
  `FileTracert.HardwareSmoke`, che deve passare (PASS) sul ferro configurato.
- **Suite verde + build pulita** (warnings-as-errors) a fine di ogni task.

## Code review finale (obbligatoria)
A fine task, code review indipendente delle modifiche: correttezza vs criteri e
scenari di fallimento; no silent catch (§9); layering (§3); no duplicazione; test
reali RED→GREEN; idempotenza/crash-safety dove si tocca la state machine o le
operazioni su file. Riportare cosa ha trovato e cosa è stato corretto (o perché un
rilievo è stato lasciato consapevolmente).

## Lavoro in parallelo (attenzione)
- **Due agenti nella stessa working directory DEVONO stare sullo stesso branch.**
  Un `checkout -b` cambia i file su disco sotto l'altro agente. Mai creare/cambiare
  branch se un altro agente lavora nella stessa cartella.
- **Niente parallelo sui file caldi della coda** (`JobExecutionEngine`,
  `SpaceLedger`, `QueueService`, `QueueProcessorWorker`): agente unico, in sequenza.
  Il parallelo è ammesso solo tra progetti isolati (es. l'harness).
- Isolamento vero della history = working directory separate (`git worktree`), non
  branch nella stessa cartella.
- **Host chiuso prima di ricompilare** (evita lock DLL / rebuild lenti).

## Principi architetturali sempre validi (dai §3/§5/§6/§9)
- **Layering:** `Business ↛ Platform`; tutta la P/Invoke in `Platform` dietro
  interfacce; SQLite-specifics dietro `IFileSearchIndex`/`IBulkIndexWriter`.
- **No hard-delete:** flag (`IsIncluded`/`IsPresent`/`IsMaterialized`), delete nel
  cestino, mai cancellare ciò che non si è copiato+verificato.
- **No silent catch:** resilienza sì (worker prosegue), silenzio no (log completo +
  Notification dove è azione utente).
- **Crash-safety:** ogni transizione idempotente e resume-aware; rilascio ledger
  nella stessa transazione del cambio stato; `Blocked` riattivabile, non `Failed`
  terminale, per condizioni recuperabili (spazio, offline, collisione).

## Roadmap (ordine di lavoro)
Stato: WP3 (perdita dati), WP1 (crash-safe), WP2 (offline gate), **fix UX date/UTC**
(#12, #11, C31) — **fatti**.
Prossimo, in ordine:
1. **Step 9 — Proiezione:** overlay `Pending*` inline, dipendenze tra job, FTS sul
   nome proiettato. Qui si saldano i **debiti §11**: truncate-per-volume → **merge**
   che preserva l'overlay, E **spezzare la transazione monolitica dello scan** in
   blocchi corti (chiude anche la contesa di lock SQLITE_BUSY). Prerequisito già
   posato dai WP1/WP2.
2. **Step 10 — Device-watcher + SignalR real-time:** sostituisce il trigger polling
   del WP2 con push; remount istantaneo; progress/notifiche/coda in tempo reale.
3. **Work package minori rimanenti** (dalla code review): guard enqueue, indice/
   ricerca (#6 `UsnFileRef` al move cross-volume, C19 `Extension`/`Category` al
   rename, C16 esclusione ereditata, P2 collation), spazio, resto UX, logging/
   shutdown, efficienza, cleanup (incl. eventuale spostamento di `ScanPath` in
   `Contracts` come da review).
4. **Step 12 — Test UI end-to-end (Playwright).**

### Fatto nel giro «date/UTC» (2026-08-10, commit `f523938`…`b627fc4`)
Converter UTC globale in `ConfigureConventions` (write condizionale: solo `Local`
convertito, altrimenti si sposterebbero gli `Unspecified` già su disco); bound date
passati come `DateTime` al provider invece che come stringa ISO; filtro data esposto
in Ricerca con `shared/date/day-range.util` (giorno locale → istante UTC, bound alto
fino all'ultimo tick); scenario harness `search-date-filter` (PASS sul ferro).
**Aperti consapevolmente** dalla review: (a) `FileSearchIndex.AsUtc` timbra un bound
`Unspecified` come UTC invece di rifiutarlo — non è una regressione (il vecchio
`ToString("o")` faceva lo stesso) ma va deciso col filtro data del Catalogo, primo
vero caller in-process; (b) l'harness asserisce il `Kind` sull'entità EF, non sul DTO
serializzato: il path JSON della ricerca resta scoperto fino allo step 12.
**Nota UI:** i filtri **dimensione** (`sizeBytesMin/Max`) esistono nello store ma non
hanno ancora un controllo in Ricerca.

## Cosa resta all'umano (non delegabile al repo)
Le **decisioni di prodotto e di priorità**: cosa promuovere a bloccante, cosa
rimandare come debito datato, cosa entra o esce dall'MVP. L'agente esegue; la regia
sulle scelte resta all'utente. In caso di decisione di **prodotto** ambigua, **fermarsi
e chiedere** invece di scegliere per conto proprio.

**Le decisioni tecniche dentro un task già approvato NON si chiedono** *(regola
stabilita il 2026-08-11)*: schema e migration, strutture dati, approccio di
implementazione, split dei commit, dove vive un helper. L'agente sceglie, **documenta
la scelta e il perché** nel task o nel commit, e procede. Chiedere conferma su questa
classe di decisioni rallenta e basta. Resta l'eccezione ovvia delle azioni distruttive
o irreversibili fuori dal repo.