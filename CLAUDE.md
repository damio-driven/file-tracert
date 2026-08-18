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

### Dipendenze tra job *(implementate allo step 9c)*
- `DependsOnJobId?` rilevata **automaticamente** quando un'operazione ha come
  target un'entità ancora pendente.
- Esecuzione e fattibilità rispettano l'ordine delle dipendenze.
- **Una sola operazione pendente per entità** (MVP): la seconda è `Blocked`
  finché la prima non si risolve. *(Chaining → fase 2, vedi §11.)*
- Cancellare un prerequisito (es. `CreateFolder`) → i dipendenti vanno
  **`Blocked` con `DependencyCancelled`** (restano in coda, riattivabili),
  NON cancellati a cascata. **Riattivabili = «Riprova»**, non in automatico:
  il guard è pulito già nell'istante dell'annullamento, quindi liberarli da soli
  vorrebbe dire ricreare in silenzio ciò che l'utente ha deciso di non creare.

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

**Directories** *(step 9b: `Pending*` scritti all'enqueue da `OverlayWriter`; una riga
`PendingCreate` ha `IsMaterialized = false` e `IsPresent = false` finché il job non la crea)*
`Id` · `VolumeId` · `ParentId?`→self · `Name` · `MaterializedPath` (denormalizzato,
aggiornato in cascata sui rename) · `UsnFileRef?` · `IsMaterialized` · `IsPresent`
(default `true`; stessa semantica di `Files.IsPresent` — la scansione non l'ha più
trovata sul disco, mai un delete) · `PendingName?` · `PendingParentId?` ·
`PendingState` · `PendingJobId?` + audit.

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

**OperationJobs** *(step 9c: `SequenceOrder` ha un indice **unico** e viene assegnato dentro la
transazione di insert; `DependsOnJobId` è scritto dal guard di enqueue e ripuntato alla rivalutazione)*
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
- ~~**Re-scan idempotente vs proiezione + contesa di lock**~~ *(introdotto allo step 4,
  aggravato allo step 8, **chiuso allo step 9a — 2026-08-15**)*. `PersistAsync` non
  tronca più: fa **merge**. Matching per `UsnFileRef` (identità vera, regge un rename
  fatto fuori dall'app) e in subordine per `(DirectoryId, Name) COLLATE NOCASE`;
  aggiorna solo i fatti fisici, quindi `Id`, `Pending*`, `IsIncluded`, hash
  sopravvivono; ciò che non si è più visto va `IsPresent=false` (mai delete). Il
  merge dei file è set-based dietro `IBulkIndexWriter` (staging TEMP per lotto), le
  directory in `DirectoryMerger`. Le transazioni sono **corte**: un commit per lotto
  (default 5 000 file), checkpoint `LastFullScanUtc`/`LastUsn` in transazione propria
  a fine scan, così un'interruzione non dichiara completo uno scan parziale. Il
  **primo** scan di un volume resta bulk insert puro. Restano validi ma non più
  necessari come cerotto: `WalCheckpointWorker` + `busy_timeout`.
  Misura sul ferro (D:, 2 002 file): primo scan 1,05 s, re-scan 0,83 s (prima un
  re-scan costava quanto un primo scan).

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
(#12, #11, C31), **step 9a** (merge dello scan + transazioni corte), **step 9b**
(proiezione: overlay scritto/letto/pulito), **step 9c** (dipendenze tra job + guard
unificato, WP4 intero), **step 10a** (device watcher: il remount è un push, non un poll)
— **fatti**. Lo **step 9 è chiuso**.
Prossimo, in ordine:
1. **Step 10b — SignalR hub** (progress/notifiche/coda in tempo reale) e **step 10c —
   frontend realtime**. Il device-watcher (10a) è già dentro.
2. **Work package minori rimanenti** (dalla code review): indice/
   ricerca (#6 `UsnFileRef` al move cross-volume, C19 `Extension`/`Category` al
   rename, C16 esclusione ereditata, P2 collation), spazio, resto UX, logging/
   shutdown, efficienza, cleanup (incl. eventuale spostamento di `ScanPath` in
   `Contracts` come da review, e la discrepanza §3: `IBulkIndexWriter` sta in
   `Data/Indexing` e `IFileSearchIndex` in `Contracts/Search`, mentre §3 li dà
   entrambi in `Contracts` — segnalata allo step 9a, non spostata).
   Da valutare qui anche: un file **fuori** dai watched root attivi viene marcato
   `IsPresent=false` al primo scan del volume. Non è una regressione (prima veniva
   **cancellato** dal truncate), ma ora è visibile come «assente» invece che sparito.
3. **Step 12 — Test UI end-to-end (Playwright).**

### Fatto nello step 10a (2026-08-18, commit `a86783a`…`40c36d4`)
Il remount di un drive è un **evento**, non più un'attesa fino a 60 s. Il polling resta, come rete.
- **`IDeviceWatcher` in `Contracts/Platform`** — port orientata all'evento (`Changed` +
  `Start()`, `IDisposable`), **senza identità del dispositivo**: la notifica di sistema porta un
  *symbolic link name* (`\\?\STORAGE#Volume#…`), non il Volume GUID path che è la nostra chiave
  (§4), e mapparlo non è né economico né affidabile. L'evento significa «qualcosa è cambiato,
  ri-sonda»; l'identità la risolve `VolumeSyncService`, che enumera e matcha per GUID come già fa.
- **`Win32DeviceWatcher` su `CM_Register_Notification`** (cfgmgr32, filtro
  `GUID_DEVINTERFACE_VOLUME`). Scelto al posto di `RegisterDeviceNotification` perché **non
  richiede né una finestra né l'handle di `RegisterServiceCtrlHandlerEx`**: il generic host non ha
  nessuno dei due, e lo stesso codice deve girare in console (dev) e come servizio (prod).
  Due scelte di implementazione: (1) la callback è un **function pointer**
  `[UnmanagedCallersOnly]`, non un delegate marshalled tenuto vivo in un campo — così non esiste
  proprio un oggetto gestito che il GC possa raccogliere sotto cfgmgr32, e l'istanza si raggiunge
  con una `GCHandle` **forte** passata come contesto (forte per lo stesso motivo per cui il
  delegate andrebbe rootato: il nativo tiene un puntatore grezzo per tutta la registrazione).
  `Dispose` **deregistra prima** (il che drena le callback in volo) e libera l'handle dopo.
  (2) `CM_NOTIFY_FILTER` è dichiarata `LayoutKind.Explicit` con `Size` esplicita che copre l'unione
  nativa (16 + 400 = 416 byte su x86 e x64), così `cbSize` è giusta senza campi di padding morti.
- **`VolumeSyncCycle` in `Host/Infrastructure`** — il corpo di `VolumeSyncWorker.SyncOnceAsync`
  estratto: sync → rivalutazione dei job parcheggiati → segnale alla coda. **Uno solo** per i due
  trigger (§9). Singleton, perché il `SemaphoreSlim(1,1)` che li serializza dev'essere condiviso.
  Il secondo chiamante **aspetta**, non viene scartato: un ciclo già in corso può aver enumerato i
  volumi un istante prima che il drive comparisse, e scartare perderebbe proprio l'arrivo per cui
  il push esiste. Al massimo se ne accodano due, e un ciclo non scansiona file.
- **`DeviceWatcherWorker`** — la raffica che Windows spara per un singolo inserimento viene
  collassata da un canale **capacità 1 `DropWrite`** più una finestra di debounce
  (`DeviceChangeDebounceMilliseconds`, default 1000): l'handler gira su un thread di sistema e fa
  solo `TryWrite`, il loop aspetta il primo token, dorme la finestra, **svuota** ciò che è
  atterrato e fa **un** ciclo. Una notifica arrivata *dopo* lo svuotamento tiene il proprio token e
  avrà il proprio ciclo — può essere un cambiamento che quello in corso non ha visto.
- **Fallimento della registrazione = rumoroso, non fatale** (§9): log completo **e** Notification
  `Warning` che dice all'utente che il rilevamento automatico non è attivo e che i volumi verranno
  comunque riconosciuti entro l'intervallo di sync; poi il worker esce, così nulla resta in attesa
  di eventi che non arriveranno. `VolumeSyncWorker` **non cambia comportamento**: resta la rete.
- **Verifica**: xUnit **558 verdi** (+11), build backend pulita (warnings-as-errors). RED
  dimostrato rompendo il prodotto apposta: canale unbounded senza svuotamento → la raffica diventa
  3 cicli; gate rimosso → due cicli dentro la stessa chiamata di piattaforma. Harness sul ferro
  (`D:\Collaudo\A`, coppia *intra*): **10 scenari applicabili, 10 PASS**; primo scan 1,08 s,
  re-scan 0,59 s su 2 002 file.
La **code review finale** non ha trovato rilievi sopra soglia: layout della struct e `cbSize`,
ordine deregistrazione→`GCHandle.Free`, thread-safety di `Start`/`Dispose`, ogni interleaving del
debounce (nessuna notifica viene persa: o viene assorbita dal ciclo in corso o resta pendente per
il successivo), il gate con `WaitAsync` **fuori** dal `try` (nessuna `Release` su un lock mai
preso) e la prova temporale dell'harness (nel contenitore dell'harness non gira alcun poll: un
PASS può venire solo dal push) sono stati verificati uno per uno. L'unica osservazione sotto
soglia — nel ramo «l'handle di contesto non si risolve» non c'era un logger con cui parlare, cioè
un catch muto sulla carta (§9) — è stata **corretta**: il logger dell'ultima registrazione è
tenuto in un campo statico e fa da sink a quel ramo.
**Limiti noti e accettati:**
- **`offline-unplug` non è stato eseguito in questo giro**: richiede `SemiAutomatic=true`, un drive
  esterno e un operatore che stacchi e ricolleghi. Il codice dello scenario è aggiornato — la
  seconda metà non sincronizza più a mano, avvia un `DeviceWatcherWorker` vero e **asserisce** il
  tempo *ricollegamento → job terminale* contro un budget di 25 s, FAIL con il numero in chiaro —
  ma il PASS sul ferro va preso al primo collaudo con drive esterno.
- **Un arrivo fisico non è simulabile** in un test: gli scenari harness non interattivi continuano
  a sincronizzare a mano (stanno testando il *gate* della coda, non il trigger) e il README lo dice
  invece di dichiarare un PASS su un push mai avvenuto.
- La classificazione del dispositivo non entra nell'evento (vedi sopra): il watcher non distingue
  *quale* volume è arrivato, e ogni evento costa un'enumerazione completa dei volumi. Su una
  macchina con molti dispositivi USB è il prezzo di non fidarsi del symbolic link name.

### Fatto nello step 9c (2026-08-16, commit `c9f0b34`…`616c25e`)
Le **dipendenze tra job** del §5 esistono davvero: `DependsOnJobId` non è più una colonna morta
e `DependencyPending`/`DependencyCancelled` non sono più irraggiungibili. Chiude l'intero **WP4**
(finding 8 + C26 + K5), la metà «dipendenze» del finding 9 e l'ultima metà del finding 13.
- **Un solo predicato di sovrapposizione** — `ScanPath.Overlaps` (case-insensitive,
  segment-aware) usato da `PendingWorkGuard`, unico posto che risponde a «un altro job sta già
  lavorando qui?». Vede **source e target** di ogni job non terminale, `CreateFolder` compreso
  (che non ha item: il suo unico path sta sul job). La regola: **un path SORGENTE che si
  sovrappone a qualunque path dell'altro** è conflitto (chi rinomina/sposta toglie il terreno a
  tutto ciò che sta sopra, sotto o lì); **due TARGET uguali** sono conflitto; due target
  semplicemente annidati **no** — è il caso §5 «accodo la cartella X e poi ci sposto dentro i
  file», che deve restare legale. `DirectoryQueries.InSubtree` fa lo stesso per le query di
  sottoalbero in SQL. *Deviazione documentata dal piano*: il predicato NON esiste «in due forme»
  (in memoria + SQL); in SQL userebbe il misto LIKE/BINARY di SQLite e non potrebbe concordare
  con quello in memoria — cioè la divergenza che K5 denuncia. Il SQL **restringe** i candidati
  (job non terminali sui volumi coinvolti, marker invece degli item espansi), la memoria decide.
- **Il guard si interroga dopo l'INSERT**, dentro la transazione: SQLite concede il write lock
  alla prima scrittura, non al `BEGIN`, quindi chiedere prima leggerebbe uno snapshot che un
  altro enqueue può ancora cambiare sotto — e due richieste sulla stessa entità atterrerebbero
  entrambe `Pending` su di essa. Con l'indice unico su `SequenceOrder`, chi perde la corsa
  ritenta il numero e *poi* vede il rivale davanti a sé: i due meccanismi si compongono.
- **Niente più 409.** La seconda operazione su un'entità entra in coda `Blocked` /
  `DependencyPending`, con `DependsOnJobId` = il job in conflitto **ultimo in ordine di coda** e
  un messaggio italiano che lo nomina. `EntityAlreadyPendingException` e il ramo `Conflict` del
  controller sono stati **eliminati**: il 400 ora significa «richiesta sbagliata in sé».
- **Un dipendente bloccato non possiede l'entità** e quindi **non scrive overlay** (unico punto:
  `JobDependencies.OwnsItsEntity` davanti a `OverlayWriter.ApplyAsync`, l'aggancio che 9b aveva
  lasciato pronto). Lo scrive quando viene liberato.
- **Rilascio** (`BlockedJobRevaluator` + `JobUnblocker`, condiviso con `RetryAsync`): si
  **richiede il guard** invece di fidarsi del prerequisito (così un solo `DependsOnJobId` basta:
  viene **ripuntato**), si **rinfrescano gli snapshot** e si **prende l'overlay**, tutto nella
  stessa transazione del cambio di stato. Il re-ask conta solo i job **davanti** in coda: senza
  quel vincolo due job sovrapposti si nominano a vicenda e restano bloccati per sempre (trovato
  dal test di ripuntamento, che andava in deadlock).
- **Snapshot freschi** (`JobSnapshotRefresher`, il vero fix del finding 8a): gli item con
  `FileId` si ri-risolvono dal catalogo (l'identità sopravvive a ogni job completato e a ogni
  re-scan dallo step 9a); gli item di cartella e i path di destinazione, che nessuna riga
  identifica, subiscono il **replay** dei move/rename di cartella **intra-volume completati dopo
  l'accodamento**, come riscritture di prefisso. Solo un path effettivamente riscritto viene poi
  verificato contro il catalogo; uno intatto resta com'era. Se qualcosa non si risolve il job
  resta `Blocked` con messaggio esplicito, **mai** `Failed` silenzioso.
- **Prerequisito annullato o fallito** → `JobDependencies.ParkDependentsAsync` nella **stessa
  transazione** dello stato terminale (UPDATE condizionale `WHERE State = Blocked`, così non
  corre contro il concurrency token di nessuno). Mai cascata di cancellazioni.
  *Deviazione documentata dal piano*: `DependencyCancelled` **non** viene rivalutato
  automaticamente. Il piano voleva riportarlo a `Pending` «se il guard è pulito», ma il guard è
  pulito proprio nell'istante in cui il prerequisito viene annullato → il dipendente ripartirebbe
  subito e ricreerebbe in silenzio la cartella che l'utente ha appena deciso di non creare: è lo
  scenario di fallimento del finding 9. La riattivazione è **«Riprova»**, che è la decisione
  dell'utente e azzera la dipendenza morta.
- **Barriera in esecuzione** in `ExecuteJobAsync`, prima del gate offline: se `DependsOnJobId`
  non è `Completed` il job torna `Blocked` senza una syscall. `Blocked` lo terrebbe già fuori
  dalla query del processor — questa è la rete sotto un «Riprova» manuale o una rivalutazione
  andata storta, e un job eseguito fuori ordine corrompe file veri.
- **`SequenceOrder` transazionale + indice unico** (C26): `MAX+1` letto **dentro** la
  transazione di insert, l'indice unico fa da arbitro, retry corto e loggato dentro la stessa
  transazione (una violazione UNIQUE annulla lo statement, non la transazione). Se sia il *nostro*
  indice a essere scattato lo si chiede al database, non al testo d'errore del provider (§3).
  La migration **rinumera prima di indicizzare** (rank per `(SequenceOrder, Id)` via tabella
  temporanea): un DB già in uso può contenere i duplicati che il difetto produceva.
- **`CancelAsync` rivaluta e segnala** (finding 13, ultima metà): un annullamento libera sia
  l'entità sia i byte prenotati, quindi è uno degli «eventi» del §4.
- **UI**: la Coda dice *«In attesa dell'operazione #12»* / *«Dipendenza interrotta: #12»* con il
  numero **linkato** alla riga del prerequisito (riusa il deep link `/queue?job=<id>` di 9b);
  il picker non traduce più il 409 (non esiste) e sulla schermata di conferma dice quante
  operazioni sono state accodate **in attesa** — annunciare un successo liscio per un'operazione
  che l'utente non vedrà accadere è peggio del vecchio 409.
- **Verifica**: xUnit 547 verdi, Vitest 166 verdi, build backend pulita (warnings-as-errors),
  `ng build` ok (restano i 4 warning di budget SCSS, pre-esistenti). Harness sul ferro
  (`D:\Collaudo\A`, coppia *intra*): **10 scenari applicabili, 10 PASS**, incluso il nuovo
  `job-dependencies`. Misura re-scan invariata: primo scan 1,13 s, re-scan 0,93 s su 2 002 file.
La **code review finale** ha trovato due rilievi reali. Il primo è stato corretto: il guard girava
*prima* della transazione, quindi due enqueue simultanei sulla stessa entità potevano non vedersi
e diventare entrambi `Pending` su di essa — ora la domanda si pone dopo l'INSERT, quando il write
lock è nostro, e c'è un test che inietta il rivale nell'istante esatto. Il secondo (target esatti
degli item espansi di un `MoveFolder`) è stato lasciato consapevolmente: sta nei limiti noti qui
sotto. La review ha anche notato che `OperationsController` non logga le eccezioni che converte in
400 (§9 vuole log **e** risalita a video): aggiunto il log sul solo `Enqueue`, l'unico ramo toccato
da questo giro — gli altri controller hanno lo stesso difetto pre-esistente, da chiudere nel WP
frontend/logging.
**Limiti noti e accettati:**
- Il guard non confronta i **target esatti** degli item espansi di un `MoveFolder` pendente: un
  move che atterra sullo stesso path di uno di quei file viene confrontato con la radice della
  cartella, non con la foglia, e passa `Pending`. Lo intercetta l'esecuzione come
  `Blocked(NameCollision)` — recuperabile, e nulla viene mai sovrascritto (`FinalizePartial`
  rifiuta un target esistente). Chiuderlo significherebbe caricare tutti gli item di ogni
  MoveFolder pendente (un cross-volume da 100 000 file) a ogni enqueue: cattivo affare.
- Il replay degli snapshot copre solo i move/rename di cartella **intra-volume**. Una cartella
  spostata su un altro volume non ha reso «stantio» un path: lo ha portato via, e il job resta
  `Blocked` con un messaggio, non riscritto verso un posto in cui non è mai stato.
- Quando il refresh non riesce, il job resta `Blocked` con il `BlockReason` che aveva (spesso
  `None`, con il messaggio a spiegare): `JobBlockReason` non ha un valore per «l'entità non è più
  risolvibile». Aggiungerlo è una modifica di enum + UI, rimandata.

### Fatto nello step 9b (2026-08-16, commit `f2ba90e`…`fcc3c8a`)
Il §5 (proiezione) è implementato: **scrittura**, **lettura**, **pulizia**.
- **Scrittura** — nuovo `FileTracert.Business/Projection/`: `OverlayWriter.ApplyAsync` è
  l'**unico** punto che scrive i `Pending*`, chiamato dall'enqueue **dentro la transazione del
  job** (job e overlay commitano insieme) e dopo il gate offline, qualunque sia l'esito. Legge
  tutto dal job persistito + item, così enqueue e `RetryAsync` condividono un solo percorso.
  Un `MoveFolder` scrive **un solo** overlay, sulla riga della cartella. `DirectoryResolver`
  (era privato in `IndexUpdater`) fa la risalita find-or-create con due significati:
  *materialized* (esiste su disco, post-esecuzione) e *projected* (non esiste ancora → riga
  `IsMaterialized=false, IsPresent=false, PendingCreate` sul job che la creerà).
  **Scelta cambiata rispetto al piano, documentata:** il piano voleva la dir target implicita
  *senza* overlay; così sarebbe invisibile nel Catalogo e un file spostato in una cartella
  inventata nel picker finirebbe in una cartella non mostrabile. Ora porta `PendingCreate` — che
  è ciò che è — e `CreateFolder` usa lo stesso helper. La radice del volume non si timbra mai.
- **FTS** — la colonna `name` è `COALESCE(NULLIF(f.PendingName,''), f.Name)` in tutti e tre i
  percorsi di popolamento (una sola costante condivisa). La colonna `path` resta **path fisico
  della directory + nome proiettato**: un rename-cartella non tocca l'indice (§5), il path
  proiettato è quello *mostrato*, risolto in lettura.
- **Lettura** — `ProjectedPathResolver` (Business): path e **volume** proiettati per un lotto di
  directory, fast-path a `MaterializedPath` con coda vuota, chiusura degli antenati una
  generazione per query, risalita iterativa con visited-set e cap di profondità (un
  `PendingParentId` ciclico ripiega sul path fisico e logga Warning, mai un hang).
  Catalogo: figli per **posizione proiettata**, nome proiettato, visibilità
  `(IsMaterialized AND IsPresent) OR PendingState <> None`, contatori con gli stessi predicati
  delle liste. `CatalogDirDto` guadagna `ProjectedState`, entrambi i DTO `PendingJobId`.
  Ricerca: nome, path e **volume** proiettati, `ProjectedState` reale (spariti i `"None"`
  hardcoded).
- **Pulizia** — overlay azzerato nella **stessa transazione** dello stato terminale:
  `Completed` (dentro il commit di completamento, dopo il fatto fisico), `Cancelled` (dopo il
  save di stato, così un token di concorrenza scattato lascia stare la proiezione), `Failed`
  (e `RetryAsync` lo riscrive). `Blocked` **conserva** l'overlay. Rete di sicurezza all'avvio:
  `OverlayWriter.ReconcileOrphansAsync` in `DatabaseInitializer` azzera gli overlay il cui job
  non esiste più o è terminale.
- **UI** — `ft-projection-badge` condiviso (Catalogo file + cartelle, Ricerca): famiglia ambra
  «in attesa» già usata dalla coda, etichette italiane *In creazione / In rinomina / In
  spostamento*, link a `/queue?job=<id>`; la Coda evidenzia e porta in vista quella riga.
  I modelli TS tipizzano `projectedState` con l'union `EntityPendingState`.
- **Verifica**: xUnit 523 verdi, Vitest 163 verdi, build backend pulita (warnings-as-errors),
  `ng build` ok (restano i 4 warning di budget SCSS, pre-esistenti). Harness sul ferro
  (`D:\Collaudo\A`, coppia *intra*): **9 scenari applicabili, 9 PASS**, inclusi il nuovo
  `projection-overlay` e `rescan-preserves-overlay` (che ora passa dall'enqueue reale).
  Misura re-scan invariata: primo scan 0,70 s, re-scan 0,68 s su 2 002 file.
**Limiti noti e accettati (candidati 9c):**
- `CatalogDirDto.MaterializedPath` resta **fisico** (è l'identità della riga e il bersaglio delle
  operazioni). Per una `PendingCreate` è già il path voluto; per una cartella con rename pendente
  è vecchio → un'operazione accodata al suo interno userebbe il path vecchio. È territorio del
  guard di enqueue unificato (9c), non toccato qui.
- La seconda operazione sulla stessa entità resta un **409** (`EntityAlreadyPendingException`):
  lo sostituisce 9c con `Blocked(DependencyPending)`.

### Fatto nello step 9a (2026-08-15, commit `621be75`…`7f85dff`)
`IsPresent` su `DirectoryNode` (+ migration, backfill a `true`, propagato a Catalogo,
conteggi volume e risoluzione della cartella sorgente all'enqueue); API di merge su
`IBulkIndexWriter` (staging TEMP per lotto, match FRN → path `COLLATE NOCASE`, pass
degli assenti); `PersistAsync` riscritto a lotti con transazioni corte e checkpoint
finale; `DirectoryMerger` in Business (usa finalmente `BulkInsertDirectoriesAsync`);
FTS sincronizzata per lotto (`SyncFilesAsync`/`PruneVolumeAsync`) invece del rebuild
per volume; primo scan mantenuto a bulk insert puro. Scenario harness
`rescan-preserves-overlay` **PASS** sul ferro. Misura: primo scan 1,05 s, re-scan
0,83 s su 2 002 file (prima un re-scan costava quanto un primo scan).
La code review finale ha trovato e corretto un difetto reale: `DirectoryMerger`
azzerava il change tracker, staccando l'entità `Volume` → `LastFullScanUtc`/`LastUsn`
non venivano più persistiti quando una directory tornava presente (`396d506`).
**Limite noto e accettato:** il match per path usa `COLLATE NOCASE`, che in SQLite
piega solo l'ASCII. Un file con nome **non ASCII** di cui cambia solo il *case* sul
disco viene visto come riga nuova (una in più, mai un overlay perso) e vale solo per
il motore a enumerazione — su NTFS risponde prima l'FRN.

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