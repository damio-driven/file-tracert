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
2. **Indicizzazione efficiente e incrementale** (USN Journal su NTFS). Esiste
   davvero **dallo step 14d**: `UsnDeltaApplier` + `UsnSyncWorker` applicano il
   delta senza ricamminare l'MFT, e convergono con la scansione completa.
   Misurato lì, su un volume intero: l'USN **pareggia** il primo scan e **vince
   il re-scan del 17%**; **perde sui sotto-alberi**, perché lo snapshot completo
   cammina tutta l'MFT ignorando il perimetro. Il numero contrario dello step 13
   («l'USN non vince a nessuna scala») era un artefatto di misura.
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
- E2E: **Playwright** sui flussi critici — progetto `tests/e2e` con npm proprio, si lancia con
  `npm test` da lì (compila backend e frontend, poi avvia **un Host vero per test** su un DB
  usa-e-getta). Windows, **non elevato** (il `globalSetup` si rifiuta di partire da elevato),
  fuori CI — come l'harness. Dettagli in `tests/e2e/README.md`.

---

## 3. Architettura backend — layout solution

### Monorepo
Repo unico con backend (.NET) e frontend (Angular) sotto `src/`, il `CLAUDE.md`
nella **root** (descrive entrambi i mondi).

```
filetracert/                         (root repo — qui sta questo CLAUDE.md)
├── src/
│   ├── backend/
│   │   ├── FileTracert.slnx
│   │   ├── Directory.Build.props
│   │   ├── FileTracert.Contracts/    // net10.0          — DTO, enum, port interfaces, SignalR msg. Nessuna dipendenza.
│   │   ├── FileTracert.Data/         // net10.0          — DbContext, entity, IEntityTypeConfiguration, migrations
│   │   ├── FileTracert.Platform/     // net10.0-windows  — Interop Win32 (implementa le port interface)
│   │   ├── FileTracert.Business/     // net10.0          — Service + Orchestrator, queue engine, space ledger
│   │   ├── FileTracert.Host/         // net10.0-windows  — Web API + Windows Service + SignalR hub + BackgroundServices
│   │   └── FileTracert.HardwareSmoke/ // net10.0-windows — harness di collaudo sul ferro (scenari PASS/FAIL su file veri)
│   └── frontend/                     // Angular 21
└── tests/
    ├── FileTracert.Tests/            // net10.0-windows  — xUnit unit + integration (nella solution)
    ├── FileTracert.PoolProbe/        // net10.0          — eseguibile a parte: prova la corsa sui pool SQLite (11i)
    └── e2e/                          // Playwright (node)
```

6 progetti .NET sotto `src/backend` + 3 di test, nessuna over-ingegnerizzazione.
`HardwareSmoke` sta accanto ai progetti di prodotto e non sotto `tests/` perché è un
**eseguibile** che gira sul ferro, non una suite; `PoolProbe` è un eseguibile per lo stesso
motivo (11i: il difetto che prova è chiamare una API *per processo* dentro il test host).

### Grafo dei riferimenti (RISPETTARE)
```
Contracts     → (nessuno)
Data          → Contracts
Platform      → Contracts
Business      → Contracts, Data
Host          → Contracts, Data, Platform, Business
HardwareSmoke → Contracts, Data, Platform, Business, Host
Tests         → Contracts, Data, Platform, Business, Host, HardwareSmoke, PoolProbe
```

### Regole di layering
- `Data` non conosce `Business`. `Business` non conosce `Host`.
- `Contracts` è lo **shared kernel**: DTO, enum, request/response, messaggi
  SignalR **e le port interface verso la piattaforma** (`IVolumeProbe`,
  `IUsnReader`, `IFileMover`, `IDeviceWatcher`) con i loro DTO di scambio.
  Ci stanno anche gli **helper di dominio puri** che più layer devono spellare
  allo stesso modo — oggi `Scanning/ScanPath` (path relativi al volume: normalize,
  join, parent, contenimento), usato da `Business`, `Host` e `Platform`, e
  `Platform/UsnPathResolver` (ricostruzione del path da un FRN di padre), portato
  qui allo step 14d quando lo stesso algoritmo è servito allo snapshot completo e
  al delta incrementale. Non dipendono da nulla: solo tipi BCL, mai un'entità.
- **Tutta** la P/Invoke vive in `Platform`, che **implementa** le port interface
  definite in `Contracts`. `Business` dipende solo da `Contracts` + `Data`
  (mai da `Platform`): resta `net10.0` puro, non vede chiamate native, è
  testabile con mock. `Host` wira le implementazioni `Platform` in DI.
- I pezzi legati a SQLite (FTS5, quirk UPSERT del bulk) sono isolati dietro
  `IFileSearchIndex` e `IBulkIndexWriter`, **implementati in `Data`**.
  *Dove vive l'interfaccia lo decide la sua firma* (chiuso allo step 11f):
  `IFileSearchIndex` parla in **id e DTO** e sta in `Contracts/Search`;
  `IBulkIndexWriter` parla in **entità EF** (`FileEntry`, `DirectoryNode`) e sta
  in `Data/Indexing` — portarlo in `Contracts` ci trascinerebbe il modello, cioè
  romperebbe la regola «Contracts non dipende da nulla» per rispettarne la
  lettera. Nessun consumatore ci perde: `Business` referenzia `Data` comunque.
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
  chiamate UI — **oppure** in query string `?access_token=…`, ma **solo** su
  `/hubs/*` (§7: l'handshake WebSocket del browser non può mettere header custom).
  Single-user, sufficiente.
- BackgroundServices, nell'ordine di registrazione di `Program.cs` (che è anche
  l'ordine di stop, al contrario — vedi 11c e 14b): `LogFlushService`,
  `ReadCancellationLifetime`, `VolumeSyncWorker`, `DeviceWatcherWorker`,
  `ScanWorker`, `UsnSyncWorker`, `QueueProcessorWorker`, `LogRetentionWorker`,
  `WalCheckpointWorker`. I primi due non sono worker di dominio: drenano la coda
  dei log e accendono il segnale di annullamento delle letture, e stanno in cima
  proprio perché devono fermarsi per ultimi.

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
  con `FSCTL_READ_USN_JOURNAL` persistendo **la coppia** `LastUsn` +
  `UsnJournalId` — una posizione senza l'istanza a cui appartiene non è un
  cursore (§6, step 14d). Richiede admin + NTFS; senza privilegi l'handle di
  volume viene rifiutato e il volume ripiega sull'enumerazione, con log e
  Notification.
  - **Snapshot completo** (`ScanService`): cammina l'MFT e **merge** contro il
    catalogo. I record MFT non portano dimensione né date: le ricompra
    `IFileMetadataReader`, una `stat` per file (fase `ReadingMetadata`).
  - **Delta** (`UsnDeltaApplier`, guidato da `UsnSyncWorker` ogni 30 s, un volume
    alla volta): riusa gli **stessi** pezzi dello scan — `MergeScannedFilesAsync`
    per l'upsert (match per FRN), `DirectoryMerger` per l'albero, `ScanPerimeter`
    + `FileFilter` per il verdetto. L'unica cosa che **non** prende in prestito è
    il pass degli assenti: «non nominato da questo delta» non significa «non c'è
    sul disco». L'assenza si scrive **solo contro una prova** del giornale.
    Cursore scritto per ultimo, in transazione propria: un crash costa un delta
    ripetuto, mai uno saltato. Giornale ricreato o posizione sorpassata → il
    cursore viene lasciato cadere e il volume torna a `ScanWorker`.
- **exFAT/FAT32 → enumerazione** delle sole cartelle sorvegliate, poi lo **stesso
  merge** dello snapshot completo. *Non* c'è un diff su size/timestamp: il merge
  riscrive i fatti fisici della riga ritrovata (match per FRN, in subordine per
  `(DirectoryId, Name) COLLATE NOCASE`) e marca assente ciò che non ha rivisto.
  Niente journal, quindi **nessun percorso incrementale**: qui `LastUsn` e
  `UsnJournalId` restano nulli di proposito.
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
`CreateFolder`, `RenameFile`, `RenameFolder`, `MoveFile`, `MoveFolder`,
`CopyFile`, `CopyFolder` *(le due Copy dallo step 15a)*.
- Rename e move **intra-volume** (anche cartelle) = metadati, **istantanei**,
  O(1), nessuna prenotazione spazio.
- **Una Copy non è mai gratis, nemmeno intra-volume** *(step 15a)*: scrive un
  secondo insieme di byte sul volume da cui legge, quindi prenota spazio e passa
  **sempre** dalla macchina a stati `Pending → SpaceReserved → Copying →
  Verifying → Completed`. Salta `DeletingSource` e basta — `JobState` non cambia:
  uno stato in meno percorso non è uno stato nuovo. `FreedBytesSource = 0`: il
  sorgente resta dov'è. **`IsIntraVolume` ha smesso di essere sinonimo di
  «gratis»**, e la domanda vera («questo job consuma byte sul target?») si chiede
  a `RequiredBytesTarget > 0`, in `SpaceLedger.ReservationFor` e — stessa
  guardia, obbligatoriamente — in `SpaceCheck.EvaluateHardAsync`.
- **Move cartella cross-volume** e **Copy cartella** (a qualunque volume) sono i
  casi che esplodono in molti `OperationJobItems`. Per la copia non c'è
  scorciatoia intra-volume: un move di cartella dentro un volume è una syscall di
  rename, una copia deve duplicare ogni file.
- `CreateFolder` = mkdir banale in esecuzione; in proiezione la cartella "esiste
  già" (riga `Directories` con `IsMaterialized = false`).
- **Una Copy crea un'entità NUOVA**, quindi §5 non si può esprimere con i campi
  `Pending*`: non esiste una riga alla destinazione che possa portarli. La riga
  si crea **prima** del file (`IsMaterialized = false`, `IsPresent = false`,
  `PendingCreate`), che è il mestiere che `IsMaterialized` fa da sempre sulle
  directory. Il sorgente **non viene toccato**: promettergli un cambiamento
  sarebbe una bugia a video. Alla conclusione `IndexUpdater` la promuove — e lo fa
  **prima** di `OverlayWriter.ClearForJobAsync`, che cancella le righe non
  materializzate di quel job: l'ordine opposto cancellerebbe dal catalogo il file
  appena scritto, nella stessa transazione che dichiara il job completo.
- **L'unico punto in cui il no-hard-delete di §6 non vale**: annullando o
  facendo fallire una Copy, la riga proiettata alla destinazione si **cancella**,
  non si azzera. Quella riga non ha mai descritto niente su nessun disco.
- **Una Copy non rivendica il proprio sorgente** nel guard di enqueue: lo
  *legge*. `PathClaim` ha quindi tre generi (`Source` / `Target` / `Read`), e la
  regola guadagna un congiunto: un `Target` che si sovrappone a un `Read` è
  conflitto. Due `Read` non lo sono mai — due copie dello stesso file verso
  destinazioni diverse devono partire entrambe.

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
  distinguere "non c'è più sul disco" da "escluso dal filtro". **I due non si
  sostituiscono mai a vicenda** *(decisione di prodotto del 2026-08-19, step 11g)*:
  - `IsPresent = false` significa **soltanto** «la scansione l'ha cercato e non
    l'ha trovato sul disco». Nient'altro lo scrive.
  - `IsIncluded = false` significa «fuori da ciò che l'utente ha chiesto di
    indicizzare»: fuori dai watched root attivi, sotto una cartella esclusa per
    attributi o path, o fuori dall'allow-list di estensioni. Non dice nulla sul
    disco ed è **reversibile senza ri-scansione** (§4).
  - Quindi: una decisione di perimetro non tocca `IsPresent`, un'assenza non tocca
    `IsIncluded`. **Ciò che la scansione non ha guardato non è assente.**
  - Le **directory** non hanno `IsIncluded`: una cartella che esiste sul disco
    esiste, anche se non se ne indicizza il contenuto. Una cartella fuori
    perimetro resta quindi `IsPresent = true` e visibile nel Catalogo, con dentro
    solo ciò che è incluso.
  - **Una riga ricorda *perché* è esclusa** *(step 11h, quarta causa allo step 16)*:
    quattro flag su `Files` — `ExcludedByType` (estensione fuori allow-list),
    `ExcludedByRoot` (nessun watched root **attivo** la governa), `ExcludedByPath`
    (un segmento del suo path è in `ExcludedPaths`), `ExcludedByScan` (**gli
    attributi**: Hidden/System, propri o di una cartella sopra di lei). Flag e non
    un enum: le cause si **sommano** (un `.tmp` in `AppData` dentro una cartella
    nascosta è escluso tre volte) e ognuna deve poter essere spenta dal suo
    proprietario. Invariante mantenuta da ogni writer:
    `IsIncluded == !(ExcludedByType || ExcludedByRoot || ExcludedByScan ||
    ExcludedByPath)`; `IsIncluded` resta una colonna propria perché è quella che
    leggono Catalogo, FTS e gli indici covering.
    **Chi disfa cosa:** `FilterReconciler` ricalcola le prime **tre** (sono fatti
    delle *impostazioni*, e il segmento di path è derivabile dal
    `MaterializedPath` senza leggere il disco) e **non nomina mai** la quarta —
    nessuna impostazione sa se quella cartella è ancora nascosta. Solo il merge di
    una scansione azzera tutte e quattro.
    **Perché `ExcludedByPath` è una colonna a sé** *(step 16)*: finché stava dentro
    `ExcludedByScan`, aggiungere un segmento a `ExcludedPaths` **non escludeva
    nulla** di ciò che era già a catalogo — la riconciliazione non poteva toccare
    quella colonna senza riammettere anche il contenuto delle cartelle nascoste. La
    prima causa era ostaggio della seconda. Quando **entrambe** le regole rifiutano
    lo stesso item si registrano **entrambe**: non c'è una precedenza da scegliere,
    e sceglierne una qualunque fa rientrare la riga quando l'utente disfa la causa
    vincente. Il nome `ExcludedByScan` è rimasto per non pagare un rename di colonna
    su 742 675 righe: oggi significa **soltanto** attributi.
- Configurazioni EF in classi `IEntityTypeConfiguration` separate. **Nessuna
  data annotation sulle entità.**

### Dominio Catalogo

**Volumes**
`Id` · `VolumeGuid` (unique) · `SerialNumber?` · `Label?` · `FileSystem` ·
`IsRemovable` · `PhysicalDiskId?` · `LastDriveLetter?` · `CapacityBytes` ·
`FreeBytesLastKnown` · `LastSeenUtc` · `IsOnline` · `ScanEngine` ·
`Kind` (VolumeKind) · `IsCatalogable` (default da Kind; override utente preservato) ·
`LastUsn?` · `UsnJournalId?` (l'istanza del giornale a cui `LastUsn` appartiene: una posizione
senza il suo id non è un cursore) · `LastFullScanUtc?` + audit.

**WatchedRoots**
`Id` · `VolumeId`→Volumes · `RelativePath` · `IsActive` · `FilterOverrideJson?`
+ audit.

**Directories** *(step 9b: `Pending*` scritti all'enqueue da `OverlayWriter`; una riga
`PendingCreate` ha `IsMaterialized = false` e `IsPresent = false` finché il job non la crea)*
`Id` · `VolumeId` · `ParentId?`→self · `Name` · `MaterializedPath` (denormalizzato,
aggiornato in cascata sui rename) · `UsnFileRef?` · `IsMaterialized` · `IsPresent`
(default `true`; stessa semantica di `Files.IsPresent` — la scansione l'ha cercata e
non l'ha trovata sul disco, mai un delete; una cartella **fuori perimetro** non viene
mai marcata così, e non esiste un `IsIncluded` sulle directory — step 11g) ·
`PendingName?` · `PendingParentId?` ·
`PendingState` · `PendingJobId?` + audit.

**Files**
`Id` · `VolumeId` · `DirectoryId`→Directories · `Name` · `Extension` (lower) ·
`Category` (derivata e persistita) · `SizeBytes` · `CreatedUtc`/`ModifiedUtc`
(le date **del file**; attenzione: sono i nomi delle *colonne*, le proprietà CLR
si chiamano `FileCreatedUtc`/`FileModifiedUtc` e le due colonne di audit stanno
sotto `RowCreatedUtc`/`RowUpdatedUtc` — `FileEntry` è l'unica entità con questo
scarto, e serve a non avere due `CreatedUtc` nella stessa classe) ·
`Attributes` · `UsnFileRef?` · `QuickHash?` (size + primi/ultimi KB)
· `Hash?` (full, lazy) · `IsIncluded` · `ExcludedByType` · `ExcludedByRoot` ·
`ExcludedByScan` · `ExcludedByPath` (le **quattro** cause di §6 «Convenzioni
trasversali» — le prime tre da 11h, la quarta da 16, che ha anche ristretto
`ExcludedByScan` ai soli **attributi**) ·
`IsMaterialized` (default `true`; **il file esiste sul disco**. Falso solo per la riga che
una **Copy** in coda proietta alla destinazione — step 15a: una copia è l'unica operazione il
cui risultato è un'entità *nuova*, quindi non esiste una riga i cui `Pending*` possano
portarla, ed è lo stesso mestiere che `IsMaterialized` fa da sempre sulle directory. **Non è
un sinonimo di `IsPresent`**: `IsPresent = false` dice «una scansione l'ha cercato e non l'ha
trovato», `IsMaterialized = false` dice «nessuno l'ha ancora creato», cioè nessuna scansione
ha mai guardato) ·
`IsPresent` · `LastIndexedUtc` ·
`PendingName?` · `PendingDirectoryId?` · `PendingState` · `PendingJobId?` + audit.
Indici, tutti in `FileEntryConfiguration` — sono nove e ognuno ha un motivo
scritto accanto, quindi **non allargarne uno senza leggerlo**:
`(DirectoryId, PendingDirectoryId, IsIncluded, IsPresent)` e
`(PendingDirectoryId, IsIncluded, IsPresent)` (i due FK **covering** di 11e, che
*sostituiscono* quelli stretti) · `(VolumeId, DirectoryId)` (la ricerca del merge:
il suo piano è sotto guardia, `ScanMergePlanTests`) ·
`(VolumeId, IsIncluded, IsPresent, SizeBytes)` (i contatori di Volumi/Dashboard,
14c) · `Extension` · `Category` · `SizeBytes` · `FileModifiedUtc` ·
`(VolumeId, UsnFileRef)` **unique** filtrato.

**FileSearchIndex** — tabella virtuale **FTS5** (DB principale), **tre** colonne:
`name` (nome proiettato) + `path` (path relativo completo) + `tags` (i token
sintetici di categoria e volume introdotti da 14a — non parole, mai cercabili
dall'utente: ogni MATCH è scopato a `{name}`/`{name path}` e `bm25` dà loro peso
0). `rowid = Files.Id`, tokenizer `unicode61 remove_diacritics 2` con separatori
`. _ -` (prefix query supportate). La DDL sta in un posto solo,
`Data/Search/FileSearchIndexSchema`, che è ciò che esegue la migration. **Sync
esplicito** via `IFileSearchIndex` (no trigger): popolata negli stessi batch del
`BulkIndexWriter`; `RebuildAsync` per il backfill, con la guardia
`CountEntriesAsync` che all'avvio confronta le voci dell'indice con le righe che
ci **devono** stare (a senso unico: corto vuol dire ricostruisci, lungo no). La
ricerca supporta scope **solo nome** (colonna `name`) o **path completo**
(entrambe), scelto dall'utente in UI. SQLite-specific, dietro `IFileSearchIndex`.

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

**AppSettings** — singleton tipizzato: `DefaultExtensionFilter`, `ExcludedPaths`,
`ApiToken` (loopback), `SpaceMarginPercent`, `MinimumLogLevel`,
`LogRetentionDays` (14) e `LogMaxRows` (500 000) + audit. Dei due tetti sui log
è **`LogMaxRows` a legare per primo**, misurato sul catalogo vero (step 13).

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
- `JobType` { CreateFolder, RenameFile, RenameFolder, MoveFile, MoveFolder, CopyFile, CopyFolder }
  (le due Copy dallo step 15a: **due tipi, non uno**, perché §5 elenca le operazioni per *entità*
  e ogni switch del backend è scritto così)
- `JobState` { Pending, SpaceReserved, Copying, Verifying, DeletingSource, Completed, Blocked, Failed, Cancelled }
- `JobBlockReason` { None, InsufficientSpace, TargetVolumeOffline, SourceVolumeOffline, NameCollision, DependencyPending, DependencyCancelled }
- `JobItemState` { Pending, Copying, Copied, Verified, Done, Failed, Skipped }
- `EntityPendingState` { None, PendingCreate, PendingRename, PendingMove }

---

## 7. API surface (linee guida)

- **Paging/sort/filtro lato server ovunque** (milioni di righe). Pattern condiviso
  `PagedRequest` / `PagedResult<T>` (skip/take o cursore, `sortBy`).
  **Un'eccezione nota, non chiusa**: la lista delle **sottocartelle** del Catalogo
  non è paginata (`CatalogChildrenDto`). Chiuderla è un cambio di contratto + UI,
  quindi una decisione di prodotto — vedi la roadmap.
- **Endpoint preview/dry-run**: `POST /operations/preview` esegue la logica del
  ledger e ritorna la fattibilità **senza creare il job** (riusa il motore
  dell'enqueue). Serve alla UI per dire "ci sta / non ci sta / stima offline"
  prima di confermare.
- DTO con **freschezza del dato**: `lastSeenUtc`, `dataIsLive`, `estimateIsLive`
  dove i dati provengono da un volume offline. **Un flag solo, non due**:
  `isStale` era la negazione letterale di `dataIsLive` e non lo leggeva nessuno —
  tolto allo step 11f (K13). `estimateIsLive` significa «questo numero l'ha
  risposto il **dispositivo** adesso», non «il catalogo crede il volume online»:
  la differenza è di 11b ed è il punto di quel giro.
- Fattibilità come **oggetto**, non booleano:
  `{ requiredBytes, reservedBytes, availableEstimateBytes, deficitBytes,
  estimateIsLive, blockingVolumeId, feasible, marginBytes }`. `marginBytes` è
  separato da `requiredBytes` di proposito (11b): il richiesto resta la dimensione
  onesta dell'operazione, e il deficit — che il margine lo **contiene** — si può
  spiegare a video invece di sembrare un difetto dell'app.
- Navigazione catalogo = **albero lazy** (figli on-demand), distinta dalla ricerca.

### SignalR hub — messaggi tipizzati
Hub unico su **`/hubs/events`**, unidirezionale server → client, broadcast (single-user
su loopback). Protetto dal token come `/api`: header, **oppure** `?access_token=…` in
query string — solo su `/hubs/*`, perché l'handshake WebSocket del browser non può
mettere header custom.
`VolumeStatusChanged`, `JobProgress`, `JobStateChanged`, `ScanProgress`,
`ProjectionChanged` (per refresh del Catalogo/Ricerca quando l'overlay cambia) e
**`NotificationRaised`** (aggiunto allo step 10b: è ciò che permette alla campanella di
spegnere il poll).
Payload **snelli** (id + i campi che cambiano; il resto si rilegge con la GET), enum
serializzati **come stringhe**, date **UTC**. Il contratto sta in `Contracts/Realtime/`
(`IRealtimePublisher` + record); l'hub e l'implementazione della port stanno **solo** in
`Host/Realtime/`.

---

## 8. Architettura frontend

### Struttura (standalone-first)
```
src/app/
├── core/            // api, config, http (interceptor + traduzione errori), logging,
│                    // models, realtime (client SignalR + bridge), toast
├── shared/          // componenti riutilizzabili, pipe, date, selection, validation
├── styles/          // _tokens.scss, _mixins.scss, _utilities.scss,
│                    // _components.scss (.ft-*), _data-views.scss
└── features/        // feature lazy-loaded per dominio
    ├── dashboard/
    ├── volumes/
    ├── setup/
    ├── catalog/
    ├── search/
    ├── queue/
    ├── logs/
    ├── notifications/   // solo store (la campanella vive in shared/components)
    └── scans/           // solo store (l'indicatore vive nella shell)
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
- `_components.scss` — le classi condivise vere: `.ft-btn` (+ `--primary`,
  `--ghost`), `.ft-modal*` (la shell modale estratta da 11d/11f), `.ft-skel`
  (skeleton), `.ft-table` (+ `--kv`), `.ft-h1`, `.ft-sub`, `.ft-stale`, `.ft-rise`.
  **`ft-panel`, `ft-pill` e `ft-card` non sono classi**: sono componenti Angular
  in `shared/components/`, e si usano come elementi (`<ft-panel>`). Il mixin
  `panel`/`pill` in `_mixins.scss` è ciò che li veste.
- **Layout in flexbox**. Liste enormi (Catalogo/Ricerca) → **CDK Virtual Scroll**.
- Tipografia: IBM Plex Sans + IBM Plex Mono (per path, GUID, valori tecnici).
- **Tema dark**. Vedi mockup di riferimento (`src/frontend/filetracert-mockup.html`).
- ⚠️ **Usare la skill `impeccable` per la realizzazione del frontend.**

### Schermate (7 in barra + 1 fase 2)
Sette route in `app.routes.ts`, tutte raggiungibili dalla barra di navigazione.
1. **Dashboard** — card riassuntive + tabella volumi con stato live/stale.
2. **Volumi** — dettaglio per volume (GUID, serial, FS, disco fisico, cartelle,
   filtri, indice/USN), azioni (ri-scansiona, modifica cartelle/filtri).
3. **Setup** — cartelle monitorate (albero del filesystem) e filtro dei tipi,
   globale o per singolo root. È da qui che si allarga o si restringe il
   perimetro, quindi è la schermata che innesca le riconciliazioni di 11g/11h.
4. **Catalogo** — browser ad albero lazy che **funziona offline**; selezione
   multipla → accoda operazione; badge di stato proiettato.
5. **Ricerca** — FTS5 sul nome proiettato + filtri (categoria, data, volume,
   solo-online); risultati con volume e stato. **La dimensione non ha un
   controllo**: `SizeBytesMin/Max` esistono nell'API e nello store, non a video —
   vedi la roadmap.
6. **Coda** — tabella job con stato, progress, colonna fattibilità (delta
   mancante, volume in attesa).
7. **Log** — lista del DB log dedicato con filtri (livello, categoria, ricerca
   testuale) e cambio a caldo del `MinimumLogLevel`.
8. **Regole** — fase 2.

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

0. **Scaffold** monorepo + solution (riferimenti, TFM, file di supporto, scaffold
   Angular). Skeleton-only, deve compilare a vuoto. *(Il `TASK-step0-scaffold.md`
   che questa riga citava non è nel repo: lo step è chiuso da tempo e il layout
   vero è quello di §3.)*
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
- ~~**Operazione Copy** (oltre a Move)~~ — **fatta allo step 15a** (2026-08-26).
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
- Mockup UI navigabile: `src/frontend/filetracert-mockup.html`.
- I task per step vivono nella root come `TASK-stepNN*.md`; quello dello step 0
  non esiste più.
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

**Dove siamo.** L'**MVP è chiuso** (tutte e dodici le voci del §10, con lo step 12).
Il prodotto è **installato come servizio** e gira su un catalogo vero da 742 033 file
(step 13). Gli step **14a–14e** hanno pagato i difetti che quel catalogo ha reso
visibili. Lo step **15a** è il primo lavoro di **fase 2**: l'operazione **Copy**; il **15b** ha chiuso i
due difetti che l'utente ha scelto per primi (voci A1 e A6); il **16** le voci **A2 e A3**, cioè le
decisioni di perimetro che non raggiungevano le righe già a catalogo.
**Non c'è lavoro obbligatorio aperto**: tutto ciò che segue è debito datato,
decisione di prodotto o fase 2.

**Una cosa sola resta dovuta allo step 16**: una passata **elevata** dell'harness, che trasforma i
6 SKIP in PASS e firma A3 sul giornale USN vero. Senza elevazione il volume non si apre e la
scansione ripiega sull'enumerazione, quindi quegli scenari fanno SKIP invece di passare per la
strada sbagliata.

**Il servizio installato è aggiornato a 15b** (2026-09-02) — vedi «Deploy di 15b» qui sotto.

### A. Difetti veri, in ordine di fastidio *(nessuno è MVP)*
1. ~~**La corsa dell'auto-selezione su Volumi**~~ — **chiusa allo step 15b** (2026-08-27): lo
   store registra ciò che è stato *chiesto* (`selectedId`, sincrono) e scarta ogni risposta che
   non è più quella.
2. ~~**`ExcludedPaths` non ha una riconciliazione propria**~~ — **chiusa allo step 16**
   (2026-09-03). E la voce descriveva la metà innocua: quella che mordeva era il verso
   opposto, **aggiungere** un segmento, che non escludeva **niente** di ciò che era già a
   catalogo mentre la schermata diceva che era tutto a posto. Vedi il paragrafo dello step.
3. ~~**Una cartella che *diventa* nascosta non ri-esclude il proprio sottoalbero già a
   catalogo**~~ — **chiusa allo step 16 per il sottoalbero**, con tre residui dichiarati che
   vanno letti insieme (il traffico di scrittura che disfa l'esclusione, la crescita dentro il
   sottoalbero, e il caso stesso-tick): stanno nei limiti di `TASK-step16` e accanto al
   `KNOWN HOLE` di `UsnDeltaApplier`. **Il verso opposto resta aperto e non è chiudibile qui**:
   una cartella che *smette* di essere nascosta non è rilevabile dal delta (14d) — un record di
   cartella che passa il filtro e non ha riga è indistinguibile da una cartella nuova, e i file
   dentro non generano record propri. Serve una scansione.
4. **L'USN perde sui sotto-alberi** — `FSCTL_ENUM_USN_DATA` cammina tutta l'MFT
   ignorando il perimetro (misurato in 14d). Scegliere il motore per rapporto fra
   sotto-albero e MFT è il candidato ovvio.
5. **Classificazione Cloud non affidabile** (§11, debito datato allo step 6.7). Coperto
   dall'esclusione manuale, che funziona.
6. ~~**Un job ripreso da un checkpoint non ri-controlla lo spazio**~~ — **chiuso allo step 15b**
   (2026-08-27). Chiedeva quanto **manca**, non la domanda intera: vedi il paragrafo dello step.
7. **C32** — la risposta di enqueue porta `SourceVolumeLabel`/`TargetVolumeLabel` null
   (quel percorso non fa `Include` dei volumi). Innocuo: il picker la scarta.
8. **P1** — hard link nello snapshot MFT: `nodes[frn]` è last-write-wins, quindi
   sopravvive un solo path. Mai indagato.

### B. Decisioni di prodotto, non difetti *(vedi «Cosa resta all'umano»)*
- **Paging delle sottocartelle del Catalogo** — oggi illimitato (E5, §7). Chiuderlo
  cambia `CatalogChildrenDto` e la schermata.
- **Filtro dimensione in Ricerca** — `SizeBytesMin/Max` esistono nell'API e nello store,
  non hanno un controllo a video (§8).
- ~~**Accendere l'incrementale su `C:`**~~ — **decisione presa dall'utente il 2026-08-27: NO.**
  I 739 421 file di `C:` continueranno a dipendere da una scansione completa, e va bene così:
  accenderlo costa una ri-scansione esplicita del volume, cioè una camminata completa dell'MFT
  per indicizzare tre sottoalberi — il caso che 14d ha misurato come **perdente** per l'USN.
  L'utente ha detto che non gli servirà mai scansionare un volume così grosso; resta buono solo
  come prova assoluta, se un giorno la si vorrà. `D:` ha il cursore acceso dal 2026-08-25 ed è
  l'unico volume su cui il percorso incrementale gira davvero (verificato sul catalogo vivo il
  2026-08-27: `Windows-SSD` e i due volumi offline hanno `UsnJournalId` a `NULL`).
- **Il riavvio vero della macchina** non è mai stato fatto: è l'unica cosa che manca
  alla prova dell'avvio automatico del servizio (step 13). Vale ancora dopo il deploy di 15b.

### C. Igiene nota, tollerata
- **`%TEMP%` accumula `ft-test-*-logs.db`**: `WebApplicationFactory.Dispose` non ferma
  l'host, quindi il drain dei log non avviene (11i). Costa una `StopAsync` sul factory.
- **`ScenarioEnvironment` pulisce ancora *tutti* i pool SQLite** — innocuo (processo
  suo, single-threaded) ed è l'unica eccezione ammessa dal guard di 11i.
- **`IndexUpdater` valuta `IsIncluded` col filtro di default** quando la destinazione di
  un rename/move è fuori da ogni root attivo (11g): si ripara alla scansione successiva.
- **Gate offline e guard di enqueue girano *prima* del refresh degli snapshot** (11a):
  un job può restare parcheggiato per un'operazione eseguibile finché non si fa
  «Riprova». Riordinarli tocca la macchina della coda.
- **Gli E2E non girano da elevato** (per scelta, 12a), quindi non provano il percorso
  USN; l'harness sì, dal collaudo del 2026-08-21.

### D. Fase 2
Il §11, nell'ordine che l'utente deciderà.

### Storia — cosa è stato chiuso, e dove leggerlo
I paragrafi «Fatto nello step N» qui sotto sono la memoria del progetto e restano.
In ordine: **WP3** (perdita dati), **WP1** (crash-safe), **WP2** (offline gate),
**giro date/UTC** (#12, #11, C31) · **9a** merge dello scan e transazioni corte ·
**9b** proiezione · **9c** dipendenze fra job (WP4) · **10a** device watcher ·
**10b** hub SignalR · **10c** il frontend ascolta · **11a** indice/ricerca (WP5) ·
**11b** spazio (WP6) · **11c** logging/shutdown (WP8) · **11d** frontend/UX (WP7) ·
**11e** efficienza (WP9) · **11f** cleanup e layering (WP10) · **11g** esclusione vs
assenza · **11h** perché una riga è esclusa · **11i** le corse sui pool SQLite ·
**12a/12b** end-to-end (con cui l'MVP si chiude) · **13** servizio installato e catalogo
vero · **14a** filtro categoria della Ricerca · **14b** annullabilità delle query ·
**14c** le due schermate Volumi · **14d** l'USN incrementale · **14e** l'allineamento del
brief · **15a** l'operazione Copy · **15b** la ripresa che ricontrolla lo spazio e la corsa di
Volumi · **16** le decisioni di perimetro che raggiungono le righe già a catalogo (la quarta causa
di esclusione, e il sottoalbero che il delta esclude). I finding della review del 2026-07-12 stanno in
`CODE-REVIEW-HANDOFF.md`, che porta in cima il proprio stato aggiornato.

### Deploy sul catalogo reale (2026-08-25, build `49619b5`)
**Il servizio installato è passato da 14a a 14e**, e i numeri di 14c sono ora provati sul
catalogo vero invece che su una copia.

**Il brief diceva il falso anche qui, e l'ha scoperto la ricognizione**: ogni paragrafo da
14a a 14d chiude con «il servizio installato non è stato aggiornato». Vero quando è stato
scritto — ma **14a è stato distribuito dall'utente** il 24/08 alle 17:35, con tanto di
`filetracert.db.pre14a` accanto al database. Lo dice la sonda: l'ultima migration applicata
era `20260824131723_AddFtsTagsColumn`. Quei paragrafi restano come sono: sono storia, ed
erano veri all'ora in cui sono stati scritti.

**Stato di partenza, verificato prima di toccare qualcosa**: servizio `Stopped`, porta
libera, **0 job non terminali e 0 overlay pendenti** — cioè niente da riprendere a metà.
Backup: snapshot dei 76,4 MB di `Program Files` più `filetracert.db.pre14e`, sha256
identico all'originale.

**Due migration, entrambe additive**: `VolumeAggregateCoveringIndex` (14c) e
`AddVolumeUsnJournalId` (14d). Publish + install: **24 s** in tutto. **Nessun rebuild FTS**
— l'indice era già a 742 033 voci, e la guardia a senso unico di 14a ha taciuto, che è
esattamente ciò che quella guardia deve fare al secondo avvio su un database sano.

**Verificato sul ferro**: servizio `Running`/`Automatic`, in ascolto **solo** su `127.0.0.1`
e `::1`, UI 200 col token nel `<meta>`, **401** senza token, catalogo intatto (742 033 file ·
114 132 directory · 742 033 righe FTS · 22 volumi · 468 861 553 711 byte), il piano della
lista volumi è `SCAN Files USING COVERING INDEX
IX_Files_VolumeId_IsIncluded_IsPresent_SizeBytes`, e **zero Error** nel log dall'avvio (gli
unici Warning sono gli `FirstOrDefault senza OrderBy` di EF, pre-esistenti).

**I tempi, attraverso l'API, mediana di 5 a caldo** — «step 13» è il soak sul build vecchio,
«oggi» è il servizio installato adesso:

| endpoint | step 13 | oggi |
|---|---|---|
| `GET /api/dashboard` | 373 ms | **119 ms** |
| **`GET /api/volumes`** | **1 571 ms** | **45 ms** |
| **`GET /api/volumes/{id}`** (739 421 file) | **1 768 ms** | **176 ms** |

Sono gli stessi ordini di grandezza misurati da 14c sulla copia (106 / 37 / 139 ms), il che
è la conferma che quella misura non era un artefatto della copia.

**Cosa NON è stato acceso**: i quattro volumi con un cursore USN hanno `UsnJournalId` a
`NULL` (il backfill della migration), e il pre-filtro di `UsnSyncWorker` lo pretende non
nullo. Il worker gira quindi **a vuoto**, e nessuna camminata dell'MFT parte a sorpresa. Per
accenderlo serve una ri-scansione esplicita — vedi la roadmap.

#### Il worker incrementale ha girato per la prima volta su dati veri
Acceso **solo su `D:`** (decisione dell'utente): ri-scansione esplicita dalla API, **2,2 s**,
motore **USN** (righe `NtfsUsnReader` nel log — il servizio gira elevato e il giornale c'è).
Lo scan ha anche recuperato il drift accumulato da luglio: **2 610 → 3 251 file**, 80 directory
nuove, **5 file marcati assenti** — mai cancellati.

Da lì `UsnJournalId` è scritto (`134275375767659020`) e il pre-filtro è soddisfatto. `C:` e i
due `E:` restano inelegibili con `UsnJournalId` a `NULL`, quindi **fermi**.

**Un delta vuoto non logga nulla** — è `UsnSyncStatus.UpToDate`, e `CheckpointAsync` non scrive
nemmeno la riga quando `NextUsn` non si è mosso. Il silenzio è il comportamento giusto, e va
saputo: *(nota metodologica, costata un'inferenza sbagliata)* la colonna `UpdatedUtc` del volume
**non** è la prova che il worker gira — la muove `VolumeSyncCycle` ogni 60 s.

La prova vera è stata un file vero sotto un root sorvegliato, creato e poi cancellato:

| | record di giornale | esito | ritardo |
|---|---|---|---|
| creazione | 3 | **1 indexed** | 25 s |
| cancellazione | 1 | **1 absent** | 13 s |

In mezzo, la Ricerca lo trovava per nome con path, categoria `Document` e 52 byte — cioè il
**delta ha sincronizzato anche l'FTS**, non solo la tabella. Dopo, non lo trova più.

E la riga finale è la semantica di 11g vista sul catalogo reale: **`IsPresent = 0` con
`IsIncluded = 1`** e le tre cause di esclusione tutte a zero. Il file non c'è più sul disco e
questo è tutto ciò che il catalogo afferma; non è mai stato «escluso», e la riga — con il suo
FRN — è ancora lì. Cursore avanzato da `101092976` a `101093664`.

**Residuo dichiarato**: una riga assente in più nel catalogo (6 in tutto su D:), esattamente
come per qualunque file che l'utente cancelli davvero. Sparisce a una ri-scansione di `D:`.

**Limiti noti:** il **riavvio della macchina** non è stato fatto, quindi l'avvio automatico
resta provato solo da `sc start`; il `MinimumLogLevel` installato è **`Trace`**, e a quel
livello il tetto che lega è `LogMaxRows` = 500 000 righe (≈184 MB, misurato allo step 13) —
oggi il DB dei log sta a 3,9 MB; e **su `C:` il percorso incrementale non è ancora acceso**,
cioè i 739 421 file che contano continuano a dipendere da una scansione completa.

### Fatto nello step 16 (2026-09-03, commit `cbfa973`…`f927b0d`)
**Una decisione di perimetro raggiunge le righe che il catalogo ha già.** Chiude le voci **A2** e
**A3** della roadmap, che erano lo stesso difetto visto da due lati: A2 il lato *impostazioni*
(l'utente esclude un segmento di path e non succede niente), A3 il lato *disco* (una cartella
diventa nascosta e il suo sottoalbero resta incluso). Una migration additiva, nessun cambiamento
di contratto, frontend non toccato.

#### 1. La quarta causa (A2), e perché il difetto era peggio di come la roadmap lo scriveva
La roadmap descriveva la metà innocua — togliere un segmento dice onestamente `NeedsScan`. **La
metà che mordeva era l'altra, e non era scritta da nessuna parte**: `FilterReconciler` non nomina
mai `ExcludedByScan` (di proposito, 11h), ma quella colonna sommava **due fatti di natura
diversa** — gli attributi e il segmento di path. Quindi **aggiungere** un segmento a
`ExcludedPaths` non escludeva **niente** di ciò che era già a catalogo, `FilterWidened` tornava
`false` e la schermata Setup annunciava un riallineamento: le righe restavano navigabili nel
Catalogo e **trovabili in Ricerca** fino alla scansione completa successiva. Un'esclusione
silenziosamente non applicata, cioè l'utente che crede di aver deciso qualcosa.

`Files.ExcludedByPath` separa i due fatti: il segmento è derivabile dal `MaterializedPath` **senza
leggere il disco**, quindi la riconciliazione può disfarlo; l'attributo no, e resta esclusiva del
merge di una scansione. Il predicato SQL incornicia path e nome fra separatori, così i quattro
casi di confine (inizio, fine, in mezzo, path intero) diventano **uno**; l'escape è `!` e non `\`,
perché `\` è il separatore con cui si incornicia e `ESCAPE '\'` renderebbe letterale la coda del
pattern — l'esclusione combacerebbe **con niente**, in silenzio.

**La normalizzazione del segmento vive sul valore**, nell'`init` di `EffectiveFilter`: né il
costruttore né un `with` possono contenere un segmento non normalizzato. Senza, `Windows\` — **la
grafia che questo stesso brief usa** — combaciava in SQL e non in memoria: la riconciliazione
escludeva e **la scansione successiva riammetteva tutto**. E il fold è **ASCII-only** in entrambe
le metà, come `NOCASE` e come 9a/P2: `IndexOf(OrdinalIgnoreCase)` piegava anche il non-ASCII, e
poiché la riconciliazione scrive `ExcludedByPath` **nei due versi**, la divergenza non era
un'esclusione mancata ma la scansione **disfatta**, con flip-flop a ogni giro.

**La riconciliazione gira su tutti i root, non solo su quelli senza override**: `ExcludedPaths` è
globale e l'override sostituisce soltanto le estensioni, quindi saltarli lasciava A2 vivo identico
lì, per di più con `NeedsScan = false` e conteggi parziali.

#### 2. Il sottoalbero che diventa nascosto (A3)
`UsnDeltaApplier` escludeva **solo ciò che stava nello stesso delta**: le righe già a catalogo
sotto quella cartella non sono nominate da alcun record del giornale — non sono cambiate — quindi
restavano incluse. Il pass completo non ha il buco (chiede il perimetro per **ogni** directory del
catalogo alla chiusura, quindi ogni discendente produce la propria area saltata); il delta non
può, e il buco si chiude lì. Ora un `ExecuteUpdate` set-based sulle righe la cui `DirectoryId`
cade in `DirectoryQueries.InSubtree`, **chunked con una transazione per chunk**, e l'FTS potata
**per directory** (`SyncDirectoriesAsync`) invece che con un `RemoveAsync` per file.

**Gira prima delle due passate di assenza**, che è il trucco dello scan: le righe che marca
`IsIncluded = 0` escono da sole dalla passata degli assenti, il cui guard SQL è `IsIncluded = 1`.
Conseguenza — un file davvero cancellato da una cartella che il delta ha appena escluso esce
**escluso, non assente**, cioè ciò che produce una ri-scansione completa dello stesso mondo.

#### 3. Le cause si sommano, e questa è stata la lezione del giro
**Due BLOCKER, che erano la stessa idea sbagliata a due profondità.** Il primo: quando entrambe le
regole rifiutano un item, la prima stesura ne sceglieva **una** — e disfare la causa vincente
riammetteva il contenuto di una cartella **nascosta** perché l'utente aveva tolto un segmento di
path che non c'entrava. Lo scenario di 11h con un innesco nuovo, per di più **dipendente dalla
storia**: una scansione precedente che avesse visto la cartella hidden-ma-non-ancora-path-esclusa
lasciava la riga protetta. Stesso disco, due esiti. Il secondo, sopravvissuto al primo fix:
`VerdictFor` restituiva il **primo** antenato escluso invece dell'unione di tutti — e sulle
impostazioni che il prodotto **installa di default** (`AppData` è seedato **ed** è Hidden su
Windows), `AppData\Local` registrava solo la causa path. `Covers()` non ne risentiva, quindi ogni
test esistente restava verde.

Il baratto che la prima stesura descriveva («o una precedenza, o la riga bloccata a vita») **era
falso**: `SkippedScanArea` è una lista e `ExcludeForCauseAsync` gira una volta **per causa
distinta**, non per area. Registrarle entrambe costa **zero statement**.

#### Verifica
xUnit **949 verdi** (+67 su 882), build pulita warnings-as-errors **Debug e Release**, frontend non
toccato (Vitest resta a 256, nessun `ng build`). Migration additiva con **backfill pessimista**:
le righe legacy `ExcludedByScan = 1` restano tali, perché riammettere in silenzio è l'errore
invisibile e tenere fuori una riga una scansione in più è quello visibile e reversibile.

**Dieci passate di code review indipendente** (quattro sul checkpoint 1 fino a una pulita, sei sul
checkpoint 2), che hanno trovato **2 BLOCKER, 7 MAJOR, 20 MINOR**. Da metà in poi i rilievi non
erano più leggibili a occhio: erano **buchi di copertura** trovati per **mutazione** — mutare il
prodotto e contare i rossi **sull'intera suite**, non sulla classe. Tre volte una mutazione ovvia
ha lasciato verdi *tutti* i test; una di quelle avrebbe fatto rientrare il contenuto di una
cartella nascosta.

L'altra metà dei rilievi erano **affermazioni che dicevano il contrario del codice** — e due di
quelle erano la *correzione* di un'affermazione sbagliata, scritta senza eseguire la mutazione che
descriveva. Da lì la regola, che vale oltre questo step: **prima di scrivere cosa produce una
mutazione, eseguila.**

**Sul ferro** (`D:\Collaudo\A` ↔ `C:\Collaudo\B`), due passate identiche: **59 scenari, 53 PASS,
0 FAIL, 6 SKIP** (86,4 s e 94,5 s). `appsettings.json` rimesso byte-identico (sha256
`653f5990…`). La metà **A2 è provata su file veri** su tutte e tre le coppie: aggiunto il segmento
`vault` → `included=4 excluded=1 needsScan=False`; tolto → `included=5 excluded=0 needsScan=True`.
**RED dimostrato sul ferro** rimettendo il prodotto pre-16: 3 FAIL esattamente sulle tre
asserzioni A2, mentre presenza, riammissione e fratello restavano verdi.

#### Limiti noti e accettati
- **A3 non è provata sul giornale VERO**: i 6 SKIP sono `usn-incremental-sync` ×3 e
  `usn-hidden-subtree` ×3, tutti per **mancata elevazione** — senza privilegi il volume non si apre
  e ogni scansione ripiega sull'enumerazione. Lo scenario **fa SKIP invece di passare**, che è la
  cosa giusta: un PASS lì proverebbe la strada sbagliata. **Serve una passata elevata dell'harness
  per firmare A3.** In-process la convergenza è provata da 24 casi.
- **Il traffico di scrittura normale disfa l'esclusione degli attributi**: un file dentro una
  cartella nascosta, toccato da un delta **successivo** che non nomina la cartella, supera il
  filtro coi **propri** attributi puliti e il merge lo riscrive `IsIncluded = 1` azzerando le
  quattro cause. Non è una regressione (valeva già per una riga esclusa da una scansione completa,
  quindi il servizio installato ce l'ha), ma **prima non contava**: il delta non escludeva quelle
  righe. Ora il catalogo può stare in uno stato **misto** che nessuna delle due strade produce da
  sola. Chiuderlo richiede un fatto che il catalogo non ha — un flag di inclusione sulle
  `Directories`, che 11g ha deciso di non avere — oppure una lettura del disco per file per delta.
- **E il delta *fa crescere* il catalogo dentro il sottoalbero escluso**: una **nuova**
  sottodirectory creata lì dentro è giudicata sui propri attributi puliti, `DirectoryMerger` ne
  inserisce la riga, e i file creati sotto vengono indicizzati.
- **Il caso stesso-tick**: una cartella spostata *dentro* quella che questo delta esclude viene
  scartata da `RemoveAll` **per path** nell'istante in cui andrebbe portata avanti **per identità**,
  e da lì nessuno la nomina più. È il terzo della stessa famiglia, ma l'unico che si chiude
  **dentro `Classify`** invece che nello schema.
- **Sospetto non verificato**: una cartella nascosta **e rinominata nella stessa finestra di 30 s**
  — `Coalesce` tiene l'ultimo record per FRN, quindi il pass interroga il path **nuovo**, non trova
  righe e non esclude nulla. Pre-esistente in famiglia (il merge delle directory matcha per path e
  non per FRN dal 14d), non riprodotto.
- **Non verificata**: l'affermazione che `InSubtree` paghi una seek su
  `IX_Directories_MaterializedPath`. Se EF traducesse `StartsWith` con prefisso parametro in
  qualcosa di diverso da un `LIKE 'x%'`, sarebbe uno **scan** di 114 132 righe per ogni directory
  esclusa nominata da un tick. Nessun test pinna quel piano.
- **Gli handler di Move non scrivono alcuna causa di perimetro** (`MoveFileIndexAsync` e i due di
  cartella): un move verso un segmento escluso lascia la riga inclusa fino alla scansione
  successiva. Pre-esistente, stessa famiglia della nota che 11g porta su `IndexUpdater`; questo
  giro **restringe** la finestra, perché ora un salvataggio di Setup la ripara via SQL.
- **Il servizio installato non è stato aggiornato**: c'è una migration, quindi distribuirlo è una
  decisione dell'utente e ha la sua procedura.
- **E2E non eseguiti**: il loro `globalSetup` si rifiuta di partire da elevato (12a).

### Deploy di 15b sul catalogo reale (2026-09-02)
**Il primo deploy senza migration.** Distribuito su richiesta dell'utente; questo giro non tocca
lo schema, quindi è una copia di file e il database non viene aperto in scrittura da nessuna
migrazione.

**Stato di partenza, verificato prima di toccare qualcosa** — la solita e unica domanda che conta:
`GET /api/dashboard` dava **0 job in coda, 0 bloccati, 0 in corso**. Catalogo a **742 669** file,
invariato dal deploy di 15a (il worker USN su `D:` non ha raccolto altro drift nel frattempo).

**Sequenza**: publish col servizio attivo, `sc stop` pulito, backup `filetracert.db.pre15b` con
**sha256 identico** all'originale (`69dcbac6…`), `install-service.ps1`. Il backup è stato fatto
comunque, pur senza migration: costa nulla ed è la rete sotto un aggiornamento di binari.

**Verificato sul ferro**, servizio `Running`/`Automatic`, in ascolto solo su `127.0.0.1` e `::1`,
UI 200, **401** senza token, **zero Error** nel log dall'avvio:

| controllo | esito |
|---|---|
| migration applicate | **nessuna nuova** — in cima restano le due di 15a, che è ciò che ci si aspetta |
| rebuild dell'FTS | **nessuno** — le uniche due righe «Search index» nel log dei log sono del 24/08 |
| catalogo | 742 675 file · 114 212 directory · 742 669 righe FTS · 27 volumi |
| job non terminali | 0 |
| `foreign_key_check` | nessuna violazione |

**Il fix del frontend è davvero servito**, e non solo compilato: `selectedId` compare in due chunk
lazy dentro `wwwroot`, datati oggi. Il controllo vale la pena farlo — un `ng build` riuscito non
prova che i file siano finiti in `Program Files`.

**Il percorso che questo giro ha riscritto risponde**: `GET /api/operations` (la lista della Coda,
dove la review ha trovato il MAJOR) torna 200 in **5 ms** su 28 job. Con zero job bloccati la somma
raggruppata dei byte atterrati esce subito, che è il caso normale.

**I tempi, mediana di 5**, accanto a quelli del deploy di 15a:

| endpoint | 15a | oggi |
|---|---|---|
| `GET /api/dashboard` | 125 ms | 156 ms |
| `GET /api/volumes` | 39 ms | 88 ms |
| `GET /api/volumes/{id}` | 264 ms | 333 ms |
| `GET /api/operations` | — | 5 ms |

Più alti di 15a, e **non attribuibili a questo giro senza un A/B che non è stato fatto**: sono le
prime letture su un servizio appena riavviato, a cache fredda, sul disco di sistema. È lo stesso
avvertimento che i paragrafi di 14a e 15a portano già; nessuna di queste query è stata toccata da
15b.

**E la Copy continua a dire la verità**, riprovata col preview che per §7 non crea il job — stesso
file, stesso volume, stessa destinazione, cambia solo il verbo: `CopyFile` chiede **1 941** byte +
58 di margine, `MoveFile` chiede **0**.

**Limiti dichiarati**: il **riavvio della macchina** continua a non essere stato fatto; nessuna
Copy né alcuna ripresa **reale** è stata eseguita sul catalogo dell'utente, di proposito — un job
vero duplicherebbe o sposterebbe un suo file, e il ricontrollo della ripresa è provato dai 4
scenari `crash-resume` e da `resume-space-recheck` sul ferro, non sui suoi dati.

### Fatto nello step 15b (2026-08-27, commit `81d3c7d`…`d8affe7`)
**Due difetti scelti dall'utente**, le voci **A6** e **A1** della roadmap. Nessuna migration,
nessun cambiamento di contratto.

#### 1. Un job ripreso ricontrolla il disco (A6)
Il ricontrollo hard viveva dentro `if (job.State == JobState.Pending)`, quindi un job ripreso da
un checkpoint — dopo un crash, uno spegnimento, o un rilascio da `Blocked` — rientrava nella copia
sulla fede di una risposta data **prima** dell'interruzione. Era l'ultimo buco nel «mai copiare
sulla fiducia di una stima» del §4, e lo **step 15a lo aveva allargato**: da quando una copia
intra-volume prenota, anche le operazioni che restano su un volume sono esposte.

**La sottigliezza che rende il fix un fix e non un difetto peggiore.** Un job ripreso ha già messo
parte della propria domanda sul target, e quei byte mancano già dalla lettura dello spazio libero.
Richiedere di nuovo la domanda **intera** li conterebbe due volte e parcheggerebbe per sempre ogni
job grosso interrotto: 9 GB di una copia da 10 GB scritti, mezzo giga libero, e un job che ha
bisogno di 1 GB rifiutato perché ne chiede 10. La domanda giusta è «quanto **manca**».

Quella risposta ha **una definizione sola** — `JobStates.OutstandingBytes` — e deve averla: la
chiede l'engine prima di riprendere e il `BlockedJobRevaluator` prima di liberare un job
parcheggiato, e il commento del revaluator dice già cosa succede quando i due giudicano su numeri
diversi: il job fa ping-pong `Blocked → Pending → Blocked` senza muovere un byte.
`EvaluateHardAsync` la deriva **da sé** dagli item invece di prenderla come parametro, così nessun
decisore può passare la cosa sbagliata — non passa niente. `JobStates.Landed` nomina gli stati di
item i cui byte sono sul target, e `CompletedItemBytes` dell'engine legge da lì invece di
riscrivere gli stessi tre stati.

Il controllo gira **solo** prima della fase di copia, mai prima di `Verifying` o `DeletingSource`:
da `Verifying` in poi ogni passo rinomina sul posto o **libera** spazio, quindi rifiutare lì
parcheggerebbe lavoro già finito.

#### 2. Su Volumi vince l'ultima scelta, non l'ultima risposta (A1)
La schermata auto-seleziona il primo volume catalogabile appena la lista arriva; l'utente può
cliccarne un altro un istante dopo. `select` scriveva lo store **incondizionatamente** al ritorno
della propria risposta, quindi vinceva l'ultima **risposta atterrata**. Su una macchina carica
quella è l'auto-selezione, e il pannello tornava su un volume che nessuno stava guardando. Trovato
dal 12a — è ciò che fece cadere una di quelle tre passate — e lasciato lì apposta come difetto
della schermata, non del test.

Lo store registra ora ciò che è stato **chiesto**, sincronicamente, in `selectedId`, e scarta ogni
risposta che non è più quella: dettaglio, spinner ed errore. Un errore stantio riportato sopra una
selezione ancora in corso è la stessa bugia in un altro colore. `selectedId` corregge anche i due
punti che leggevano `selected` per intendere «cosa è scelto»: non lo è — è il dettaglio **arrivato**
— quindi fra un clic e la sua risposta conteneva ancora il volume precedente.

#### Verifica
xUnit **882 verdi** (+4 su 878), Vitest **256 verdi** (+6), build backend pulita
(warnings-as-errors), `ng build` ok (restano i 4 warning di budget SCSS pre-esistenti).
**RED dimostrato tre volte**: il ricontrollo tolto → rosso il solo test della ripresa mid-copy,
mentre i due che difendono la ripresa legittima restano verdi; i due guard di staleness tolti →
rossi esattamente i 3 test della corsa e verdi i 2 che descrivono `selectedId`; il numero del
lotto tolto dalla lista → **8 000 invece di 4 000**.

**Sul ferro, elevato** (`D:\Collaudo\A` ↔ `C:\Collaudo\B`): **56 scenari, 56 PASS, 0 FAIL** — i
55 di 15a più `resume-space-recheck`. La scarsità è arrangiata sul lato **domanda**, come fa
`insufficient-space` e per il motivo che 11b ha scritto: riempire un disco vero — qui quello di
sistema — per qualche secondo è un disservizio, non un test. I quattro `crash-resume` esistenti
attraversano ora il controllo nuovo, che è l'altra metà della prova: il ricontrollo non deve
rompere una ripresa legittima. `appsettings.json` rimesso byte-identico (sha256 `653f5990…`).

#### La code review, e cosa ha trovato
**Fatta da un agente indipendente con contesto pulito** — la prima di questo progetto, perché
l'utente ha tolto il divieto di lanciare sotto-agenti proprio per questo («è importante fare
determinate cose con un contesto pulito»). Ha trovato **due cose**, corrette in `27db639`.

**MAJOR, e la colpa era mia.** `QueueService.ListAsync` **non** carica gli item di proposito (E1,
step 11e). La mia derivazione cadeva quindi nel suo fallback e chiedeva la domanda intera: la Coda
avrebbe mostrato per un job bloccato a metà copia un deficit che l'engine non ha mai deciso — quel
job da 9 GB su 10, fermo perché gliene manca 1, dato per corto di 10 GB. E contraddiceva il
commento immediatamente sopra la chiamata, che promette che il numero mostrato è quello che ha
parcheggiato il job: la mia modifica rendeva falso proprio quel commento. Corretto con lo stesso
pattern batched che quel metodo già usa per i path sorgente — una somma raggruppata, zero entità
materializzate — e la derivazione resta una sola: chi **decide** non passa niente e non può quindi
passare la cosa sbagliata; solo la lista, che non può caricare gli item, fornisce ciò che ha letto.

**MINOR.** `clearSelection()` è l'unico chiamante che muove `selectedId` senza fare una richiesta,
quindi una selezione in volo si vedeva scartare la risposta dal guard e non restava nessuno ad
azzerare `detailLoading`: spinner acceso per sempre. Oggi irraggiungibile (nessuno la chiama), ma
è API pubblica di uno store che questo stesso step ha indurito contro questa classe di corse.

**Verificato pulito** dalla review, e vale la pena scriverlo: il flag `roomChecked` su tutti e
quattro i percorsi di ripresa, `SetBlockedAsync` chiamato da `Copying` con i partial su disco,
l'aritmetica dell'item interrotto (pessimista, non ottimista — il lato sicuro), il reset degli item
in `RetryAsync`, e che i chiamanti di `EvaluateHardAsync` sono esattamente tre.

#### Un difetto dei test, trovato dalla suite
Tre test di `JobResumeTests` e `JobCancellationTests` cablavano l'engine su un `ISpaceLedger`
**finto** che rispondeva solo a `ReleaseAsync`. Bastava finché una ripresa non chiedeva niente al
ledger; da questo step lo chiede, e il finto rispondeva `null` a `ComputeFeasibilityAsync`. I due
guasti erano diversi ed entrambi ingannevoli: `JobResumeTests` trasformava una ripresa riuscita in
un job `Failed` (il null passava dal catch generico dell'engine), e `JobCancellationTests`
**andava in hang** — il suo mover con cancello aspettava un `Verify` che non poteva più arrivare, e
la suite restava lì. *È questo che faceva sembrare la suite lenta su una macchina lenta: non era la
macchina.* Il blame-hang ha nominato il test.

Entrambi usano ora un `SpaceLedger` **vero** sullo stesso database in memoria, che è ciò che
CLAUDE.md chiede a parole sue: un test che mocka il ledger non testa il ledger — e il ledger è ora
parte di ciò che una ripresa fa. `JobExecutionEngineTests` portava già la stessa nota («a ledger
substitute previously masked the double-count bug»): è la stessa lezione arrivata una seconda
volta.

#### Limiti noti e accettati
- **Il rifiuto di una ripresa non è provato riempiendo un disco vero**, né in xUnit (sonda dello
  spazio stubbata) né sul ferro (scarsità dal lato domanda). È la stessa scelta di 11b, con lo
  stesso motivo: il volume di destinazione del collaudo è `C:`.
- **La lista della Coda paga ora una query in più** per pagina, ma solo quando ci sono job
  `Blocked` con una domanda: una somma raggruppata sugli item di quei job, nessuna entità
  materializzata. Sotto la soglia di E1 per costruzione.
- **`OutstandingBytes` con gli item non caricati e nessun numero passato restituisce la domanda
  intera.** È il lato sicuro per chi *decide* (può rifiutare un job che sarebbe entrato, mai
  ammetterne uno che non entra), ed è il numero sbagliato da *mostrare* — che è esattamente il
  difetto che la review ha trovato. Ora l'unico chiamante che non carica gli item passa il numero.
- **E2E non eseguiti**: il loro `globalSetup` si rifiuta di partire da elevato (12a) e questa
  sessione era elevata per l'harness.
- **Il servizio installato non è stato aggiornato.** Questo giro non ha migration, quindi
  distribuirlo è una copia di file — resta una decisione dell'utente.

### Deploy di 15a sul catalogo reale (2026-08-26)
**Il servizio installato sa copiare.** Distribuito su richiesta dell'utente subito dopo la
chiusura dello step.

**Stato di partenza, verificato prima di toccare qualcosa** — è la stessa domanda del deploy di
14e, e l'unica che conta: `GET /api/dashboard` dava **0 job in coda, 0 bloccati, 0 in corso**,
cioè niente da riprendere a metà. Catalogo a **742 669** file (era 742 033 al deploy precedente:
la differenza è il drift che il worker USN ha raccolto su `D:` da allora).

**Sequenza**: publish col servizio ancora attivo (per accorciare il fermo), poi `sc stop` —
pulito, WAL checkpointato a zero — poi backup `filetracert.db.pre15a` con **sha256 identico**
all'originale (`eaac4968…`), poi `install-service.ps1`, che aggiorna in place e non tocca il
database.

**Due migration, entrambe additive**, applicate all'avvio: `AddFileIsMaterialized` (colonna, con
default e backfill a `true`) e `CatalogVisibilityIncludesProjectedCopies` (drop e ricrea dei due
indici covering di `Files`, nessuna ricostruzione di tabella).

**Nessun rebuild dell'FTS**, ed era la cosa da controllare: `IndexableSql` e il conteggio del
backfill di avvio sono stati allargati **allo stesso modo**, quindi su un catalogo senza copie
proiettate il numero non si muove e la guardia a senso unico tace. L'unica riga *«Search index is
short»* nel DB dei log è del **24/08**, cioè del deploy di 14a.

**Verificato sul ferro**, servizio `Running`/`Automatic`, **401** senza token, **zero Error** nel
log dall'avvio (restano solo i Warning `FirstOrDefault senza OrderBy` di EF, pre-esistenti):

| controllo | esito |
|---|---|
| migration applicate | le due di 15a, in cima a `__EFMigrationsHistory` |
| `Files.IsMaterialized` | presente, `INTEGER`, default `1` |
| righe con `IsMaterialized = 0` | **0** — nessuna Copy accodata, come dev'essere |
| i due indici covering | ricreati con la colonna in coda |
| catalogo | 742 675 file · 114 212 directory · 742 669 righe FTS · 25 volumi |
| `foreign_key_check` | nessuna violazione |

**La proprietà di 11e regge sul catalogo vero, col disgiunto nuovo** — è la misura che lo step
aveva fatto su 20 000 righe, ora provata su 742 675: il ramo caldo del contatore per cartella dice
`SEARCH f USING COVERING INDEX IX_Files_DirectoryId_PendingDirectoryId_IsIncluded_IsPresent_IsMaterialized`.

**E la funzione vive**, dimostrata **senza creare niente** — con il preview, che per §7 non crea il
job. Stesso file (12 091 byte), stesso volume, stessa destinazione, cambia solo il verbo:

| preview | `requiredBytes` | `marginBytes` |
|---|---|---|
| `CopyFile` | **12 091** | 362 |
| `MoveFile` | **0** | 0 |

È il punto n° 1 dello step visto in produzione: una copia intra-volume chiede spazio, un move
intra-volume no. `estimateIsLive: true`, cioè il numero l'ha risposto il dispositivo in
quell'istante.

**I tempi, attraverso l'API, mediana di 5**, accanto a quelli del deploy di 14e:

| endpoint | 14e | oggi |
|---|---|---|
| `GET /api/dashboard` | 119 ms | 125 ms |
| `GET /api/volumes` | 45 ms | 39 ms |
| `GET /api/volumes/{id}` | 176 ms | 264 ms |

**La Ricerca restituisce esattamente le stesse righe di 14a** (`report` 431, `report`+Image 2,
`e` tappato a 10 000, `e`+Image 508): il disgiunto non cambia una risposta, ed è atteso, perché
righe proiettate non ce ne sono. I millisecondi sono più alti di quelli di 14a (133 / 95 / 903 /
253 contro 55 / 50 / 471 / 90) e **non sono attribuibili a questo giro senza un A/B che non è
stato fatto**: 14a misurava una **copia a riposo**, questa è il servizio vivo sul disco di
sistema, e lo stesso avvertimento è scritto nel paragrafo di 14a. Ciò che è stato verificato è la
domanda vera — il **piano è identico** con e senza il disgiunto (`SCAN fts` + seek per rowid in
entrambi i casi), quindi quel termine è un booleano per riga già recuperata, non una passata in
più.

**Limiti dichiarati**: il **riavvio della macchina** continua a non essere stato fatto (l'avvio
automatico resta provato solo da `sc start`/`install-service.ps1`); nessuna Copy **reale** è stata
eseguita sul catalogo dell'utente, di proposito — accodarne una duplicherebbe un suo file, e il
preview prova lo stesso percorso di enqueue senza scrivere niente; e i backup precedenti
(`filetracert.db.pre14a`, `.pre14e`) restano dove sono, accanto al nuovo `.pre15a`.

### Fatto nello step 15a (2026-08-26, commit `e999238`…`cbe5c3b`)
**Si può copiare, e la copia è un'operazione di prima classe.** Primo lavoro di **fase 2**,
scelto perché riusa una macchina a stati già provata sul ferro invece di inventarne una. La
stima «Copy è Move senza `DeletingSource`» è vera **solo dell'esecuzione**: le tre cose che
costano stanno altrove, ed erano il motivo per cui il task esisteva.

**1. Una copia intra-volume sposta byte.** `SpaceLedger.ReservationFor` cominciava con
`!job.IsIntraVolume` — una scorciatoia per «un'operazione che resta su un volume non muove
niente fra volumi, quindi non chiede spazio». Vera di ogni operazione che esisteva allora, falsa
di una copia che resta ferma: scrive un **secondo** insieme di byte sul volume da cui legge. La
domanda vera è `RequiredBytesTarget > 0`, e continua a dire **no** a un move intra-volume, la cui
domanda è zero per costruzione. `SpaceCheck.EvaluateHardAsync` perde la stessa clausola **nello
stesso commit**, di proposito: la sua guardia **specchia** quella del ledger, e due grafie di
«questo job ha bisogno di spazio» significano che la coda prenota per un job che l'engine lascia
passare senza guardare il disco. Il test è nelle **due** direzioni, come il task chiedeva.

**2. Una copia crea un'entità nuova.** §5 non si può quindi esprimere con i campi `Pending*`:
non c'è una riga alla destinazione che possa portarli. `OverlayWriter` ne crea una **prima** del
file (`IsMaterialized = false`, `IsPresent = false`, `PendingCreate`) — lo stesso mestiere che
`IsMaterialized` fa da sempre sulle directory, e il motivo della colonna nuova su `FileEntry`
(migration additiva, backfill a `true`). Il **sorgente non viene toccato**: un move ci stampa
`PendingMove`, una copia non cambia nulla di lui. Alla conclusione `IndexUpdater` promuove la riga
in loco — mai una seconda riga accanto — e lo fa **prima** di `ClearForJobAsync`, che cancella le
righe non materializzate di quel job: l'ordine opposto cancellerebbe dal catalogo il file appena
scritto, dentro la transazione che dichiara il job completo. Annullata o fallita, quella riga si
**cancella**: è l'unico punto in cui il no-hard-delete di §6 non vale, perché quella riga non ha
mai descritto niente su nessun disco.

**3. Il sorgente sopravvive.** Il guard di enqueue serializzava per sovrapposizione di path
assumendo che chi tocca un path lo **porti via**. `PathClaim` ha ora tre generi — `Source`,
`Target`, **`Read`** — e nessuno dei due preesistenti andava bene per il sorgente di una copia:
come `Source` avrebbe messo in fila due copie dello stesso file verso due cartelle diverse per
nulla; come `Target` sarebbe stato **peggio**, perché `SameTarget` confronta i path per
**uguaglianza** e quelle due copie condividono il path sorgente — sarebbero state lette come
atterranti sulla stessa destinazione, cioè il contrario del vero. La regola guadagna un congiunto:
un `Target` che si **sovrappone** a un `Read` è conflitto. Un lettore perde contro chi rimuove o
riscrive ciò che sta leggendo, e contro nient'altro.

#### La misura che il task pretendeva, e la deviazione che ha prodotto
Il task diceva di aggiungere al Catalogo il disgiunto `|| PendingState != None`, «come già fanno
le directory». Misurato su 20 000 righe, il ramo caldo del contatore per cartella:

| disgiunto | piano |
|---|---|
| nessuno (baseline 11e) | `SEARCH … USING COVERING INDEX …` |
| `\|\| PendingState <> 'None'` | `SEARCH … USING INDEX …` |
| `\|\| NOT IsMaterialized`, colonna non indicizzata | `SEARCH … USING INDEX …` |
| `\|\| NOT IsMaterialized`, colonna nell'indice | `SEARCH … USING COVERING INDEX …` |

Perdere `COVERING` è **una lettura di riga di tabella per ogni file contato**: le ~300 000 per
listato che lo step 11e esisteva per togliere, restituite in silenzio. `PendingState` — la grafia
ovvia — non può ricomprarsele perché è una **stringa**, ed è esattamente la ragione per cui 11e
rifiutò di indicizzarla. `IsMaterialized` è un booleano: i due indici covering lo prendono come
colonna in coda (migration `CatalogVisibilityIncludesProjectedCopies`, drop e ricrea, nessuna
ricostruzione di tabella) e il piano torna quello che 11e aveva lasciato. `CatalogCountIndexTests`
asserisce ora il predicato **nuovo**, così la regressione non può rientrare zitta.

La stessa regola sta **una volta sola** in `FileSearchIndex.IndexableSql` — allargata perché il
nome proiettato di §5 sia cercabile nell'istante in cui la copia è accodata — e specchiata con il
motivo scritto accanto nei suoi altri due punti: la negazione di `PruneVolumeAsync` (che non deve
potare una proiezione) e il conteggio del backfill di avvio.

**Il merge dello scan scrive ora `IsMaterialized = 1`** sulla riga che ritrova, per la ragione più
forte possibile: ha appena visto il file sul disco. Il suo match per path è `(DirectoryId, Name)`,
quindi **può** atterrare su una riga proiettata da una Copy, e lasciarla non materializzata la
renderebbe presente e mai-creata insieme. Il pass degli assenti non la raggiunge comunque: pretende
`IsPresent = 1`, che una proiezione non ha mai.

#### UI (skill `impeccable`)
La modalità si sceglie **al punto di intenzione** — due pulsanti nella barra di selezione di
Catalogo e Ricerca — e non con un interruttore dentro il modale. Un toggle sopra un pulsante che
dice solo «Accoda» è un errore di modo che aspetta di succedere, e le due operazioni non sono
egualmente recuperabili: un move manda gli originali nel cestino. Il dialog dichiara la scelta due
volte, nel titolo e **nell'etichetta del pulsante che la conferma**: «Accoda copia →» /
«Accoda spostamento →». Un solo primario, non due: Move resta primario (è il verbo centrale del
§1) e «Copia selezionati…» sta accanto a «Rinomina» come ghost. Il preview **non è cambiato**, ed
è il punto: il backend restituisce ora un `requiredBytes` vero per una copia intra-volume, quindi
«Verifica spazio» dice la verità senza che la schermata sappia niente di nuovo.

#### Verifica
xUnit **878 verdi** (+46 su 832), Vitest **250 verdi** (+6), build backend pulita
(warnings-as-errors), `ng build` ok (restano i 4 warning di budget SCSS pre-esistenti, e non è
stato aggiunto SCSS). **RED dimostrato cinque volte**, rompendo il prodotto e ripristinandolo:
- guardia del ledger rimessa su `IsIntraVolume` → **2 rossi** (la prenotazione intra-volume e il
  ricontrollo hard), e l'intra-**move** resta verde, che è metà della prova;
- sorgente della copia rimesso a `Source` → **3 rossi su 11**, esattamente i tre «due lettori
  devono convivere», mentre gli 8 che devono restare conflitti erano verdi prima e dopo;
- disgiunto del Catalogo tolto e pulizia rimessa ad azzerare invece che cancellare → **3 rossi su
  19**;
- salto di `DeletingSource` tolto → **5 rossi su 7**, e il guasto è **i sorgenti finiti nel
  cestino**;
- modalità ignorata nel picker → **2 rossi**, i due che parlano del verbo.

**Sul ferro, elevato** (`D:\Collaudo\A` ↔ `C:\Collaudo\B`): **55 scenari, 55 PASS, 0 FAIL**, la
baseline di 52 più i tre nuovi — `copy-intra-volume` (che gira su tutte e tre le coppie),
`copy-cross-volume`, `copy-cancel-mid-flight`. Eseguito **due volte**, prima e dopo la correzione
della review. `appsettings.json` rimesso byte-identico (sha256 `653f5990…` verificato).
`D:\Collaudo\A` è stato **ricreato**: non c'era più sul disco.

La **code review finale** di questo giro **l'ho fatta io sul diff, non un agente indipendente** —
il harness di questa sessione vieta di lanciare sotto-agenti senza richiesta esplicita
dell'utente, e lo scrivo invece di presentarla per ciò che non è. Ha trovato **una cosa con i
denti**, corretta in `cbe5c3b`: `ProjectCopyAsync` faceva **una lettura e una `SaveChanges` per
file**, dentro la transazione di enqueue che tiene l'unico write lock di SQLite. Una copia di
cartella si espande a un item per file del sottoalbero: migliaia di round trip sotto quel lock,
proprio dove un job che sta copiando aspetta di scrivere i propri checkpoint. Ora è **una query e
un salvataggio** per l'intero lotto. La seconda metà della correzione è altrettanto necessaria: la
ricerca della riga già proiettata è scopata per **directory** e non per `PendingJobId` da solo —
quella colonna non ha indice, quindi la forma semplice è una scansione di `Files` (742 033 righe
sul catalogo vero) e alla **prima** applicazione scansionerebbe tutto per non trovare niente;
`DirectoryId` è la colonna di testa dei covering index di 11e/14c. Un test la misura **in
statement, non in millisecondi**, e asserisce che a non crescere siano le **letture**: EF emette
una INSERT per riga comunque — l'enqueue paga esattamente quella per i propri
`OperationJobItems`, 61 di ciascuna — quindi contare gli statement misurerebbe il provider, non il
codice. RED rimettendo la ricerca nel ciclo: **76 letture invece di 16**.

#### Limiti noti e accettati
- **Una copia annullata lascia sul disco ciò che era già atterrato.** Gli item già finalizzati
  alla destinazione sono file veri la cui riga proiettata l'annullamento cancella, quindi restano
  non catalogati fino alla scansione successiva di quel volume. È l'esito onesto: l'alternativa è
  cancellare file che nessuno ha chiesto di cancellare. `ReconcileCancelledJobAsync` **rifiuta**
  una Copy per un motivo con i denti — ri-punta la riga `Files` che l'item nomina, e per una copia
  quell'item nomina il **sorgente**, ancora fermo dov'è sempre stato: ri-puntarlo lascerebbe il
  catalogo a sostenere che l'originale si è spostato in una destinazione che l'utente ha appena
  annullato.
- **Una Copy verso un nome già occupato produce due righe con lo stesso nome nella stessa
  cartella**: quella reale e quella proiettata. Il job finisce `Blocked(NameCollision)` e §5 dice
  che un `Blocked` **conserva** l'overlay, quindi il Catalogo mostra il doppione con il badge «In
  creazione» finché l'utente non risolve. È informativo, non un difetto — ma va saputo.
- **Le date della copia sono l'istante della copia, non quelle del sorgente.** `Win32FileMover`
  scrive uno stream nuovo e non preserva i timestamp, quindi portarsi dietro quelli del sorgente
  daterebbe il file nuovo a qualcosa che non gli è mai successo. Non sono rilette dal disco:
  `IFileMetadataReader` vuole un mount root e `IndexUpdater` ha un volume GUID, e il valore che
  restituirebbe è l'istante che abbiamo appena scritto. Una scansione successiva mette i valori
  veri. La **dimensione** è invece esatta: `Verify` ha confrontato le lunghezze.
- **`ExcludedByRoot` viene azzerato alla conclusione** anche quando la destinazione è fuori da
  ogni root attivo. È lo stesso buco noto che 11g documenta per `IndexUpdater` (il filtro di
  default come «risposta più larga»), e si ripara alla scansione successiva.
- **La proiezione di una copia di cartella è una riga per file.** Non c'è alternativa: nessuno di
  quei discendenti esiste alla destinazione, quindi il trucco di `MoveFolder` — un solo overlay
  sulla riga della cartella — non è disponibile. L'enqueue scrive già un `OperationJobItem` per
  file nella stessa transazione, quindi è lo stesso ordine di lavoro, non una classe nuova; il
  tetto del gesto resta `MaxBatchSize`.
- **Un doppio passaggio di pulizia dei partial logga un Warning innocuo**: l'annullamento dell'API
  e quello dell'engine possono entrambi provare a cestinare lo stesso `.fadit-partial`, e il
  secondo prende `SHFileOperation` codice 2 (file non trovato). Pre-esistente, visibile anche in
  `cancel-mid-copy`; lo scenario passa perché nessun partial resta.
- **E2E non eseguiti**: il loro `globalSetup` si rifiuta di partire da elevato (12a) e questa
  sessione era elevata per l'harness.
- **Il servizio installato non è stato aggiornato**: distribuire questo giro — e con esso pagare
  le due migration — resta una decisione dell'utente.

### Fatto nello step 14e (2026-08-25)
**Il brief dice la verità sul codice.** Nessuna modifica di prodotto: un audit di §3, §4,
§5, §6, §7 e §8 contro il codice su `develop`, più il riallineamento di
`CODE-REVIEW-HANDOFF.md` e la riorganizzazione di questa roadmap.

**Diciannove discrepanze**, ognuna con la prova nel messaggio di commit che la chiude.
Le cinque che avrebbero fatto sbagliare qualcuno:
- **§4 diceva che exFAT/FAT32 fa «enumerazione + diff (size/timestamp)»**. Non esiste
  alcun diff: si enumerano le cartelle sorvegliate e si passa dallo **stesso merge** del
  percorso NTFS. Chi si fosse fidato avrebbe cercato codice mai esistito — o peggio,
  l'avrebbe scritto credendo di ripristinare un comportamento voluto.
- **§6 descriveva una tabella FTS5 a due colonne**. Ne ha tre da 14a, e la terza
  (`tags`) è sicura **solo** finché ogni MATCH resta scopato per colonna: una quarta
  strada di ricerca scritta contro il §6 vecchio renderebbe i token cercabili.
- **§7 elencava ancora `isStale`**, tolto da 11f — la cui stessa scheda K13 annotava che
  «§7 lo elenca», senza che nessuno correggesse §7.
- **`Files.CreatedUtc`/`ModifiedUtc` sono nomi di *colonna*, non di proprietà**: in LINQ
  le date del file si leggono da `FileCreatedUtc`/`FileModifiedUtc`, mentre
  `f.CreatedUtc` è la colonna di **audit** — e non dà errore.
- **Il commento di `FeasibilityResult.EstimateIsLive`** diceva «true quando il volume è
  online», cioè il significato che 11b ha *smesso* di avere: da allora è «il dispositivo
  ha risposto adesso». Unica modifica al codice di questo giro, commento e basta, come
  il task permette.

Le altre: la solution è `FileTracert.slnx` (non `.sln`); l'albero di §3 ignorava
`FileTracert.HardwareSmoke` — citato altrove dalle regole di test dello stesso
documento — e `tests/FileTracert.PoolProbe`; §3 elencava quattro BackgroundService su
nove; il paragrafo dello shared kernel diceva «oggi `ScanPath`» dopo che 14d ci ha
portato `UsnPathResolver`; §3 prometteva il token «in header» senza la forma in query
string dei soli `/hubs/*`; l'elenco degli indici di `Files` ne dava cinque su nove, e
uno con un nome di colonna inesistente; `AppSettings` non nominava `LogMaxRows`; la
fattibilità non nominava `feasible` né `marginBytes`; §8 dava cinque feature su nove e
sei schermate su sette (mancavano **Setup** e **Log**); `.ft-panel`/`.ft-pill`/
`.ft-card` erano descritte come classi CSS e sono **componenti Angular**;
`TASK-step0-scaffold.md` non esiste e il mockup sta in `src/frontend/`.

**Dove il codice era indietro e il brief avanti**, si è tenuto il brief e si è aperta una
voce di roadmap invece di riscrivere il testo: il **filtro dimensione** di §8 (esiste
nell'API, non a video) e il **paging delle sottocartelle** di §7 (E5, mai chiuso). È la
scorciatoia che il task chiedeva di non prendere.

**`CODE-REVIEW-HANDOFF.md`**: dieci dei quindici finding della TOP 15 e quattro righe C
erano ancora scritte come aperte, chiuse nei fatti dai WP1–WP3 e dal giro date/UTC di
mesi fa — cioè il file avrebbe fatto rifare lavoro finito, l'esatto contrario del suo
scopo. Ogni chiusura porta ora il commit **e** un punto di codice verificato su HEAD.
Restano aperti **due soli** finding (C32 e P1) più la metà di E5 lasciata come decisione
di prodotto; tre schede (K12, E3, E5) descrivevano codice che 14a e 14c hanno mosso di
nuovo.

**Verifica**: build backend pulita (warnings-as-errors), xUnit **832 verdi** — nessun
test toccato, perché il solo cambiamento di codice è un commento XML. Frontend non
toccato. Harness sul ferro non rieseguito e non richiesto: questo giro non cambia il
comportamento su un solo byte.

**Limiti noti:** l'audit ha coperto §3–§8, cioè le sezioni che descrivono componenti e
comportamenti, come chiedeva il task; §5 (proiezione) è stato verificato e trovato
esatto. I paragrafi storici «Fatto nello step N» **non** sono stati riverificati riga per
riga: sono la memoria di ciò che era vero quando è stato scritto, e riscriverli
significherebbe cancellare la storia invece di allinearla.

### Fatto nello step 14d (2026-08-25, commit `626a42f`…`9383f00`)
**Il percorso incrementale USN esiste, e concorda con la scansione completa.** Chiude le voci 4 e 5
del lavoro lasciato dallo step 13, e rende vero il nodo tecnico n° 2 del §1 — che fino a ieri il
prodotto non aveva. Con un colpo di scena: **il difetto che il task esisteva per chiudere non
c'era**, ed era un artefatto di misura.

#### Checkpoint 1 — la premessa era sbagliata, e la misura lo dimostra
Il task partiva dal «fatto n° 1» dello step 13: *l'USN non vince a nessuna scala* (4,88 s contro
3,74 s su `D:`), quindi bisogna leggere le dimensioni dall'MFT o il motore non ha motivo di
esistere. Quel confronto metteva però una sessione **elevata** contro una **non elevata**, prese in
momenti diversi e con cache diverse. Rifatto in **A/B dentro lo stesso processo elevato** — sonda
usa-e-getta che gira il `ScanService` vero su un DB usa-e-getta, con l'enumerazione forzata facendo
fallire `EnsureJournal`, cioè esattamente il caso non-elevato — su `D:` (52 233 file + 6 859
directory, 59 439 voci MFT), **10 campioni per motore**, mediane:

| | primo scan | re-scan | gather | meta |
|---|---|---|---|---|
| **USN** | **5,14 s** | **3,71 s** | 0,18 s | 0,27 s (0,87 a freddo) |
| Enumerazione | 5,26 s | 4,48 s | 0,94 s | 0 |

Sul re-scan i due intervalli **non si sovrappongono** (USN 3,18–4,42, enum 4,23–5,38), e il
controllo con l'ordine invertito dà lo stesso quadro. Sul volume intero l'USN quindi **pareggia il
primo scan e vince il re-scan del 17%**, oggi, senza toccare una riga.

Dove perde è un altro posto, e l'MFT non c'entra. Su un **sotto-albero** (`D:\DeployProd`, 5 557
file, 3 campioni): USN 1,16 s / 0,88 s contro enumerazione **1,05 s / 0,71 s**, perché
`FSCTL_ENUM_USN_DATA` cammina **tutta** l'MFT del volume ignorando il perimetro — 59 439 voci per
indicizzarne 6 189. È il caso d'uso reale del prodotto (§1: cartelle di media), ed è il termine che
un parser `$MFT` **non** toccherebbe: lì la `stat` costa 0,02 s.

E lo scan è **DB-bound**: 4,5 s dei 5,2 s sono filtro + merge + scrittura. Qualunque lavoro sul lato
lettura si muove dentro un secondo.

**Decisione dell'utente**, presa su questi numeri: niente parser `$MFT` (il rapporto fra un parser
NTFS di strutture su disco — con la sua classe di guasti silenziosi: dimensione sbagliata = catalogo
sbagliato — e un guadagno del 7% sul primo scan non regge), si va dritti ai checkpoint 2 e 3, che
sono dove sta l'ordine di grandezza. La sonda **non è stata committata**: ha prodotto i numeri qui
sopra e ha finito il suo lavoro.

#### Checkpoint 2 — `Volumes` ricorda il giornale
Colonna `UsnJournalId` (+ migration `AddVolumeUsnJournalId`, backfill nullo). Una posizione senza
l'istanza a cui appartiene **non è un cursore**: cancellare e ricreare un giornale (`fsutil`, un
ripristino da snapshot) riparte con un id nuovo, quindi un `LastUsn` perfettamente valido può
puntare dentro una storia completamente diversa. Le due metà si prendono ora dalla **stessa**
`FSCTL_QUERY_USN_JOURNAL` e si checkpointano insieme, nella transazione che già scriveva `LastUsn`.
Salvata come reinterpretazione con segno dell'`ulong` nativo, lo stesso trucco di
`FileEntry.UsnFileRef` — SQLite non ha un intero a 64 bit senza segno e il valore si confronta solo
per uguaglianza. **Nulla** dopo una scansione a enumerazione: niente deve credere di poter riprendere
da uno scan che il giornale non l'ha mai letto.

#### Checkpoint 3 — `UsnDeltaApplier` + `UsnSyncWorker`
`IUsnReader.ReadChanges` era implementato e testato **senza un solo chiamante di prodotto**, e il
`UsnSyncWorker` che il §3 elenca fra i BackgroundServices non era mai stato scritto. Ora esistono
entrambi (worker ogni 30 s, un volume alla volta, `UsnSyncIntervalSeconds`).

- **Le due strade devono concordare, e lo fanno per costruzione.** Tutto ciò che è durevole passa
  dai pezzi che usa la scansione: `MergeScannedFilesAsync` per l'upsert (che matcha per **FRN**,
  quindi un rename fatto fuori dall'app atterra sulla riga che c'era già), `DirectoryMerger.EnsureAsync`
  per l'albero, `ScanPerimeter` + `FileFilter` per il verdetto. L'unica cosa che il delta **non può
  prendere in prestito** è il pass degli **assenti**: quel pass legge «non toccato da questo giro»
  come «non c'è sul disco», che è vero di una scansione (ha guardato ovunque) e falso di un delta
  (ha guardato ciò che è cambiato).
- **L'assenza si scrive solo contro una prova**: il giornale dice che il file è stato cancellato,
  oppure dice che non è più dove la riga lo colloca. Mai perché il delta non l'ha nominato. Tutto il
  resto fuori perimetro è `IsIncluded = false` con la causa vera e `IsPresent` **intatto**, cioè la
  semantica di 11g/11h.
- **I path si ricostruiscono dal FRN del padre** attraverso lo stesso `UsnPathResolver` dello
  snapshot completo: la sua mappa contiene le directory **del delta** (così una catena appena creata
  si risolve contro sé stessa) e il suo fallback risponde dalle righe `Directories` del catalogo. Un
  padre che non sta in nessuna delle due è una cartella che la scansione non ha mai indicizzato — e
  **non è un guasto, è il meccanismo**: è così che il delta eredita l'esclusione di sottoalbero di
  C16 senza leggere un byte dal disco.
- **Il cursore si scrive per ULTIMO**, in transazione propria, come il checkpoint di 9a. Tutto ciò
  che il delta fa è idempotente (il merge matcha sul FRN, i flag si settano a costanti), quindi un
  crash costa un delta ripetuto e mai uno saltato. L'ordine opposto lascerebbe dichiarato consumato
  un delta mai applicato, e nessuno tornerebbe più a prenderlo.
- **L'invalidazione è rumorosa e una volta sola**: giornale ricreato o posizione sorpassata → il
  cursore viene **lasciato cadere**, il volume torna a `ScanWorker` via `IScanScheduler`, con log e
  Notification. Senza il drop ogni ciclo riscoprirebbe lo stesso cursore morto e rifarebbe la stessa
  richiesta.
- **`UsnPathResolver` si sposta in `Contracts`** per il motivo per cui ci si spostò `ScanPath` (§3):
  ora due layer spellano la stessa regola, ed è BCL puro. Il suo fallback `FrnNode?` — codice morto,
  mai usato — diventa quello **per path** che il caso incrementale chiedeva davvero. La risoluzione
  dei filtri per root esce da `ScanService` in `RootFilters`, così scan e delta non possono divergere
  su quale filtro governa cosa.
- **Il catalogo cambia senza che nessuno l'abbia chiesto**, quindi il delta alza `ProjectionChanged`
  (`RealtimeEvents.CatalogChangedAsync`): dallo step 10c le schermate non pollano più, e senza quel
  messaggio Catalogo e Ricerca resterebbero ferme su dati vecchi fino a un refresh a mano.

#### Verifica
xUnit **832 verdi** (+19 su 813), build backend pulita (warnings-as-errors). Frontend non toccato.

**La prova che conta è la convergenza**, e ha una forma precisa: ogni caso apre **due** database
dalla **stessa** scansione completa di un mondo di partenza, poi porta l'uno attraverso una
ri-scansione completa del mondo cambiato e l'altro attraverso il delta che descrive lo stesso
cambiamento, e pretende righe **identiche**. Due scansioni e non «una scansione vergine dello stato
finale» di proposito: un catalogo ha una storia — un file cancellato lascia una riga marcata
assente, uno escluso conserva la sua identità — e confrontare con un database vergine lascerebbe il
delta sbagliare proprio dove conta. Dodici casi: create, rename, move, delete, cartella nuova con
file dentro, rename fuori dall'allow-list, move dentro una cartella nascosta, catalogo intatto,
cursore, replay idempotente, invalidazione, non-idoneità.

**RED dimostrato tre volte**, rompendo il prodotto: coalescing a «primo record vince» invece di
«ultimo» → **4 rossi mirati** (rename, move, rename-fuori-filtro, cartella nascosta) e 8 verdi;
`UsnSyncWorker` non registrato → **4 su 4** dei test Host rossi; e il probe che disattivava del tutto
la riconciliazione **non compila** sotto warnings-as-errors, il che è di per sé una guardia.

**Sul ferro, elevato** (`D:\Collaudo\A` ↔ `C:\Collaudo\B`): **50 scenari, 50 PASS, 0 FAIL** — la
baseline di 47 più le tre coppie del nuovo `usn-incremental-sync`. Quello scenario è l'unico livello
che legge il **giornale vero**: scansione completa di una fixture, poi un file creato, uno rinominato
e uno cancellato con normali chiamate BCL, poi **solo** il delta. Due asserzioni portano il peso —
`LastFullScanUtc` non deve essersi mosso (altrimenti la convergenza sarebbe una scansione, e lo
scenario misurerebbe la strada sbagliata), e il file rinominato deve **conservare l'identità della
riga**, perché il FRN è il vero nome del file per NTFS e un rename fatto alle nostre spalle deve
atterrare sulla riga che un'operazione in coda già punta (§5). Su `C:` il delta ha portato **851
record** di traffico di sistema estraneo e ne è uscito `indexed=2 absent=1 unplaced=103`: cento
record di oggetti che questo catalogo non ha mai indicizzato, scavalcati senza danno, che è il
comportamento su cui il progetto è costruito. `appsettings.json` dell'harness rimesso byte-identico
(sha256 `653f5990…` verificato).

La **code review finale** di questo giro **è stata fatta da me sul diff, non da un agente
indipendente** (il harness di questa sessione vieta di lanciare sotto-agenti senza richiesta
esplicita dell'utente) — scritto qui invece di essere presentato come ciò che non è. Ha trovato
**tre** cose, tutte corrette in `9383f00`. La prima ha i denti: una `Win32Exception` sulla lettura
del giornale **buttava via il cursore** e chiedeva una scansione completa — ma un handle di volume
rifiutato non dice che la nostra posizione è persa, dice solo che non abbiamo potuto guardare, e su
un volume da milioni di righe quello barattava un guasto transitorio con una camminata completa
dell'MFT più un avviso a video. Ora il cursore si tiene, il fallimento si riporta come «non idoneo
adesso», e il ciclo successivo ritenta; un fallimento persistente resta visibile perché l'applier lo
logga per intero ogni volta. La seconda: una **directory cancellata fuori perimetro** veniva marcata
assente, dove una scansione la lascia stare (`DirectoryMerger` guarda `perimeter.Covers`); ora il
delta pone la stessa domanda, sul path che la riga stessa registra. La terza è un test il cui **nome
affermava il contrario di ciò che il prodotto fa** — `converges_to_excluded_not_absent` per un file
spostato in una cartella nascosta: è **assente**, e correttamente, perché la riga descrive il vecchio
path e lì il file non c'è più; l'asserzione di equivalenza da sola sarebbe stata soddisfatta anche da
due strade sbagliate allo stesso modo. Rinominato, e il verdetto ora è asserito per nome.

#### Limiti noti e accettati
- **Una cartella già indicizzata che *diventa* nascosta non ri-esclude il proprio sottoalbero.** Il
  caso *dentro* il delta è coperto (il record di cambio attributi arriva, `ExcludeSubtree` scatta, e
  i file dello stesso delta sotto di lei diventano esclusi o assenti); le righe **già** a catalogo
  che il delta non nomina restano incluse fino alla scansione successiva. Chiuderlo vuol dire un
  update ricorsivo di sottoalbero dentro un tick di sync: è un giro a sé.
- **Il verso opposto — una cartella che smette di essere nascosta — non è nemmeno rilevabile** dal
  delta (un record di cartella che passa il filtro e non ha riga è indistinguibile da una cartella
  nuova), e i file che contiene non generano record propri. Serve una scansione, che è esattamente
  ciò che 11g già scriveva.
- **L'USN perde ancora sui sotto-alberi** (misura sopra), perché lo snapshot completo ignora il
  perimetro. Non è stato toccato: sceglierlo per rapporto fra sotto-albero e MFT sarebbe scope in più
  rispetto a questo task, ed è il candidato ovvio del giro che vorrà chiudere quel divario.
- **Il delta ricompra dimensioni e date con una `stat` per file cambiato.** È corretto qui — un delta
  nomina una manciata di file — ed è la stessa lettura che sullo snapshot completo costa una syscall
  per file del volume.
- **`ReadChanges` è sincrona** e gira sul thread del worker, come `ReadFullSnapshot` nello
  `ScanService`: un delta enorme dopo un lungo spegnimento blocca quel thread. Il token è onorato
  dentro il loop nativo, quindi non tiene lo shutdown.
- **Il servizio installato non è stato aggiornato**: gira ancora il build precedente sul catalogo
  reale, che non è stato toccato. Distribuire questo giro — e con esso far girare per la prima volta
  il worker incrementale sui 742 033 file veri — resta una decisione dell'utente (voce aperta da 14a).
- **E2E non eseguiti**: il loro `globalSetup` si rifiuta di partire da elevato (12a) e questa sessione
  era elevata per necessità.

### Fatto nello step 14c (2026-08-25, commit `11a80ae`…`13f7c2d`)
**Le due schermate Volumi stanno sotto il decimo di secondo, non sotto il secondo.** Chiude la voce
n° 3 dello step 13. Nessuna riga di ciò che le schermate mostrano è cambiata: è cambiato **come** la
domanda arriva al database.

- **La diagnosi è stata letta, non ipotizzata.** `EXPLAIN QUERY PLAN` sullo statement che il prodotto
  emette davvero diceva `SCAN f USING INDEX IX_Files_VolumeId_DirectoryId` — cioè: percorri l'indice
  per volume e poi **risali alla riga di tabella di ogni file del catalogo** per leggere due
  booleani. È la **stessa forma** che lo step 11e aveva trovato un piano più sotto, sui contatori del
  Catalogo. Una sonda usa-e-getta con lo stesso attrezzo di 11e lo quantificava: **176 000 visite di
  riga su otto volumi seminati, per produrre otto numeri**. Quella sonda **non è stata committata**, e
  il motivo è nel test: il contatore di righe vive in una **view** sopra `Files`, e una view forza
  proprio l'accesso alla tabella la cui assenza è il punto — quindi può misurare il difetto ma non la
  sua chiusura. Ciò che resta nella suite è l'asserzione sul **piano**, che è riproducibile in
  entrambe le direzioni.
  Il sospetto del task («conteggi ripetuti riga per riga, 30 passate») era invece **sbagliato**: la
  lista faceva già *una* query raggruppata, senza N+1. Il difetto non era il numero di query, era che
  nessuna delle due restava dentro l'indice.
- **Il fix è un indice covering**: `(VolumeId, IsIncluded, IsPresent, SizeBytes)`. La lista diventa
  `SCAN … USING COVERING INDEX` con il raggruppamento soddisfatto dall'**ordine** dell'indice
  (niente temp B-tree), il dettaglio una seek sullo stesso indice con la `SUM` letta da lì.
  `SizeBytes` è l'ultima colonna proprio perché il totale in byte del dettaglio non debba uscirne.
- **È un'AGGIUNTA, non un allargamento**, al contrario di 11e — e la review ha corretto il motivo
  che era stato scritto. Il primo testo diceva «`DirectoryId` deve restare la seconda colonna»: vero
  ma **irrilevante**, perché allargare l'indice esistente a
  `(VolumeId, DirectoryId, IsIncluded, IsPresent, SizeBytes)` la lascia seconda e vince gli stessi
  due piani **senza un B-tree in più** — cioè esattamente il baratto che 11e fece un piano più sotto.
  La ragione vera è un'altra, e riguarda il merge: la sua ricerca risolve una riga in staging con
  `VolumeId = ? AND DirectoryId = ? AND Name = ? ORDER BY Id LIMIT 1`, **una volta per file** di ogni
  lotto, e oggi quell'`ORDER BY` è **gratis** perché `Id` è il rowid e dentro un gruppo
  `(VolumeId, DirectoryId)` le voci sono già in quell'ordine. Allargando, `Id` smette di essere la
  chiave successiva — servirebbe un sorter per ogni riga in staging — e ogni voce del gruppo
  porterebbe tre colonne in più da leggere mentre la si scandisce cercando il nome. Si è preferito
  pagare un ottavo B-tree, il cui costo è stato **misurato e trovato invisibile**, piuttosto che
  toccare il percorso più caldo di un re-scan sulla base di un ragionamento non misurato.
  L'alternativa è scritta accanto al codice invece che nascosta: chi un giorno misurerà quel percorso
  potrà prenderla.
- **E il piano che NON deve cambiare ha ora una guardia** (`ScanMergePlanTests`, secondo rilievo
  della review): la ricerca del merge viene esplicitata dal SQL che il writer emette davvero e deve
  restare `SEARCH f USING INDEX IX_Files_VolumeId_DirectoryId (VolumeId=? AND DirectoryId=?)`, senza
  `TEMP B-TREE FOR ORDER BY`. Serve perché quel guasto sarebbe **silenzioso**: i re-scan
  rallenterebbero e basta, con la suite verde.
- **L'aggregato della lista esce dal controller** e va in `Business/Dashboard/VolumeFileCounts`,
  accanto a `CatalogTotals` e `VolumeTotals` a cui somigliava già: una query scritta inline in un
  controller si può misurare **solo** con un test che ne tiene una propria copia, il che dimostra che
  la copia è pianificata bene — non il prodotto (l'argomento di 14a).

**Verifica.** xUnit **813 verdi** (+6), build backend pulita (warnings-as-errors). Frontend non
toccato. **I test asseriscono il PIANO, non i millisecondi**, e deve essere il piano: il contatore di
righe visitate di 11e/14a vive in una **view** sopra `Files`, e una view **forza** proprio l'accesso
alla tabella la cui assenza è il punto. **RED con l'indice tolto**: entrambi i piani ricadono su
`IX_Files_VolumeId_DirectoryId` e i due test cadono, mentre i tre di equivalenza restano verdi.
L'equivalenza sta accanto al costo: gli stessi contatori, nei casi che 11h ha reso distinti — un file
**escluso** e uno **assente** escono dal conteggio per porte diverse, e un volume senza nulla di
indicizzato mostra comunque **zero** invece di sparire dall'elenco.

**Sul ferro, su una copia del catalogo reale** (742 033 file, 22 volumi; il più grande ne ha 739 421),
attraverso l'API, mediana di 5 a caldo. «Prima» è il Host costruito da `6a8fb4e` in un worktree
separato, **sulla stessa copia**, misurato **per primo** perché è la migration di questo giro a
creare l'indice.

| endpoint | step 13 | prima (oggi) | dopo |
|---|---|---|---|
| `GET /api/dashboard` | 373 ms | 252 ms | **106 ms** |
| **`GET /api/volumes`** | **1 571 ms** | 1 163 ms | **37 ms** |
| **`GET /api/volumes/{id}`** | **1 768 ms** | 1 246 ms | **139 ms** |

Entrambi sotto i 300 ms chiesti dal task, con un margine largo. I numeri di oggi sono più bassi di
quelli dello step 13 sulle stesse query perché quelli furono presi su un servizio sotto carico a
cache fredda: ciò che conta è la **colonna prima contro la colonna dopo**, prese di fila sulla stessa
copia. La **Dashboard migliora senza essere stata toccata** — aggrega la stessa tabella con lo stesso
filtro, quindi eredita l'indice.

**Quanto costa l'indice, misurato invece che affermato.** *Costruirlo*: sul catalogo vero non è
separabile dal rumore di avvio — il build nuovo su una copia vergine serve in **2,7 s** contro i
**3,8 s** del build precedente che la migration non ce l'ha. *Tenerlo aggiornato*: A/B con l'harness
sul ferro (`D:\Collaudo\A`, 2 002 file, tre passate per build, alternate) — primo scan **1,33–1,67 s**
prima e **1,24–1,52 s** dopo, re-scan **0,80–1,13 s** prima e **0,81–0,93 s** dopo: nessuna
differenza attribuibile. *Spazio*: il file del database resta **731 619 328 byte** identico, perché
l'indice riusa le pagine libere. Harness completo sul ferro (`D:\Collaudo\A` ↔ `C:\Collaudo\B`):
**47 scenari, 47 PASS, 0 FAIL**; `appsettings.json` dell'harness rimesso byte-identico
(sha256 `653f5990…` verificato).

**Limiti noti e accettati:**
- **La misura dello scan è su 2 002 file**, tre ordini di grandezza sotto il volume in cui un ottavo
  B-tree per riga si vedrebbe. È la stessa avvertenza che 11e scrisse per i propri indici, e vale
  identica: quel numero dice «non c'è una regressione grossolana», non «costa zero».
- **Il costo in scrittura non è solo l'insert** *(rilievo della review, preso)*: l'UPDATE del merge
  su una riga ritrovata scrive `DirectoryId`, `SizeBytes`, `IsIncluded` e `IsPresent` — quattro delle
  colonne di questo indice — e `FilterReconciler` scrive `IsIncluded` su un intero volume. Ognuna di
  quelle riscrive anche la voce dell'indice, **dentro** la transazione che tiene l'unico write lock
  di SQLite. L'A/B dell'harness misura proprio quel percorso (il re-scan **è** il merge), ma a
  2 002 file.
- **Il conteggio delle cartelle del dettaglio non è coperto.** `Directories` filtra
  `IsMaterialized`, `IsPresent` e `PendingState`, e allargare `(VolumeId, ParentId)` per coprirlo
  vorrebbe dire portare `PendingState` — una **stringa** — dentro la chiave: è la stessa decisione
  che 11e prese e lasciò scritta. Su 114 132 directory il residuo è dentro i 139 ms, quindi non è
  stato pagato quel prezzo per un numero che è già sotto obiettivo.
- **`USE TEMP B-TREE FOR GROUP BY` resta nel piano del dettaglio**: è il `GroupBy(_ => 1)` di
  `CatalogTotals`, cioè **un solo gruppo**. Toglierlo cambierebbe l'idioma condiviso di §«un
  aggregato per tabella» per guadagnare una riga di temporaneo.
- **Nessun `ANALYZE`**, come il task vincola: tutte le misure valgono per un database senza
  statistiche, che è quello che l'applicazione produce.
- **Il servizio installato non è stato aggiornato**: la misura è su una copia, e distribuire il nuovo
  build sul catalogo reale resta una decisione dell'utente (voce aperta da 14a).

### Fatto nello step 14b (2026-08-25, commit `dec0f21`…`d7a60ee`)
**Una lettura annullata smette di lavorare, invece di smettere soltanto di essere attesa.** Chiude la
voce n° 2 dello step 13: la conseguenza che il difetto di 14a aveva rivelato, cioè che **un guasto di
prestazioni diventava un guasto di spegnimento**.

- **La causa, in una riga**: `Microsoft.Data.Sqlite` implementa `DbCommand.Cancel()` come **no-op** e
  non guarda più il token una volta che `sqlite3_step` è partito. `RequestAborted` era quindi onorato
  fra un `await` e l'altro e in nessun altro posto. Sul servizio installato: **+147 s di CPU in 119 s
  di orologio** dopo che il client si era sganciato, per oltre venti minuti, e uno `sc stop` fermo in
  `StopPending` per **oltre 270 s** contro i 30 s che il §3 promette.
- **Il ponte è `sqlite3_interrupt`**, e la sicurezza sta nella **vita della registrazione**: dura
  esattamente quanto la chiamata ADO.NET. Un interrupt sollevato mentre nessuno statement gira è un
  **no-op documentato**, e `CancellationTokenRegistration.Dispose` **blocca** finché una callback in
  volo non è rientrata: quando il metodo ritorna, nessun interrupt può essere ancora in aria contro
  una connessione che sta per tornare nel pool. **Niente qui tocca la vita dei pool** — l'incidente
  di 11i non è raggiungibile da questo codice. `SQLITE_INTERRUPT` viene **tradotto** in
  `OperationCanceledException`, così ASP.NET Core e il logging di EF lo leggono per ciò che è (§9), e
  la traduzione è condizionata al fatto che sia stato **questo** guard a sparare.
- **Il perimetro è un allow-list su `CommandSource`, e la review ha dimostrato perché non poteva
  essere dedotto.** La prima versione ragionava così: «le scritture passano dal ramo non-query, e
  comunque portano una `DbTransaction`». **Sono false entrambe insieme**: EF manda un batch di
  `SaveChanges` attraverso `ExecuteReaderAsync` (deve rileggere `changes()`/`RETURNING`) e, con
  `AutoTransactionBehavior.WhenNeeded`, un batch da **un solo** comando **non apre alcuna
  transazione**. Cioè esattamente la forma del checkpoint della coda: `JobExecutionEngine` salva una
  riga di item con un token annullabile. Uno shutdown avrebbe potuto interrompere un checkpoint — il
  solo divieto esplicito del task. Ora si guardano **solo** `LinqQuery` e `FromSqlQuery`; il test non
  assume la premessa, la **asserisce** (il batch viaggia sul ramo reader, non ha transazione, e non
  è guardato). Resta anche il secondo recinto indipendente: una lettura che porta una
  `DbTransaction` non si tocca.
- **Chi passa `CancellationToken.None` viene lasciato finire.** È deliberato in tutta la coda, e
  resta deliberato: se il token del chiamante non è annullabile, il guard **non viene nemmeno
  installato** e non costa nulla.
- **`WHEN` il segnale di shutdown si accende è metà del progetto.** `ApplicationStopping` era il
  momento sbagliato: viene alzato mentre **ogni worker gira ancora** su un token non ancora
  annullato, quindi una lettura interrotta lì dentro lancia una `OperationCanceledException` che i
  filtri `when (ct.IsCancellationRequested)` dei worker **non intercettano** — uno stop pulito
  durante una scansione sarebbe finito a log come *«Scansione fallita»* con tanto di Notification a
  video: un'interruzione travestita da errore, cioè il contrario del §9. Il segnale viaggia ora su un
  hosted service registrato **prima di ogni worker**: i servizi si fermano in ordine inverso, quindi
  si accende **dopo che ogni worker è stato fermato**. È la fessura in cui viveva l'attesa da 270 s:
  i worker non ci sono più, e ciò che tiene il servizio sono le richieste HTTP in volo.
- **E l'ordine di stop è ora *dichiarato*, non ereditato** (`ServicesStopConcurrently = false`).
  **Due** garanzie di questo host sono garanzie di ordine — il drain della coda di log per ultimo
  (11c) e questo segnale — ed entrambe sono nulle, in silenzio, se i servizi si fermano in parallelo.
  Il test lo ha dimostrato prima ancora del codice: la prima versione guardava una sonda fermarsi per
  prima e perdeva quella corsa **circa una passata su tre**. Ora asserisce l'**invariante**
  (registrato prima di ogni `BackgroundService`, stop sequenziale in ordine inverso) invece del
  comportamento emergente. Nota per chi fosse tentato di tornare alla sonda: in un TestServer
  ri-ospitato perfino `GenericWebHostService` finisce in una posizione di registrazione diversa da
  quella di produzione, quindi l'ordine emergente **lì** non è l'ordine di produzione.
- **Anche i percorsi raw sono guardati** — Ricerca e lista Log costruiscono `SqliteCommand` a mano e
  non passano dagli interceptor di EF. Il log store riceve il segnale **dal costruttore**: è
  costruito prima che il container esista, perché è il sink attraverso cui logga anche l'avvio. E il
  **ciclo del reader gira dentro il guard**, non solo `ExecuteReaderAsync`: quella chiamata esegue
  **solo il primo** `sqlite3_step`, e su una scansione `LIKE` ogni riga successiva costa un altro
  pezzo di tabella.

**Verifica.** xUnit **807 verdi** (+11 su 796), build backend pulita (warnings-as-errors). Frontend
non toccato. **RED dimostrato quattro volte**, e l'unità non è il millisecondo ma le **righe
visitate** (`Files` ombreggiata da una view che porta una UDF, la tecnica di 11e/14a — con
un'aggiunta: **è la UDF stessa a cancellare**, così l'annullamento cade *dentro* uno statement in
esecuzione e non quando il thread pool si degna, che è la differenza fra provare una cosa e sperarla):
- guard tolto → una ricerca annullata percorre **400 000 righe invece di 1 000** (lo statement di
  conteggio arriva in fondo e solo allora il controllo pre-esecuzione di ADO.NET si accorge del
  token), e una lettura EF **400 000 invece di 1 000**;
- allow-list tolta → il batch `SaveChanges` **viene guardato**;
- segnale rimesso su `ApplicationStopping` → la sonda che sta per i worker lo trova **già acceso**.

I controlli restano verdi in tutti e quattro i casi: una scrittura annullata a riga 1 000 di 200 000
**inserisce comunque tutte le righe**, una lettura in transazione **cammina tutte** le 200 000, una
query mai annullata dà **le stesse righe** per 50 volte di fila, e il guard costa **104 B per
comando** (0 quando il token non è annullabile).

**Sul ferro, su una copia del catalogo reale** (742 033 file, 731 MB — mai il database del servizio),
attraverso l'API, con **150 letture pesanti concorrenti** (`a` su path più un filtro di estensione
che non combacia: l'insieme dei match va percorso tutto). «Prima» è il **Host costruito da `9ce3691`**
in un worktree separato, stessa macchina, stessa copia, stesso script.

| | prima | dopo |
|---|---|---|
| CPU mentre serve | +55,2 s in 5 s | +48,7 s in 5 s |
| **CPU dopo che il client si è sganciato** | **+19,1 s in 20,0 s** | **+1,6 s in 20,0 s** |
| stop con 150 letture in volo | 7,1 s | 7,1 s |

La prima riga dice che il carico era lo stesso; la seconda è il difetto e la sua chiusura — circa
**un core** che continuava a girare per nessuno, contro nulla. È la stessa firma dei +147 s in 119 s
dello step 13.

**Sul tempo di stop va detta la verità intera**: nella coppia sopra vale **7,1 s in entrambi i
casi**, perché su una macchina a riposo quelle 150 letture finiscono comunque prima che il drain le
aspetti. Il numero grosso si è visto una volta sola e **non in coppia**: sulla stessa macchina in
stato degradato (quello documentato dal 12b, creazione processi a decine di secondi) il build
**pre-14b** ha impiegato **109,5 s** dal segnale, contro i **~1,7 s** del build nuovo in una prova a
6 concorrenti. Vale come indizio, non come misura appaiata, ed è scritto qui invece di essere
presentato come tale. Ciò che è **provato** è la garanzia sottostante: il segnale dell'host raggiunge
uno statement in corso (1 000 righe su 100 000, attraverso il grafo DI vero) e si accende **solo dopo
che i worker sono fermi**.

**Limiti noti e accettati:**
- **`TestServer` non trattiene l'host sulle richieste in volo** come fa Kestrel, quindi nessun test
  in-process può riprodurre onestamente l'attesa da 270 s. I test provano il meccanismo e l'ordine;
  il cronometro sta sul ferro.
- **Per le query EF il guard copre il primo `sqlite3_step`**, non l'intero reader: fra una riga e
  l'altra il token è già onorato da ADO.NET, e una singola riga molto costosa (un `ORDER BY` che
  materializza) è coperta perché quel lavoro sta tutto nel primo step. I due percorsi raw sono invece
  guardati per l'intero ciclo.
- **`sc stop` sul servizio installato non è stato rieseguito**: distribuire il nuovo build sul
  catalogo reale resta una decisione dell'utente (voce aperta da 14a). La misura è su una copia, con
  l'Host in console e un `CTRL_BREAK` — che passa dalla **stessa** sequenza di stop del generic host.
  *(Nota operativa costata un'ora: `CTRL_C` non arriva affatto se lo si manda da una shell il cui
  gruppo di processi ha il Ctrl+C disabilitato, e fa sembrare bloccato uno spegnimento che nessuno
  ha mai chiesto.)*
- **Nessuna migration, nessuna colonna nuova**: questo giro non tocca lo schema.
- **Harness sul ferro non rieseguito** in questo giro: nessuno di questi fix cambia il comportamento
  su file veri, e il task non lo chiedeva.
- **Due rossi isolati, entrambi pre-esistenti e su test temporali**, incontrati durante le passate
  ripetute di questo giro: `SqliteConnectionPoolScopeTests` (il probe di 11i, l'unico test che la sua
  nota dichiara possa cadere per lentezza) e `RootsBySpecificityTests.Resolving_a_million_items_allocates_nothing`
  (il contatore di allocazioni di 11e, che sotto carico raccoglie anche allocazioni non sue). Verdi
  in isolamento; nessuno dei due tocca il codice di questo giro.

### Fatto nello step 14a (2026-08-24, commit `df2ec7c`…`986bbf6`)
**Il filtro della Ricerca costa quanto il risultato, non quanto l'insieme dei match.** Chiude il
difetto più grave trovato dal soak dello step 13: `report` + categoria `Image` restituiva 2 righe in
12 s, e `e` + `Image` non tornava affatto. Più il filtro era selettivo, peggio andava — l'esatto
contrario di ciò che l'utente si aspetta premendo un chip.

- **La prima riga di lavoro è stata `EXPLAIN QUERY PLAN` sullo statement vero**, su una copia del
  catalogo reale, come il task chiedeva — e ha smentito per metà il sospetto scritto nel task. Non
  c'è **un** difetto, ce ne sono **due**, e il secondo è quello che rende il caso peggiore
  *illimitato* invece che soltanto lento.
  1. La query di **pagina** guida dall'indice e risolve **ogni** match su `Files` per tenerne una
     manciata: il costo segue il match set. È l'ipotesi del task, ed è giusta.
  2. La query di **conteggio** fa di peggio. Con un predicato di **uguaglianza su una colonna
     indicizzata** il pianificatore **inverte l'ordine di join**: guida da `IX_Files_Category` e
     chiede alla tabella FTS «il rowid X combacia?» una volta per riga candidata. Ognuna di quelle
     domande **rifà la query full-text** — per un termine prefisso, rimerge l'intera doclist. Il
     costo smette di essere «match set per una costante» e diventa «match set per la query, di
     nuovo»: 17 588 rimerge della doclist di `"e"*`. Di lì il «non torna».
- **Il fix toglie il predicato invece di renderlo veloce.** La tabella FTS5 guadagna una terza
  colonna, `tags`, che non contiene parole: contiene i **token sintetici** della riga (`ftcimage`,
  `ftv3`), e il filtro diventa un congiunto del `MATCH`. L'indice fa l'**intersezione di due
  doclist**, che è la cosa per cui esiste.
- **Solo categoria e volume diventano token, e il vincolo è la correttezza, non la fatica.** Un
  token è sicuro solo dove i valori sono un dominio chiuso che sopravvive **intatto** al tokenizer:
  la categoria è un enum, il volume un int, quindi ciascuno mappa a **un** token, iniettivamente — e
  insieme sono **esattamente ciò che la schermata Ricerca può produrre** (gli unici due controlli di
  filtro che esistono). L'**estensione** no: sul catalogo vero **639 delle 1 150 estensioni
  distinte** contengono caratteri su cui il tokenizer spezza (`_ - [ ]` e peggio), quindi qualunque
  codifica leggibile fa **collidere due estensioni diverse** — e un filtro che restituisce in
  silenzio le righe sbagliate è peggio di uno lento. Estensione, dimensione e data restano predicati
  SQL ordinari.
- **Ciò che le protegge è l'ordine di join, pinnato** (`CROSS JOIN`). È la seconda metà del fix e
  non è decorazione: l'estensione è ancora un'uguaglianza su colonna indicizzata, cioè ancora un
  grilletto per l'inversione. Misurato sul catalogo vero, filtrando su un'estensione che non
  combacia con nulla: **739 ms** col pianificatore libero, **10 ms** pinnato, stessa risposta. Si
  rinuncia al caso in cui guidare da `Files` sarebbe davvero meglio — che non è una perdita vera:
  quella forma paga solo se SQLite conosce la selettività, e **questo database non esegue mai
  `ANALYZE`** (11e), quindi sceglierebbe su una stima di default, contro una tabella virtuale il cui
  costo per sonda non vede affatto.
- **Due dettagli che sarebbero stati bug silenziosi.** (a) Ogni `MATCH` dell'utente è ora
  **scopato per colonna** (`{name}` / `{name path}`): un MATCH non scopato cerca in **tutte** le
  colonne, quindi senza questo digitare `ftcimage` avrebbe selezionato ogni immagine. Sulle due
  colonne preesistenti la forma scopata e quella nuda significano la stessa cosa, quindi **nessuna
  ricerca che l'utente possa digitare cambia risposta**. (b) `bm25` prende ora pesi espliciti per
  colonna con **0 sui tag**: un token sintetico che combacia non deve poter spostare l'ordine di
  rilevanza di una ricerca filtrata rispetto alla stessa ricerca senza filtro.
- **La migration non riscrive le righe, e non è pigrizia.** Una tabella FTS5 non ha
  `ALTER TABLE … ADD COLUMN` (la lista di colonne fa parte del layout delle shadow table), quindi
  l'unico modo è ricrearla; ripopolarla **dentro** la migration vorrebbe dire una seconda copia del
  SQL del nome proiettato e dei tag, e §5 è esplicito che quelle regole hanno **una** definizione.
  L'indice resta vuoto e lo riempie il backfill di avvio **che esiste già**. Le migration girano
  prima del backfill, quindi la finestra vuota non raggiunge mai una richiesta.
- **Verifica**: xUnit **796 verdi** (+5 su una baseline **misurata** di 791 su questa macchina —
  il brief dello step 13 diceva 792, il numero vero qui è 791), build backend pulita
  (warnings-as-errors). Frontend non toccato, quindi nessun `ng build`.
  Harness sul ferro (`D:\Collaudo\A` ↔ `C:\Collaudo\B`): **47 scenari, 47 PASS, 0 FAIL**, eseguito
  **due volte**, prima e dopo il giro di review; `appsettings.json` rimesso byte-identico
  (sha256 `653f5990…` verificato).
  **RED dimostrato prima di ogni fix**, e i due difetti hanno richiesto **due strumenti diversi**:
  il primo si misura in **righe visitate** (`Files` ombreggiata da una view che porta una UDF
  contatore, la tecnica di 11e/E3) — **5 010 righe per un risultato di 5** prima, **10** dopo; il
  secondo **non è visibile** a quel contatore, perché la forma invertita tocca **meno** righe di
  `Files` e paga dentro la tabella virtuale, dove SQLite non espone alcun contatore. Quello si
  asserisce sul **piano**, riletto dal SQL che il prodotto ha davvero emesso
  (`CapturingSqliteConnection` registra gli statement mentre eseguono) e non da una copia incollata
  nel test, che proverebbe solo che la copia è pianificata bene. RED con il loop più esterno che
  diceva `SEARCH f USING INDEX IX_Files_Extension`: **la stessa inversione del catalogo vero,
  riprodotta in-process**.
- **Equivalenza, prima delle prestazioni.** Nei test: undici combinazioni di filtri confrontate con
  la risposta calcolata **direttamente da `Files`**, stesse righe nello stesso ordine. Sul catalogo
  vero: il conteggio via tag e quello via predicato SQL confrontati per **tutte e sei le categorie**
  e **tutti e 22 i volumi** — identici ovunque. `e` + `Image`, che il codice vecchio non ha mai
  restituito, dà **508** in entrambe le forme.

#### I numeri, rimisurati sul catalogo vero
Su una **copia** del catalogo reale (742 033 file, 731 MB — mai il database che il servizio usa),
attraverso l'**API**, con lo stesso corpo di richiesta che manda la schermata Ricerca
(`scope: Name`, `sort: Relevance`, `take: 50`), a regime (WAL checkpointato) e mediana di 5
esecuzioni. «Prima» è il **Host costruito dal commit precedente**, in un worktree separato, sulla
stessa macchina e sulla stessa copia: non i numeri di un'altra sessione.

| query | risultato | prima | dopo |
|---|---|---|---|
| `report` | 431 righe | 105 ms | **55 ms** |
| `report` + categoria `Image` | 2 righe | **2 772 ms** | **50 ms** |
| `e` (tappato a 10 000) | 10 000 | 582 ms | **471 ms** |
| `e` + categoria `Image` | 508 righe | **mai** (arreso a 300 s, due volte) | **90 ms** |

Le righe restituite coincidono con quelle dello step 13 (431 e 2), cioè è la stessa query; i
millisecondi assoluti sono più bassi dei 517/12 123 di allora perché il soak misurava un servizio
sotto carico su cache fredda, questa è una copia a riposo. **Il percorso senza filtro non regredisce
— migliora**, perché l'indice ricostruito è contiguo. *(Una prima passata sembrava dire il
contrario: era stata misurata con un WAL da 67 MB non ancora checkpointato, lasciato lì dall'host
ucciso subito dopo il rebuild. Il numero era vero e descriveva la cosa sbagliata.)*

Rimisurato sul **build finale** (dopo la correzione della review, che aggiunge un COUNT dell'indice
all'avvio ma non tocca la ricerca) gli stessi quattro danno **53 / 35 / 316 / 93 ms**: stessi
risultati, stesso ordine di grandezza.

**Quanto costa l'aggiornamento, una volta sola**: sullo stesso catalogo il primo avvio dopo
l'update impiega **9,9 s** a servire, di cui **6,9 s** sono il rebuild dell'FTS (il resto è
migration e checkpoint), contro **2,6 s** di un avvio normale sullo stesso database. L'indice torna
a **742 033** voci esatte, quante sono le righe indicizzabili: non se ne perde nessuna. Per quei
~7 s è la **ricerca** a non essere disponibile, non il catalogo. Il log lo dice in chiaro —
*«Search index is short — 0 entries for 742 033 indexable files. Rebuilding.»* e poi *«Search index
rebuilt in 6.9 s.»* — e al secondo avvio **quella riga non compare**, che è la prova che la guardia
a senso unico non ricostruisce a ogni avvio su un database sano.

La **code review finale** (indipendente, sulle modifiche di questo giro) ha trovato **due cose**.
Una era **grave e introdotta da questo giro**, ed è del tipo peggiore: silenziosa, permanente, e
invisibile proprio alla schermata che dovrebbe accorgersene. La migration lascia l'indice **vuoto**
e delega il riempimento al backfill di avvio — il che è sicuro **solo se nessuno mette una riga
nell'indice vuoto prima**. Qualcuno lo fa: `OverlayWriter.ReconcileOrphansAsync` gira **prima**,
nello stesso avvio, e ri-sincronizza i file di cui ha appena ripulito l'overlay orfano. Con la
guardia «c'è almeno una riga?» quelle poche righe rendevano l'indice non-vuoto, il rebuild veniva
**saltato**, e la ricerca rispondeva da una manciata di righe — quell'avvio e **tutti quelli
successivi**, senza una riga di log e con il Catalogo perfettamente corretto, quindi senza niente
che indicasse la causa. Lo scatenante è un'installazione il cui ultimo arresto ha lasciato un
overlay pendente: **esattamente il caso per cui quella riconciliazione esiste**.
Riordinare i due passi chiuderebbe *quell'interazione*; **fare la domanda giusta chiude la classe**,
e si porta via anche l'altro buco che questo giro apre per la prima volta — un rebuild **interrotto
a metà** (~10 s su un catalogo vero, quindi interrompibilissimo) lascia lo stesso indice non-vuoto e
incompleto che «è vuoto?» non riparerebbe mai. Quindi `IsEmptyAsync` è diventata `CountEntriesAsync`
e il chiamante la confronta con il numero di righe che nell'indice **devono** stare. **A senso
unico, di proposito**: corto vuol dire ricostruisci, lungo no — una voce stantia per una riga poi
esclusa o sparita è già invisibile (`SearchAsync` filtra `IsIncluded`/`IsPresent`), quindi trattare
«più voci che righe» come danno non comprerebbe nulla e rischierebbe un rebuild completo a **ogni**
avvio. Il rebuild ora **logga** entrambi i numeri prima e la propria durata dopo: uno che si ripete
dev'essere visibile, non solo lento. RED dimostrato **sul percorso vero** (un overlay orfano vero,
la riconciliazione vera, l'initializer vero): **1 voce dove ne servono 20**. L'ordine dei due passi
è stato **lasciato com'è**, con il motivo scritto accanto: la riconciliazione azzera le colonne
`Pending*`, che fanno parte del **nome proiettato** che quell'indice contiene (§5), quindi un
rebuild messo prima indicizzerebbe nomi che il passo immediatamente successivo invalida.
La **seconda** non sopravvive alla misura e **non è stata applicata**: sosteneva che pinnare
l'ordine di join impedisce anche un `ORDER BY` soddisfatto da un indice di `Files`, quindi gli
ordinamenti per data e dimensione avrebbero iniziato a ordinare l'intero match set. Sul catalogo
vero, ordinando `e` (match set di centinaia di migliaia) per Data, Dimensione e Nome, `JOIN` e
`CROSS JOIN` producono il **piano identico** — `SCAN fts`, seek per rowid, `USE TEMP B-TREE FOR
ORDER BY` — e tempi identici (~165–245 ms in entrambi i casi). SQLite quell'ordinamento non lo stava
mai facendo in streaming da un indice di `Files`: il pin non toglie niente.
Verificati dalla review e trovati puliti: **ogni** scrittore di `Files.Category` / `Files.VolumeId`
(il merge dello scan, `RenameFileIndexAsync`, tutti e tre i call site di `RepointToVolume`,
`FilterReconciler`, `OverlayWriter`) è seguito da una risincronizzazione dell'FTS, quindi i tag
**non possono diventare stantii** — era la classe di rischio principale; il `{0}` ripetuto in
`UpsertAsync` (EF usa `string.Format`, quindi entrambi i buchi legano lo stesso `@p0`) ed entrambi i
suoi call site che girano **dopo** `SaveChangesAsync`, quindi la sotto-query legge la riga salvata;
la precedenza della colspec FTS5; e l'iniettività dei token.

**Limiti noti e accettati:**
- **Estensione, dimensione e data restano O(match set)**, non O(risultato): non possono diventare
  token (le prime due ragioni sono scritte sopra; una **gamma** non è un token per costruzione).
  Il pin le tiene lontane dalla forma patologica, e nessuna delle tre ha oggi un controllo nella
  schermata Ricerca — la dimensione lo dice già §«giro date/UTC», l'estensione è solo API.
- **L'indice è più grande**: due token sintetici per riga, ~1,5 milioni su questo catalogo. Non ha
  spostato la dimensione del file del database in modo misurabile (731 619 328 byte prima e dopo,
  perché il rebuild riusa le pagine liberate), ma su un catalogo che cresce è spazio vero.
- **Un `MATCH` non scopato non esiste più**, e chi aggiungesse un quarto percorso di ricerca deve
  ricordarlo: dimenticare le graffe rende la colonna `tags` cercabile dall'utente.
- **Il prefisso corto resta caro**: `e` da solo costa 471 ms perché FTS5 espande il prefisso su
  tutti i termini che iniziano per «e». Un `prefix='1,2,3'` sulla tabella lo abbatterebbe al prezzo
  di un indice molto più grande; non è stato fatto qui, non è il difetto di questo giro.
- **La guardia del backfill costa un COUNT dell'indice a ogni avvio** (~250–335 ms sul catalogo
  vero). È il prezzo della domanda giusta; l'alternativa economica è quella che ha prodotto il
  difetto sopra.
- **E2E non eseguiti**: il loro `globalSetup` si rifiuta di partire da elevato (12a) e questa
  sessione era elevata per fermare il servizio e leggere il catalogo reale.
- **Due rossi isolati e non riprodotti**, entrambi mentre un **secondo processo** girava la stessa
  suite sullo stesso albero (l'agente di review): il nome del test non è stato catturato perché il
  logger console non lo stampa in questa lingua. Cinque passate consecutive successive, con trx,
  **796/796 verdi ogni volta**. Non è archiviato come «flakiness accettata»: è scritto qui perché
  chi lo rivedesse sappia che il sospetto è la contesa sull'albero (la nota di 11i), non il codice
  di questo giro.
- **Il servizio installato non è stato aggiornato**: gira ancora il build precedente sul catalogo
  reale, che non è stato toccato (sha256 verificato identico prima e dopo il giro). Distribuire il
  nuovo build — e con esso pagare i ~10 s di rebuild sul database vero — è una decisione dell'utente.

### Fatto nello step 13 (2026-08-22, commit `76e0154`…`01be0a4`)
**Il prodotto è installato come servizio, gira sul catalogo vero, e la domanda aperta sull'USN ha
una risposta misurata — che non è quella che ci si aspettava.** Primo passo fuori dall'MVP: non
aggiunge funzionalità, mette alla prova le due fondamenta che nessun test aveva mai toccato.

- **Il database ha una casa sola, e il servizio la trova.** `DatabaseLocation` puntava a
  `%LOCALAPPDATA%`; come servizio il Host gira da `LocalSystem`, per cui quel percorso diventa
  `C:\Windows\System32\config\systemprofile\AppData\Local` — il servizio sarebbe partito su un
  catalogo **vuoto**, accanto a quello costruito dalle esecuzioni in console, senza che niente lo
  dicesse. Ora il default è **`%ProgramData%\FileTracert`** (decisione dell'utente, opzione (a) delle
  tre poste dal task): machine-wide, lo stesso file che il Host giri come servizio, come console
  elevata o sotto un altro account. **Nessun codice migra niente**: spostare il catalogo di un utente
  è un'operazione deliberata a servizio fermo, non qualcosa che un host decide all'avvio, dove un
  file copiato a metà è un catalogo corrotto. L'harness aveva una **seconda copia** della convenzione
  e non è duplicazione qualunque: il suo guard legge quel database per sapere quali cartelle
  contengono dati catalogati, quindi un percorso divergente non avrebbe sbagliato un path — avrebbe
  dichiarato «qui non c'è niente di catalogato» di un drive pieno di roba dell'utente. Ora la
  derivazione è una sola, con un test che scandisce i sorgenti perché non ne ricompaia una seconda.
- **Migrazione fatta una volta sola, a servizio fermo, copiando.** `filetracert.db`
  (731 619 328 byte, sha256 `3729AD6C…` prima e dopo), `-wal`, `-shm` e il DB dei log. Conteggi
  verificati identici alla sorgente: **742 033 file · 114 132 directory · 742 033 righe FTS · 15
  volumi · 7 cartelle monitorate · 28 job**; log 500 144 righe. L'originale in `%LOCALAPPDATA%`
  **resta dov'è** come rete di sicurezza. Prima di avviare il servizio si è controllata la cosa che
  conta: **tutti e 28 i job sono terminali** (22 Completed, 4 Cancelled, 2 Failed) e **zero overlay
  pendenti**, quindi il `QueueProcessorWorker` non aveva un solo file da spostare. Questo giro
  **indicizza e basta**.
- **Publish, install, uninstall.** `dotnet publish` esegue anche `ng build` e porta la SPA dentro
  `wwwroot` **come parte della build** (29 file), con un `Error` che fa fallire la publish se
  `index.html` non c'è: un eseguibile che serve una pagina bianca non è un artefatto installabile.
  Il glob di default non poteva essere usato — è valutato **prima** che il target giri, quindi
  avrebbe pubblicato la build precedente e mancato quella appena fatta. `deploy\install-service.ps1`
  registra il servizio (LocalSystem, avvio **automatico**, riavvio 3×60 s poi stop), copia in
  `%ProgramFiles%\FileTracert` con `robocopy /MIR` — così un aggiornamento non lascia dietro un
  `chunk-*.js` che il nuovo `index.html` non nomina più — e **aspetta che il servizio risponda**.
  `uninstall-service.ps1` **lascia il database**: è dell'utente, disinstallare non è chiedere di
  buttarlo (`-RemoveData` lo cancella, dopo aver scritto la parola per esteso).
  Alla cartella dati si concede **Modify a `Users`**, deliberatamente: scrive `LocalSystem`, ma lo
  stesso file deve poter essere aperto da una console elevata o dall'harness, che girano come
  l'utente connesso.
- **Verificato sul ferro**: servizio `Running`/`Automatic` da `C:\Program Files\FileTracert`, UI
  `200` con il token nel `<meta>`, `/api/dashboard` che risponde **742 033 file / 468 861 553 711
  byte**, `401` senza token, in ascolto **solo** su `127.0.0.1` e `::1`. **Stop e start provati**:
  stop pulito in 31,2 s (WAL checkpointato a zero), start di nuovo servente in 0,3 s.
  **Il riavvio vero della macchina NON è stato fatto** — questa sessione ci girava sopra: resta
  all'utente, ed è l'unica cosa che manca alla prova dell'avvio automatico.

#### La misura che il giro esisteva per produrre
Stesso volume, stesso insieme di file, database usa-e-getta, radice del volume come unica cartella
monitorata. `D:` è NTFS, 931,5 GB, **59 627 voci di MFT → 51 710 file + 6 782 directory** indicizzati
da entrambi i motori (numeri identici, il che è metà della prova).

| motore | primo scan | re-scan | fase Enumerating | fase ReadingMetadata | fase Writing |
|---|---|---|---|---|---|
| **USN** (elevato) | **4,88 s** | 3,67 s | 0,57 s | 0,53 s | 3,17 s |
| **Enumerazione** (non elevato) | **3,74 s** | 3,43 s | 1,06 s | ~0 | 2,10 s |

Sottoalbero della **stessa** unità, 2 000 file in 20 cartelle: USN **1,15 s** (poi 0,79 s),
enumerazione **0,86 s**. L'USN ha camminato 61 648 voci di MFT per indicizzarne 2 000.

**La risposta: l'ipotesi è per metà giusta, e la conclusione è che oggi l'USN non conviene mai.**
Giusta la parte sul costo fisso: la camminata dell'MFT **non** dipende dal perimetro, e quando il
perimetro è l'intero volume è **il doppio più veloce** della camminata delle directory (0,57 s
contro 1,06 s). Sbagliata la conclusione che ne seguiva, perché l'USN paga un secondo costo che
l'enumerazione non ha, ed è **proporzionale ai file, non fisso**: i record dell'MFT non portano
**dimensione né date**, quindi `ScanService.ResolveFilesAsync` interroga il filesystem **file per
file** attraverso `IFileMetadataReader` — la fase `ReadingMetadata`, 0,53 s per 51 710 file, che sul
ramo a enumerazione semplicemente non esiste (le dimensioni arrivano gratis dalla camminata).
Il conto non si chiude con la scala: il vantaggio della camminata (≈8 µs a voce) e il prezzo dei
metadati (≈10 µs a file) sono **dello stesso ordine**, quindi crescono insieme. L'USN vincerebbe
solo dove il filtro **scarta** gran parte del volume (un file scartato non paga la lettura dei
metadati) — cioè non nel caso d'uso di questo prodotto, dove si sorvegliano cartelle di media.
**Non è una regressione ed è la scoperta importante del giro**: la scelta di §1.2 va rivista, e la
strada non è ottimizzare, è **leggere le dimensioni dall'MFT** invece che con una `stat` per file
(`FSCTL_ENUM_USN_DATA` non le espone: servirebbe leggere `$STANDARD_INFORMATION`/`$DATA`).
Nota di igiene: i **7,56 s** su 2 002 file del collaudo del 2026-08-21 **non sono stati riprodotti**
(1,15 s qui, stesso ordine di lavoro): quel numero non descriveva una proprietà stabile del motore.

#### I quattro comportamenti USN del §2, uno per uno
1. **Primo scan via MFT — osservato.** Su un volume di test creato per l'occasione (VHDX NTFS
   montato su `T:`, giornale **assente**) `EnsureJournal` lo ha **creato** e lo scan è girato sul
   motore `UsnJournal`. La ricostruzione dei path dai `ParentFileReferenceNumber` è stata verificata
   **a campione sul catalogo vero di `D:`**, non a fiducia: **60 righe su 60** risolvono a un file
   che esiste sul disco; nel verso opposto **38 su 40**, e i 2 mancanti sono file creati **dopo**
   quello scan (verificato), quindi 40 su 40 spiegati.
2. **Delta incrementale — NON esiste nel prodotto.** `IUsnReader.ReadChanges` **non ha alcun
   chiamante** fuori dai test (grep su `src/`); `ScanWorker` prende solo i volumi con
   `LastFullScanUtc == null` più le richieste esplicite; `ScanService` fa **sempre** snapshot pieno +
   merge. Il `UsnSyncWorker` che il §3 nomina fra i BackgroundServices **non è mai stato scritto**.
   La metà **piattaforma** funziona, e da questo giro è asserita: un test crea, rinomina e cancella
   un file **fuori dall'applicazione** e pretende di ritrovare tutti e tre nel delta con la ragione
   giusta (RED dimostrato facendo partire `ReadChanges` dalla coda corrente invece che dal cursore
   del chiamante: **cade solo quel test**, gli altri cinque restano verdi).
3. **`LastUsn` ripreso dopo il riavvio — non ripreso, perché nessuno lo legge.** Provato:
   catalogo a 501 file, host fermo, due file creati + uno rinominato + uno cancellato **fuori
   dall'app** (il giornale avanza da 0 a 0x3b0), host riavviato con sync volumi a 10 s e poll di
   scansione a 5 s, lasciato in pace **75 s** → catalogo ancora 501, `LastUsn` fermo, nessuna
   scansione nel log. Una ri-scansione **esplicita** converge perfettamente (502 presenti, **1
   assente** per il file cancellato — mai un delete, §6 —, il rinominato sotto il nome nuovo,
   `LastUsn` = 944 = esattamente il `NextUsn` del giornale) ma passando da una camminata **completa**
   dell'MFT.
   **Lacuna di schema trovata qui**: `Volumes` persiste `LastUsn` ma **non l'id del giornale**, e
   `ReadChanges` ne ha bisogno per accorgersi dell'invalidazione. Chi scriverà il worker incrementale
   deve prima aggiungere quella colonna.
4. **Journal azzerato → osservato, solo sul volume usa-e-getta.** Mai su `C:`, mai su `D:`. Id prima
   `0x01dd318acf43c59f`; `fsutil usn deletejournal /d T:` → «giornale non attivo»; un file creato
   fuori dall'app mentre il giornale non c'era; scansione successiva del prodotto → giornale
   **ricreato** con id nuovo (`0x01dd318c745af494`), scansione completa, e il file nuovo **recepito**
   (500 → 501). Nessun fallimento silenzioso, nessun indice dichiarato aggiornato senza esserlo.
   L'altra faccia dello stesso guasto — handle di volume rifiutato — è il **fallback a enumerazione**
   documentato, osservato due volte nei log delle passate non elevate con la sua Notification.

#### Soak sul catalogo vero
- **La retention dei log funziona e recupera davvero lo spazio.** Il DB migrato conteneva **500 144
  righe / 184 467 456 byte**, tutte di luglio: al primo avvio `LogRetentionWorker` ha tolto tutto
  ciò che superava i 14 giorni e ha fatto `VACUUM` → **20 480 byte**. Dopo 2 h 31 m di servizio:
  **431 righe / 139 264 byte** (401 Information, 13 Warning, 17 Error). Il tetto non è la crescita
  libera ma `LogMaxRows` = 500 000, che a `MinimumLogLevel = Trace` sono ~184 MB: è il soffitto, ed è
  quello che lega per primo.
- **Il WAL resta piccolo**: 61 832 byte quello del catalogo, 70 072 byte quello dei log dopo ore di
  esercizio (`WalCheckpointWorker` ogni 120 s). Del WAL da 555 MB citato nelle opzioni, nessuna
  traccia.
- **Il catalogo non cresce da solo e nessuno scansiona di sua iniziativa**: 731 619 328 byte
  invariati, perché ogni volume aveva già `LastFullScanUtc` e quelli senza non hanno cartelle
  monitorate attive. È la verifica che la «regola di sicurezza» del task chiedeva.
- **Memoria piatta**: RSS 166 MB, privata 73 MB, 33 thread, 722 handle dopo 2,5 h.
- **Le schermate su 742 033 file**, misurate sugli endpoint che chiamano: Dashboard **373 ms**;
  **lista volumi 1 571 ms** e dettaglio volume **1 768 ms** (oltre il secondo, entrambi);
  coda 52 ms; notifiche 23 ms. Ricerca per nome: `report` (431 risultati) **517 ms** a freddo,
  284 / 64 ms poi; `e` (tappato a 10 000) **3 910 ms**; per path `a` **543 ms**.
- **Il difetto che il soak ha trovato, ed è il più grave del giro: il filtro per categoria costa
  come l'insieme dei match, non come il risultato.** `report` + categoria `Image` restituisce **2
  righe** in **12 123 ms** (contro 517 ms senza filtro), e `e` + `Image` **non è mai tornato** — il
  client ha rinunciato a 180 s e poi a 300 s.
- **E la sua conseguenza, scoperta pagandola: una query che non finisce non si annulla, e tiene lo
  shutdown.** Sganciato il client, il servizio ha continuato a bruciare **oltre un core** per più di
  venti minuti (misurato: +147 s di CPU in 119 s di orologio), e uno `sc stop` è rimasto
  **`StopPending` per oltre 270 s**, cioè molto oltre lo `ShutdownTimeout` di 30 s che il §3 promette.
  `RequestAborted` non arriva dentro uno statement SQLite già in corso.
- **Verifica finale**: xUnit **792 verdi** (+6: cinque su `DatabaseLocation`, uno sul delta USN),
  Vitest **244 verdi**, build backend pulita (warnings-as-errors), publish del Host con SPA ok.
  **E2E non eseguiti**: il loro `globalSetup` si rifiuta di partire da elevato, per scelta di
  progetto (12a), e questa sessione era elevata per necessità.
- **Nota operativa costata cara**: su una macchina in cui la creazione di processi era degradata a
  13–40 s (la stessa patologia documentata dal 12b), `Start-Service` ha sfondato il timeout di avvio
  dell'SCM e ha lasciato il servizio `Stopped`; `sc start`, che non aspetta, è andato a buon fine.
  Un avvio fallito con quella firma va letto come «macchina in ginocchio», non come servizio rotto.

**Cosa il soak lascia come lavoro successivo**, in ordine di gravità: ~~(1) il filtro per categoria
della Ricerca~~ *(chiuso allo step 14a)*; ~~(2) l'annullabilità delle query lunghe, che oggi trasforma
un guasto di prestazioni in uno di shutdown~~ *(chiusa allo step 14b)*; ~~(3) i due endpoint di Volumi oltre il secondo~~ *(chiusa allo step 14c)*; (4) le
dimensioni dall'MFT, senza le quali il motore USN non ha un motivo per esistere; (5) la colonna con
l'id del giornale, senza la quale il worker incrementale non si può nemmeno cominciare.

### Collaudo elevato — il motore USN esercitato per la prima volta (2026-08-21)
Fino a qui **ogni** numero del progetto descriveva il motore a **enumerazione**: xUnit, harness ed
E2E giravano non elevati, e `NtfsUsnReaderTests` — che richiede l'elevazione — **ritornava subito**,
cioè passava a vuoto. Sessione aperta come amministratore, su richiesta dell'utente, per chiudere
il buco. Nessun codice toccato.
- **Journal preesistenti su entrambi i volumi** (`fsutil usn queryjournal C:` e `D:`): non è stato
  creato nulla, e gli **ID journal sono identici prima e dopo** il collaudo — nessuna modifica
  persistente ai dischi.
- **xUnit 786/786 verdi da elevato** (1 m 57 s). I cinque `NtfsUsnReaderTests` isolati costano
  **17 s**: un ritorno a vuoto costerebbe millisecondi, quindi lo snapshot MFT di `C:` è stato
  percorso davvero. È la prima prova che `NtfsUsnReader` funziona sul ferro.
- **Harness 47/47 PASS** su `D:\Collaudo\A` → `C:\Collaudo\B` (272 s), e stavolta **sul motore
  USN**: zero occorrenze di *«falling back to enumeration»* nel log, contro **17** nel log degli E2E
  non elevati della stessa giornata e della stessa macchina. È il contrasto che rende la prova
  positiva invece che un'assenza.
- Tempi con USN, 2 002 file: primo scan **7,56 s**, re-scan (merge) **2,07 s**. Sono **più lenti**
  dei numeri storici a enumerazione (1,05 s / 0,83 s allo step 9a) e la differenza va indagata prima
  di trattarli come baseline: su un volume piccolo il costo fisso di `FSCTL_ENUM_USN_DATA` (che
  cammina l'**intera** MFT del volume, non la sola cartella sorvegliata) può dominare — cioè
  potrebbe essere il comportamento atteso e non una regressione. Serve una misura su un volume con
  molti più file per saperlo.
- `appsettings.json` dell'harness ripristinato byte-identico (sha256 `653f5990…`), scratch puliti
  dall'harness, nessun residuo.

### Fatto nello step 12b (2026-08-20, commit `d01f455`…`885380c`)
**L'MVP è coperto fino allo schermo, e lo step 12 è chiuso.** I flussi che il prodotto promette —
Catalogo, Ricerca, accodamento con **proiezione**, Coda, **realtime** — sono guidati da Playwright
sul prodotto assemblato, con il Host vero, il socket vero e file veri.

- **Il primo commit non è un test: è il recinto** (`tests/e2e/src/fence.ts`). 12a aveva chiuso
  scrivendo che la sandbox proteggeva la suite, non il prodotto: i suoi flussi **leggevano**
  soltanto. Qui i test accodano operazioni che il vero `JobExecutionEngine` esegue su un volume che
  la suite rende catalogabile — su una macchina di sviluppo, il **disco di sistema**. Una
  destinazione sbagliata non è più un'asserzione rossa: è un file dell'utente che si sposta. Quindi
  il contenimento si **verifica**, in tre strati, uno per ogni modo di uscire:
  **(1) perimetro** — l'unica cartella monitorata della macchina è la sandbox, quindi nessun id
  sorgente può nominare un file fuori; ed è la *risposta del servizio* a dirlo, non le due chiamate
  che il test ha fatto. **(2) destinazione** — volume e path di ogni enqueue (e di ogni
  `watched-root`, che è l'altro modo di allargare il perimetro) sono controllati **prima** che la
  richiesta raggiunga il Host: sia quando parte dal test (`Api.enqueue` **pretende** il fence come
  parametro, quindi non esiste un modo di chiamarlo senza) sia quando parte dalla **SPA**, perché
  il contesto del browser le intercetta. Una richiesta pulita passa **intatta**: non si finge
  niente. **(3) audit** — in teardown, mentre il Host risponde ancora, ogni job che il servizio ha
  **registrato** viene riletto e controllato; *cross-volume* è una violazione di per sé, perché è
  l'unico percorso che manda un file nel **cestino**. I primi due sono la promessa, il terzo è la
  prova; ognuno fallisce col path incriminato e nessuno corregge niente in silenzio.
  `specs/sandbox-fence.spec.ts` prova a uscire da tutti e tre.
- **Lezione imparata rompendo il recinto.** Il modo per dimostrare che quello spec non è vuoto è
  disabilitare il fence e guardarlo diventare rosso — e mentre è disabilitato i tentativi vengono
  spediti **davvero**. Il primo giro li puntava su `Windows\Temp`: nessun file è finito lì (l'hanno
  fermati le ACL di Windows, non il progetto), ma è stata la dimostrazione a diventare l'incidente
  che il recinto esiste per evitare. Ora i tentativi puntano **un livello sopra** la cartella
  monitorata, dentro `.artifacts`: fuori dal recinto esattamente quanto serve, innocuo se il
  recinto un giorno cede.
- **La proiezione osservata senza spegnere niente.** Il §5 chiede uno stato intermedio
  («il file è già nella destinazione, col badge»), che con la coda accesa dura millisecondi. Non è
  stato introdotto un interruttore per fermare il `QueueProcessorWorker` — il prodotto non ne ha
  uno e inventarlo per un test sarebbe stato scope creep: la destinazione **contiene già** una voce
  col nome verso cui il file si sta spostando, quindi l'engine parte, rifiuta di sovrascrivere e
  parcheggia il job `Blocked(NameCollision)`, che per §5 **conserva** l'overlay. La voce che
  collide è una **cartella**, così l'elenco file della destinazione ha esattamente una riga con
  quel nome e l'asserzione non può essere soddisfatta da quella sbagliata.
- **Il push provato davvero** (la lacuna che 10b e 10c avevano messo per iscritto). Dallo step 10c
  le schermate non hanno timer: leggono una volta all'apertura e poi sono **patchate dall'hub**.
  Quindi ogni cambiamento asserito è provocato **fuori** dal browser, via HTTP, e nessuno ricarica
  niente: se il socket non portasse il messaggio, la schermata resterebbe com'era. La connessione
  persa si prova **spegnendo il Host** — l'emulazione offline di Chromium lascia in piedi un
  WebSocket già aperto, quindi l'unico modo onesto di togliere l'hub è fermare il servizio che lo
  serve, che è anche il caso che l'utente incontra. Poi il browser viene tenuto offline mentre il
  servizio torna e il lavoro accade: così la corsa col retry da 15 s del client sparisce e la riga
  che compare dopo «Riconnetti» può venire **solo** dalla rilettura.
- **Verifica**: xUnit **786** verdi, Vitest **244** verdi, build backend pulita
  (warnings-as-errors), `ng build` ok (restano i 4 warning di budget SCSS, pre-esistenti). Harness
  sul ferro (`D:\Collaudo\A` → `C:\Collaudo\B`): **47 scenari, 47 PASS, 0 FAIL** — identico alla
  baseline di 11h, e ci si aspettava esattamente questo, perché **nessun file di prodotto è stato
  toccato** (`git diff b648df9..HEAD -- src/` è vuoto); `appsettings.json` dell'harness rimesso
  byte-identico (sha256 `653f5990…` verificato).
  E2E: **24 verdi su 24** in una passata piena da 7,2 min *prima* del giro di review. Dopo le
  correzioni della review la passata **non è stata rifatta ×3**, e il motivo va scritto invece che
  nascosto: a metà lavoro la macchina è entrata in uno stato in cui la **creazione di processi**
  costa 1–7 secondi invece di ~20 ms — misurato più volte cronometrando `cmd.exe /c exit`, con CPU,
  RAM e disco scarichi. Con un rallentamento di quell'ordine ogni test sfonda il proprio budget da
  180 s e perfino `ng build` supera i 15 minuti che il `globalSetup` concede alla build. Un ultimo
  tentativo con `--timeout=900000` (solo da riga di comando: **la configurazione committata non è
  stata toccata**) ha portato **6 test verdi su 7**, con singoli test da 2 a 8 minuti; il settimo è
  caduto su un `actionTimeout` di 20 s aspettando che la lista volumi del Catalogo comparisse, che
  è la stessa lentezza vista da un'altra angolazione. **Alzare i timeout committati per far passare
  la suite sarebbe stato il modo sbagliato di rispondere**, quindi non è stato fatto.
  Cosa resta verificato dopo le correzioni: `tsc --noEmit` pulito, i sette test sopra, e ogni spec
  verde uno per uno mentre la macchina era ancora sana.
  **Debito chiuso il 2026-08-21**: a macchina sana (~27 ms a spawn, rimisurato) la suite completa è
  **25 verdi su 25 per tre passate di fila** — 311 s con build, poi 271 s e 301 s con
  `FT_E2E_SKIP_BUILD=1`. Nessun Host residuo, DB reale dell'utente non toccato (timestamp invariati).
  **Nota su come si lancia**, costata una passata rossa da 22 minuti: `npm test` va eseguito da una
  shell **con console**. `scripts/stop-host.ps1` spegne il Host con `AttachConsole` +
  `GenerateConsoleCtrlEvent`, e da una shell staccata (background, senza console) non c'è nessuna
  console a cui agganciarsi: il Ctrl+C non arriva, ogni test muore sul teardown con *«The Host (pid …)
  did not stop within 45000 ms»* e **tutti e 25 falliscono per la stessa ragione, che non è la loro**.
  Un fallimento di massa con quel messaggio va letto come «lanciata male», non come regressione.
  **RED dimostrato quattro volte** rompendo il prodotto e ripristinandolo: tolta la scrittura
  dell'overlay all'enqueue → **2 rossi** (il badge non compare in Catalogo e in Ricerca), 8 verdi;
  tolto l'instradamento di `JobStateChanged` nel `RealtimeBridge` → **1 rosso** (la Coda non vede
  comparire il job), 7 verdi; tolta la rilettura alla riconnessione → **1 rosso** (la schermata
  resta vuota al ritorno), 3 verdi; disabilitato il fence → **2 rossi** su 3 nello spec del
  recinto.
La **code review finale** (indipendente, sulle modifiche di questo giro) ha trovato **cinque** cose,
tutte corrette. **Due erano bloccanti e riguardavano il recinto**, cioè esattamente il punto in cui
sbagliare costa un file dell'utente. (a) Il perimetro aveva **un cancello e due strade**: il
controllo guardava solo le destinazioni, mentre una *cartella monitorata* puntata altrove avrebbe
messo i file dell'utente a portata di un'operazione con destinazione perfettamente legale — ora
anche i `watched-root` passano dal fence, dal test e dalla schermata Setup allo stesso modo (e
`volumes.spec.ts`, che monitora *dallo schermo* senza passare da `watchSandbox`, lega il fence per
quel motivo: senza, il gate nuovo faceva diventare rossa una prova del 12a — ed è successo davvero
in una passata). (b) Il corpo di una richiesta veniva **letto** in camelCase e **legato** da
ASP.NET Core senza distinguere le maiuscole: un `TargetVolumeId` maiuscolo significava tutto per il
servizio e niente per il fence, che vedeva un `undefined`, saltava il controllo del volume e
avrebbe lasciato passare un **cross-volume** — l'unica forma che manda un file nel cestino. Ora
l'insieme delle chiavi si controlla **prima** di leggerci dentro, il volume di destinazione è
obbligatorio e non «controllato-se-c'è», i due punti in un path sono rifiutati (drive o alternate
data stream) e il contenimento si chiede una seconda volta al resolver di Node sul path assoluto.
Le altre tre: l'audit stava sulla fixture `api`, quindi uno spec che `api` non lo chiede poteva
accodare senza che nessuno rileggesse dove (spostato su `host`); un'asserzione **eco** nel Catalogo
(«il numero di cartelle non si è mosso» è vero anche di un contatore fermo a zero, e quel test è
l'unico che parla per 11h → ora c'è un minimo prima dell'invarianza); e un gruppo di minori (la
pagina dell'audit che poteva essere più corta della coda, un body illeggibile che *bloccava* la
richiesta invece di fallire per nome, il filtro data che prendeva il giorno dall'orologio invece
che dal file, e il prefisso `[SANDBOX FENCE]` usato per un Host irraggiungibile).
**Cosa resta scoperto da questo livello, e perché** (meglio scritto che immaginato coperto):
- **Il percorso USN.** La suite E2E si rifiuta di partire da elevato (12a): senza privilegi la
  scansione ripiega sull'enumerazione. **Per l'harness il limite è stato tolto il 2026-08-21**
  (vedi «Collaudo elevato» qui sotto): resta vero per gli E2E, per scelta.
- **Tutto ciò che è cross-volume**: `JobProgress` e quindi la **barra di avanzamento di una copia**,
  la prenotazione di spazio, `Blocked(InsufficientSpace)` col deficit e il margine, e il passaggio
  dal **cestino**. Servirebbe un secondo volume, cioè scrivere fuori dalla sandbox: resta coperto
  dall'harness sul ferro e da xUnit. Sullo schermo se ne copre il parente più vicino: il
  **preview** (§7, «senza creare il job»), che è l'unico posto in cui uno spec può puntare a un
  altro volume perché non crea niente — e che dice *«Richiesto 200 B + 6 B di margine»*.
- **Drive esterni e volumi offline veri**: staccare un drive non è simulabile (lo dice già 10a).
- **La Dashboard non reagisce ai `JobStateChanged`** resta un limite noto di 10c, non un buco di
  copertura.

### Fatto nello step 12a (2026-08-19, commit `9da54f1`…`76fdebf`)
Il **terzo livello della piramide** esiste: `tests/e2e`, Playwright su Chromium, che guida **il
prodotto assemblato**. Copre avvio/autenticazione, Dashboard, Volumi (cartelle monitorate e
filtri) e una scansione vera. Catalogo, Ricerca, Coda e realtime sono il **12b**.
- **Il browser parla con il Host, non con `ng serve`** — questa è la decisione che struttura tutto
  il resto. Stessa origine, token letto dal `<meta name="ft-token">` che il Host timbra su
  `index.html`, WebSocket diretto verso `/hubs/events`. È il prodotto: `ng serve` + proxy sarebbe
  stato più rapido da montare e avrebbe provato il percorso **dev** del token con un proxy in
  mezzo al socket.
- **Un Host per test**, su un database vuoto, ~4 s a testa. I contatori della Dashboard sono
  **globali**: condividere un database vorrebbe dire scrivere asserzioni valide solo in un certo
  ordine di esecuzione. Nessun `webServer` di Playwright — non saprebbe dare un Host per test.
- **Avvio e spegnimento passano da due `.ps1`**, e non per gusto. Node non può avviare il Host
  direttamente: `spawn({detached:true})` su Windows lo lascia **senza console**, un figlio
  non-detached **condivide** quella del runner (un Ctrl+C per il Host colpirebbe Playwright), e
  `Start-Process` con redirezione su file passa al Host **ogni handle ereditabile** — compresa la
  pipe del runner, che resterebbe aperta finché vive il servizio: il pid viaggia quindi in un
  file, non su stdout. Lo spegnimento è **CTRL_C_EVENT** (SIGINT per .NET): `taskkill` senza `/F`
  non ferma un'app console senza finestra, e con `/F` è `TerminateProcess` — la sequenza di stop
  dello step 11c non verrebbe eseguita e il test non proverebbe nulla. **Il test fallisce se il
  Host non si spegne**, e anche se l'evento non è stato consegnato ma il processo è morto lo
  stesso.
- **Isolamento, in ordine di gravità:** mai il DB reale (`FileTracert:DatabasePath` dentro
  `.artifacts`, **e** si verifica che quel file esista davvero prima di eseguire i test — un
  binding rotto farebbe migrare il catalogo dell'utente in silenzio); mai la porta 5005 (range
  dedicato 5180–5279, con un guard che scatta se qualcuno lo allarga); mai un file fuori dalla
  sandbox (`Sandbox` cancella solo sotto `.artifacts`, e lo stesso guard vale per setup e
  teardown). Il perimetro del catalogo è la sola cartella-sandbox: gli altri volumi restano come
  li ha classificati il prodotto e **senza cartelle monitorate non vengono mai scansionati**.
- **Da elevato la suite non parte.** Il motore si sceglie dal filesystem, non da un'impostazione:
  su NTFS la scansione *prova sempre* USN, e con i privilegi `EnsureJournal` **creerebbe** un
  giornale sul volume di sistema (modifica persistente fuori dalla sandbox, fatta da un test)
  mentre lo snapshot camminerebbe l'intera MFT. Non elevati, Windows rifiuta e il prodotto ripiega
  sull'enumerazione della sola cartella monitorata. Il `globalSetup` lo verifica e si ferma.
- **Nessuna sincronizzazione a tempo**, e **zero retry** (un retry nasconde esattamente la classe
  di difetti che questo livello esiste per trovare). Gli stati **transitori** non si aspettano:
  un `MutationObserver` installato *prima* dell'azione registra la comparsa della barra di
  scansione, così l'asserzione non corre contro la fine del lavoro che ha appena avviato.
- **Un difetto del prodotto trovato dai test, e lasciato lì di proposito.** La schermata Volumi
  **auto-seleziona** il primo volume catalogabile appena la lista arriva, e applica quella scelta
  quando torna *la sua* richiesta. Il clic dell'utente ne manda una seconda: **vince la risposta
  che atterra per ultima**, quindi su una macchina carica la selezione torna da sola sul primo
  volume mentre l'utente sta guardando l'altro. È esattamente ciò che ha fatto cadere la seconda
  delle tre passate (`76fdebf`). Il fix è della schermata, non del test, e sta fuori dal 12a: qui
  il test smette solo di **asserire attraverso** una selezione che il prodotto può spostare.
- **Verifica**: E2E **9 verdi ×3 di fila** (~2,2 min a passata, build inclusa); xUnit **786**
  verdi, Vitest **244** verdi, build backend pulita (warnings-as-errors), `ng build` ok (restano
  i 4 warning di budget SCSS, pre-esistenti). **RED dimostrato** due volte rompendo il prodotto:
  tolto il timbro del token nel `<meta>` → 3 su 3 rossi, con la shell che dice *«servizio non
  raggiungibile»*; tolta l'emissione di `ScanProgress` → **solo** il test della scansione rosso
  («il flag non è mai comparso»), gli altri verdi. Prodotto ripristinato in entrambi i casi.
La **code review finale** (indipendente, sulle modifiche di questo giro) ha trovato **nove** cose,
tutte corrette nel commit `8046d7c`: un Host il cui avvio fallisce non veniva mai registrato per
la pulizia (teneva porta e database, e il run successivo partiva su una cartella sporca); la
pulizia d'emergenza non poteva funzionare (`process.on('exit')` esegue solo lavoro **sincrono**,
`execFile` no); il caso elevato di cui sopra; l'assenza di un'asserzione dietro la regola del
database; la corsa sullo stato transitorio della scansione; uno spegnimento che riportava
«pulito» quando l'evento non era stato consegnato; due asserzioni **eco** (il filesystem
confrontato col valore che l'API aveva dato alla schermata, e il token con una seconda lettura
dello stesso tag); «Attiva» ambiguo tra la pill di un root attivo e il bottone offerto su uno
sospeso; più i minori (EPERM letto come «processo morto», guard di cancellazione solo in
teardown, shell che spezzava gli argomenti di build sugli spazi, export morti, lockfile che
`npm ci` rifiutava, nessun typecheck).
**Limiti noti e accettati:**
- **La corsa dell'auto-selezione su Volumi resta aperta** (vedi sopra): è un difetto del prodotto,
  candidato ai work package minori o al giro che tocca quella schermata.
- **Non prova il percorso USN.** Vale per scelta (vedi sopra) e vale anche per l'harness.
- **La prima passata di `ScanWorker`** all'avvio del Host corre contro l'`addWatchedRoot` del
  test: oggi vince il test perché quel primo ciclo gira mentre la tabella dei volumi è ancora
  vuota. Non è un difetto ora; se un domani uno spec aggiungesse un root più lentamente, la prima
  asserzione a rompersi sarebbe *«Ultima scansione … mai»*.
- **`FT_E2E_SKIP_BUILD=1` controlla che gli artefatti esistano, non che siano aggiornati.**
- ~~**La sandbox protegge la suite, non il prodotto.**~~ — **chiusa allo step 12b**, che ha
  costruito il **recinto** (`tests/e2e/src/fence.ts`) prima di scrivere il primo test che accoda.

### Fatto nello step 11h (2026-08-19, commit `b0090c1`…`460495c`)
**Una riga ricorda perché è esclusa, e la riconciliazione disfa solo ciò che ha diritto di
disfare.** Chiude il debito che 11g aveva creato consapevolmente più le due lacune collegate.
Lo scenario del difetto, in chiaro: rendi nascosta `Photos\Private`, scansiona (righe
`IsIncluded=0`, `IsPresent=1`, giusto), poi **allarga il filtro dei tipi** — e il contenuto della
cartella nascosta ricompariva nel Catalogo fino alla scansione successiva, perché la
riconciliazione vedeva un'estensione ammessa e non sapeva nulla della cartella.
- **Tre flag booleani, non un enum** *(la decisione del giro)*: `ExcludedByType`,
  `ExcludedByRoot`, `ExcludedByScan` su `Files` (§6). Le cause **si sommano** — un `.tmp` dentro
  una cartella nascosta è escluso due volte — e con un valore solo avresti dovuto definire una
  **precedenza**, dopo la quale «disfa il filtro dei tipi» diventa una domanda ambigua: la riga è
  *anche* fuori perimetro? Con un flag per causa ognuna si spegne per conto suo e
  `FilterReconciler` — dove si paga il conto — non deve tradurre nulla in aritmetica di bit che EF
  poi deve saper tradurre in SQL. `IsIncluded` **resta** una colonna propria (derivata,
  `!(a||b||c)`): è quella che leggono Catalogo, FTS e i due indici covering, e un OR di tre
  booleani a ogni seek non è quello che si vuole sul percorso caldo.
- **Il backfill è pessimista, e lo è di proposito.** Le righe esistenti `IsIncluded=0` non sanno
  perché lo sono: la migration le timbra tutte `ExcludedByScan`, cioè **la causa che la
  riconciliazione non disfa mai**, così nulla rientra in silenzio. Il prezzo: una riga che in
  realtà era solo fuori dall'allow-list resta esclusa una scansione in più. L'errore opposto —
  indovinare «tipo» e riammettere il contenuto di una cartella nascosta — è esattamente il difetto
  che si sta chiudendo, ed è **invisibile all'utente**. Il merge azzera tutte e tre le cause quando
  rivede il file, quindi il primo scan corregge. C'è un test che applica davvero le migration alla
  versione precedente, scrive righe come le scriveva quella versione e migra in avanti.
- **La scansione consegna la causa, non un booleano.** `ScanPerimeter.SkipCause` distingue «nessun
  root **attivo** la governa» (→ `ExcludedByRoot`, disfabile dalle impostazioni) da «le regole di
  perimetro l'hanno rifiutata» (→ `ExcludedByScan`, disfabile solo da un'altra scansione);
  `Covers` è ora quella risposta letta come sì/no. Precedenza deliberata: si chiedono **prima i
  root**, perché un item fuori da ogni root attivo non è mai stato offerto al filtro e quindi non
  può essere stato «filtrato via». `SkippedScanArea` porta la causa e la chiusura fa **un UPDATE
  per causa presente**.
- **Il guard dell'esclusione non è più `IsIncluded`, è il flag della causa.** Una riga già esclusa
  per un altro motivo deve comunque **imparare** questo: senza, il `.tmp` nella cartella nascosta
  non riceveva il timbro dello scan e tornava dentro al primo allargamento del filtro. Il trucco
  dell'ordine di 11g regge lo stesso: l'esclusione gira per prima, le righe che timbra escono da
  sole dal pass degli assenti (che resta lo statement e il predicato esatti di sempre).
- **La riconciliazione risincronizza l'FTS** (seconda lacuna di 11g). Il Catalogo legge il flag e
  si riprendeva da solo; la Ricerca legge l'**indice**, che la chiusura di una scansione aveva già
  potato: riallargare un filtro produceva file navigabili e non trovabili. Fatto con l'API
  set-based che esiste già — `SyncDirectoriesAsync`, che nomina le righe **per directory**, quindi
  l'insieme non lascia mai il database e porta il nome proiettato di §5 dalla costante condivisa.
  Anche cancellare un watched root ora toglie le sue voci dall'indice invece di lasciarle
  rispondere alle query fino al prossimo scan.
- **Un root inattivo si decide in un posto solo.** Il branch è passato da `WatchedRootsService`
  dentro `ReconcileRootAsync`: `FilterSettingsService` itera **tutti** i root senza override, attivi
  o no, quindi un cambio di filtro globale azzerava in silenzio l'esclusione di un root che l'utente
  aveva spento.
- **UI (skill `impeccable`)**: «1.204 file · 87 cartelle» si leggeva come **un solo censimento** e
  da 11g sono due — il numero dei file risponde al filtro e al perimetro (spegni un root e va a
  zero), quello delle cartelle no, perché una cartella che esiste sul disco esiste. Due righe, ognuna
  che dichiara il proprio perimetro: **«Indice — N file inclusi»** (la parola che Setup usa già per
  quel flag) e **«Struttura — M cartelle nell'albero, comprese quelle senza file inclusi»**. La
  didascalia riusa il parentetico faint della riga «Disco fisico»: niente di nuovo inventato.
- **Verifica**: xUnit **786 verdi** (+21, di cui 6 dal giro di review), Vitest **244 verdi** (+1),
  build backend pulita (warnings-as-errors), `ng build` ok (restano i 4 warning di budget SCSS,
  pre-esistenti).
  **RED dimostrato prima del fix**: 5 test rossi su 6 nel file nuovo — il file sotto la cartella
  nascosta rientrava al primo allargamento del filtro (due varianti: filtro globale e switch di
  root), il `.tmp` con due cause rientrava disfacendone una, e le due metà FTS non trovavano più il
  file re-incluso; il sesto (il controllo: ciò che il filtro dei tipi esclude **deve** rientrare)
  era verde da subito. Sul frontend, 1 rosso.
  **Misura, statement non millisecondi** (`CountingSqliteConnection`, lo stesso attrezzo di 11g):
  chiusura della scansione **1** statement senza aree saltate (invariato), **6** con una causa
  (invariato), **7** con due — mai per riga; riconciliazione di un root **3** statement per i flag
  più **3** per l'indice (una SELECT degli id di directory e la coppia DELETE/INSERT), e **non si
  muovono** passando da 50 a 500 file sotto il root.
  Harness sul ferro (`D:\Collaudo\A` → `C:\Collaudo\B`): **47 scenari, 47 PASS, 0 FAIL**, eseguito
  **due volte**, prima e dopo il giro di review, con `exclusion-vs-absence` esteso alle asserzioni
  di questo giro su file veri. Costo di scansione **misurato in A/B contro l'albero pre-11h**
  invece che confrontato con numeri di sessioni passate: 2 002 file, primo scan 1,76–3,27 s /
  re-scan 1,08–1,19 s prima, 1,40–4,79 s / 0,95–1,74 s dopo. La macchina è rumorosa (C: è il disco
  di sistema, e la varianza dentro una singola sessione copre l'intero intervallo); nessuna
  differenza attribuibile al giro. `appsettings.json` dell'harness rimesso byte-identico
  (sha256 `653f5990…` verificato).
- **Trovato di passaggio e chiuso** (`a8f26e3`): `ScanLockContentionTests` è caduto **una volta su
  cinque** run pieni, verde in isolamento. Il messaggio conteneva la diagnosi — `0 succeeded` **e**
  `0 blocked`, cioè non «lo scrittore è rimasto fuori» ma «lo scrittore non è mai partito»: sotto
  carico il corpo del `Task.Run` poteva essere schedulato per la prima volta **dopo** la fine dello
  scan. Non è flakiness da archiviare (11i): un test che può diventare rosso per un motivo che non
  c'entra col write lock vale meno anche quando è verde. Ora l'hammer segnala prima del primo
  tentativo e lo scan aspetta quel segnale.
La **code review finale** (indipendente, sulle modifiche di questo giro) ha trovato **due cose
sullo stesso filo**, corrette nel commit `460495c`, più tre minori attorno.
Lo spostamento del guard dell'esclusione da `IsIncluded` alla colonna della causa è corretto
**solo finché l'invariante regge** — e un writer non la reggeva. `IndexUpdater.RenameFileIndexAsync`
sovrascriveva `IsIncluded` con `ShouldIncludeFile` e non toccava le tre cause: quella chiamata
conosce nome, attributi e path del **file**, non sa che una **cartella** sopra di lui è nascosta.
Il difetto era già documentato lì come «known gap», e dichiarato innocuo perché la scansione
successiva rimetteva a posto. **Qui smetteva di esserlo**: una riga con `ExcludedByScan=1` e
`IsIncluded=1` viene saltata dal pass di esclusione, cade in quello degli **assenti** e si prende
`IsPresent=0` — un file che sta sul disco dichiarato sparito, cioè esattamente il difetto che 11g
esisteva per togliere. Corretto **alla sorgente** (il rename ricalcola la causa che può decidere,
**alza** quella di perimetro se il nuovo path fallisce da solo le regole, non la **abbassa** mai, e
deriva `IsIncluded` dalle tre) e **di nuovo come rete sotto** (`OR IsIncluded = 1` nel guard):
nessun guard su un percorso a forma di perdita di dati deve dipendere da un'invariante mantenuta
altrove. Le minori: mancava un test di **idempotenza** della chiusura proprio nel giro che ne ha
cambiato il guard (aggiunto, più il caso della riparazione); il test del backfill asseriva il flag
ma non che il reconciler lo **onori** su una riga legacy (aggiunto); e il log «marked excluded»
contava anche le righe che imparavano soltanto una causa in più, quindi ora dice «recorded as
outside the scanned perimeter» invece di far credere che siano appena sparite dalla vista.
Verificati uno per uno e **senza rilievi**: l'invariante su ogni altro writer (merge, insert,
reconciler, `ToEntity`, bulk insert, migration, seed dei test), l'esaustività e disgiunzione delle
tre `ExecuteUpdate`, il caso radice-volume di `DirectoriesUnder` (`SubtreePrefix("")` è `"\"`, e
nessun `MaterializedPath` inizia con un separatore: il ramo speciale c'è ed è giusto), il fatto che
l'insieme dei file i cui flag si muovono coincide con quello delle directory risincronizzate, che
il sync FTS giri **dentro** la transazione del chiamante, e la precedenza di `SkipCause` (non
osservabile: `ExcludeSubtree` è raggiungibile solo dentro un root attivo).
**Limiti noti e accettati:**
- **`ExcludedPaths` continua a non avere una riconciliazione propria**: togliere un segmento
  escluso non riammette nulla senza scansione (le righe sotto non sono mai state scritte) e
  `FilterWidened` lo dice onestamente con `NeedsScan` da 11g. È la terza metà della voce di roadmap
  che questo step chiude per due terzi.
- **Il pass dell'FTS in riconciliazione è chunked per DIRECTORY** (500 id per coppia di statement):
  è piatto nel numero di **file** — che è ciò che un filtro riallargato accumula in massa — e cresce
  nel numero di **cartelle**. È il baratto che `SyncDirectoriesAsync` esiste per fare, ed è quello
  giusto: le cartelle sono ordini di grandezza meno dei file. **Gira però anche dove non
  servirebbe**: solo il *rientro* può lasciare voci mancanti, mentre le voci di troppo sono già
  invisibili (la `SearchAsync` filtra `IsIncluded`/`IsPresent` a ogni query), quindi restringere un
  filtro o spegnere un root paga un DELETE+INSERT che nessuno leggerà. Tenuto incondizionato perché
  lascia l'indice **uguale a come lo lascerebbe una scansione**, che è un'invariante più facile da
  difendere di «ricostruiscilo solo nei casi in cui serve».
- **Il numero «esclusi» del cambio filtro globale su un root spento è il totale, non il delta**:
  `FilterSettingsService` itera tutti i root senza override e per uno inattivo la riconciliazione
  ri-esclude l'intero sottoalbero. Il comportamento è quello giusto (è il buco cross-service che
  questo giro chiude); è solo il numero sulla schermata Setup a essere meno informativo.
- **Un root creato ex novo non riconcilia**: `CreateAsync` non chiama il reconciler, quindi le righe
  che portavano `ExcludedByRoot` da una cancellazione precedente restano escluse fino alla prima
  scansione. Pre-esistente, non toccato qui.
- **Nessun `BlockReason`-equivalente per la causa in UI**: le tre cause sono persistite e usate dal
  motore, ma il Catalogo mostra ancora soltanto «incluso / non incluso». Dire all'utente *perché*
  una riga è fuori è un lavoro di UI a sé.

### Fatto nello step 11i (2026-08-19, commit `7b3e938`…`776f4cf`)
**La suite dice la verità anche sotto carico.** La flakiness che 11e, 11f e 11g avevano
documentato senza poterla nominare — 1–2 test di integrazione `Host` persi a ogni run pieno, un
test **diverso** ogni volta, sempre verdi in isolamento, firma
`ObjectDisposedException: 'SQLitePCL.sqlite3'` — è chiusa **alla radice**, con la causa
**misurata** e non ipotizzata. Il prodotto non è stato toccato di una riga: la diagnosi di 11e era
giusta nel dire «il difetto è nei test», sbagliata nel dire *quale*.
- **La causa, provata.** `Microsoft.Data.Sqlite` mette le connessioni in pool **per connection
  string**, ma il registro dei pool appartiene al **processo**. `SqliteConnection.ClearAllPools()`
  quindi dispone l'handle nativo `sqlite3` di connessioni che non ha mai aperto — e xUnit esegue
  le classi in **parallelo**, per cui i cinque teardown che la chiamavano frugavano dentro
  qualunque altra classe stesse interrogando in quell'istante. Spiega ogni pezzo del sintomo:
  quale test cade è casuale, l'isolamento è sempre pulito, e l'eccezione nomina l'**handle nativo**
  invece della connessione gestita.
- **La prova sta in `tests/FileTracert.PoolProbe`, un eseguibile a parte**, e non in un `[Fact]`,
  per lo stesso motivo per cui esiste il fix: chiamare `ClearAllPools()` dentro il test host
  **sarebbe** il difetto. Dice due cose. Una **deterministica**, senza alcun timing: una
  connessione in pool ma inattiva possiede ancora il file, quindi «riesco ad aprirlo in esclusiva?»
  risponde a «il pool di questo database lo sta ancora tenendo?» — e la risposta è che il
  `ClearPool` di un *altro* database lascia il lock dov'era, `ClearAllPools()` lo toglie. Una **di
  corsa**: quattro thread che martellano ciascuno il proprio database mentre un quinto fa da
  teardown → `ObjectDisposedException` su `SQLitePCL.sqlite3` in **33–192 ms** (10 misure su 10,
  contro un budget di 30 s), con la variante mirata come **controllo**: stessi quattro thread,
  ~58 000 iterazioni, nessuno disturbato. `SqliteConnectionPoolScopeTests` lancia il processo
  figlio e asserisce su ciò che stampa.
- **Il fix è che ogni teardown chiude il *proprio* pool.** Ognuno dei cinque conosce il proprio
  database per path e `SqliteConnection.ClearPool` lavora su una sola connection string;
  `SqliteTestDatabase` è quella chiamata più la pulizia dei sidecar, in un posto solo, con il
  motivo scritto accanto. **Niente è stato serializzato e nessuna asserzione è stata tolta**: le
  classi continuano a girare in parallelo, smettono solo di frugare l'una nell'altra. Il pooling
  nei test **resta acceso**: spegnerlo cambierebbe ciò che si sta testando —
  `DatabaseInitializerTests` misura il **checkpoint del WAL**, che col pooling ha proprietà diverse
  — ed è proprio quel test a guadagnarci di più, perché il clear globale gli **checkpointava e
  cancellava il WAL sotto i piedi**: di lì il suo `FileNotFoundException` sul file `-wal`, che non
  somigliava affatto a un problema di connessioni.
- **Una seconda causa, indipendente, trovata misurando la prima.**
  `CatalogApiTests.GetChildren_excludes_absent_directories_but_keeps_the_ones_with_an_overlay`
  semina di proposito un job `Pending` **vivo** (da 9b un overlay senza job vivo viene riconciliato
  via) ma lasciava acceso il vero `QueueProcessorWorker`. Quel job è eseguibile: il worker lo
  prende, lo esegue contro un volume che esiste solo nel database, lo fa fallire e **azzera proprio
  l'overlay** che il test asserisce. È una corsa che la macchina vince quando è occupata, ed è per
  questo che sembrava il bug del pool: `GoneButQueued` semplicemente assente dalla risposta,
  nessuna eccezione da nessuna parte. Provata trattenendo l'asserzione di tre secondi — **rosso su
  ogni run in isolamento**, verde su ogni run col worker spento, che è ciò che `ProjectionApiTests`
  già fa per la stessa ragione.
- **Due test tengono onesto il risultato.** `ProcessWideStateGuardTests` scansiona i sorgenti
  cercando la **chiamata** (nomi e messaggi restano liberi di nominarla) e ammette esattamente due
  file, entrambi padroni del proprio processo: l'harness, single-threaded, e il probe. È **rosso
  sui teardown pre-fix** — verificato ripristinandoli. `SqliteTestDatabaseContractTests` copre il
  modo di fallire che il fix introduce: `ClearPool` libera il file solo se nomina il pool che EF
  Core e il log store hanno **davvero** aperto, e sbagliarlo è **silenzioso** — le delete
  semplicemente iniziano a mancare. Apre quindi ciascun database **come lo apre il prodotto**
  (`AddDataServices` per EF, `SqliteLogStore` per i log, entrambi costruiti da `DatabaseLocation`
  come fa `Program.cs`), chiude, pulisce e asserisce che il file sia sparito — senza host e senza
  scrittori di sfondo, quindi senza tempo di mezzo. Rosso dimostrato aggiungendo **un** parametro
  alla connection string: una chiave di pool che non combacia non libera niente.
- **La stringa di connessione è scritta a mano in un posto solo** (`SqliteTestDatabase`), che
  delega a `DatabaseLocation.ConnectionString`, e `FileTracertAppFactory.LogDatabasePath` chiama
  `DatabaseLocation.ResolveLogs` invece di riscriverla: un domani un parametro in più su quegli
  helper rende **rossi i contract test** invece di trasformare in silenzio ogni teardown in un
  no-op.
- **Verifica sotto carico, l'unica che conta qui.** «L'ho eseguita ed era verde» è la frase che
  questa flakiness ha già smentito tre volte, quindi il criterio è stato misurato con lo **stesso
  carico prima e dopo**: due `ng build` in loop sul frontend (~3 min l'uno sotto contesa, contro
  73 s da soli), 9–14 processi `node` vivi a ogni passata. **Prima del fix: 3 run falliti su 5**,
  tre firme diverse — `ProjectionApiTests` con `ObjectDisposedException` dentro
  `RelationalConnection.OpenAsync`, `DatabaseInitializerTests` con il `-wal` sparito,
  `CatalogApiTests` con la riga mancante (la seconda causa). **Dopo il fix: 24 passate consecutive,
  24 verdi, zero fallimenti** — 12 sul codice pre-review (769/769 ciascuna, 37–56 s di test) e 12
  sul codice finale (771/771 ciascuna, 46 s–1 m 44 s, contesa più pesante). Il paragone vale
  perché **il carico è lo stesso** che prima del fix faceva cadere 3 run su 5.
- **Verifica finale**: xUnit **771 verdi** (+6), build backend pulita (warnings-as-errors). Il
  frontend non è stato toccato. Harness sul ferro **non eseguito e non richiesto**:
  `ScenarioEnvironment` è rimasto identico — è un processo suo, single-threaded, e nessun test
  xUnit lo istanzia, quindi il suo `ClearAllPools()` non ha nessuno da disturbare. È la sola
  eccezione ammessa dal guard, che la nomina con il motivo.
La **code review finale** (indipendente, sulle sole modifiche di questo giro) non ha trovato
BLOCKER né MAJOR e ha trovato **cinque MINOR, tutti presi**. Il primo valeva il giro: il test di
teardown asseriva che dopo `FileTracertAppFactory.Dispose` **entrambi** i file fossero spariti,
mentre `SqliteTestDatabase.Delete` documenta la delete come *best effort* — e `%TEMP%` dà ragione
alla documentazione (1256 `ft-test-*-logs.db` residui contro 7 principali, 295 dei quali a **zero
byte**, cioè cancellati e poi **ricreati**). Sarebbe stato un rosso a caso ogni ~N passate,
proprio nel task che serve a farli finire; la verifica si è spostata dove è **deterministica**
(vedi sopra). Gli altri quattro: la stringa di connessione scritta a mano in cinque posti,
il guard che camminava `src/frontend` (`node_modules` e `dist`, proprio gli alberi che l'`ng build`
concorrente riscrive) invece del solo `src/backend`, lo strip dei commenti che passava `//` anche
dentro i literal — cioè l'unica cosa che il suo commento dice di non dover mai fare, ora ancorato
a inizio riga — e il BOM perso da `FileTracert.Tests.csproj`.
**Limiti noti e accettati:**
- **`%TEMP%` accumula `ft-test-*-logs.db`, ed è un difetto pre-esistente non chiuso qui.**
  `WebApplicationFactory.Dispose` **dispone** l'host senza **fermarlo**, quindi `LogFlushService`
  non drena mai e `SqliteLogProcessor` non viene disposto (il container non l'ha creato — lo dice
  già il suo commento): una scrittura di log ancora in volo riapre il file un istante dopo la
  delete. Misurato: 1256 residui accumulati da sessioni precedenti, **1 solo** prodotto dalle 12
  passate della campagna. Costa una `StopAsync` sul factory ed è materiale da giro proprio, non da
  un task sulle corse del pool.
- **`ScenarioEnvironment` continua a pulire tutti i pool.** Uniformarlo costerebbe una passata
  completa dell'harness sul ferro (è la regola del brief per chi lo tocca) a fronte di zero rischio
  reale. Il giorno in cui lo si tocca per altro, si cambi anche quello e si tolga dalla allowlist
  del guard.
- **La riproduzione della corsa è un test *temporale*.** Il margine è enorme (33–192 ms misurati
  contro 30 s di budget, più le 12 passate della campagna che l'hanno eseguita sotto carico), ma
  resta l'unico test della suite che potrebbe fallire per lentezza invece che per un difetto. Se
  un giorno lo facesse, il messaggio riporta l'intera trascrizione del probe.
- **Il guard è un'analisi testuale, non semantica**: intercetta `ClearAllPools(` fuori dai commenti
  e non intercetterebbe una chiamata costruita per riflessione o rinominata con un alias. È la
  forma di errore che si commette davvero — copiare un teardown esistente — non un aggiramento
  deliberato.

### Fatto nello step 11g (2026-08-19, commit `c8b0fff`…`ae8c679`)
**Esclusione e assenza sono due fatti diversi, e ora il database lo dice.** Implementa la
decisione di prodotto presa dall'utente lo stesso giorno (opzione (b) della voce di roadmap, ora
chiusa): `IsPresent=false` significa **soltanto** «la scansione l'ha cercato e non l'ha trovato sul
disco»; tutto ciò che sta fuori dal perimetro — fuori dai watched root attivi, sotto una cartella
esclusa per attributi o path — è `IsIncluded=false` con la presenza **intatta**. Vedi §6.
- **La scansione consegna al merge il perimetro che ha applicato** (`ScanPerimeter` in
  `Business/Filtering`: i root ordinati + i sottoalberi esclusi + i file saltati uno a uno).
  `IBulkIndexWriter.MarkAbsentFilesAsync` diventa `ReconcileUnseenFilesAsync` e prende le **aree
  saltate**; due UPDATE set-based, in **quest'ordine**: prima l'esclusione, così le righe che
  timbra `IsIncluded=0` **escono da sole** dal pass degli assenti — che resta lo statement e il
  predicato esatti di prima, senza sottoquery negate sul percorso caldo e senza due condizioni da
  tenere in accordo.
- **Un'area è un id di DIRECTORY** (o una coppia `(directory, nome)` per un file saltato da solo),
  **non un prefisso di path**. È la scelta tecnica centrale del giro: il catalogo contiene solo ciò
  che *era* dentro il perimetro quando è stato scritto, quindi «quali directory del catalogo sono
  fuori adesso» è normalmente l'insieme **vuoto** e al massimo il sottoalbero appena disattivato —
  mentre l'insieme dei **path esclusi** è grande per costruzione (su un volume di sistema ogni
  cartella sotto `Windows\` fallisce il filtro per conto suo). Lo staging riusa la forma del merge
  (tabella TEMP per connessione), e la mappa `IdByPath` che serve a tradurre l'ha già costruita il
  merge delle directory: zero query in più.
- **Il merge scrive `IsIncluded = 1`** sulle righe che ritrova. Una riga nel lotto è una riga che
  il filtro ha lasciato passare, quindi **la scansione È la decisione del filtro** (§4) — ed è
  l'unica cosa che può annullare un'esclusione che nessuno ha fatto da Setup: una cartella che
  smette di essere nascosta non alza alcun evento, e senza questo il suo contenuto resterebbe
  invisibile per sempre.
- **Nessuna colonna nuova sulle directory** *(decisione, come chiedeva il task)*: una cartella che
  esiste sul disco **esiste**, anche se non se ne indicizza il contenuto. Quindi niente
  `IsIncluded` su `Directories`, e `DirectoryMerger` semplicemente **non marca più assente** una
  cartella solo perché la scansione non ci è entrata. Il prezzo, accettato: dopo aver disattivato
  un root il Catalogo mostra lo **scheletro** delle sue cartelle (vuote), invece di farle sparire.
- **Il rientro nel perimetro non costa una ri-scansione** (§4): `WatchedRootsService.UpdateAsync`
  riconcilia anche sul cambio di `IsActive` — stesso `FilterReconciler`, un entry point in più,
  **non una seconda copia**. Root spento → tutto sotto va `IsIncluded=false` (ciò che la cancella-
  zione del root già faceva); riacceso → inclusione ricalcolata contro il filtro effettivo, con
  `NeedsScan` vero perché ciò che non è mai stato indicizzato non si può «riabilitare» da una riga
  che non esiste. Una richiesta che non cambia nulla non riscrive l'indice.
- **Niente migration che indovina.** I DB esistenti contengono righe `IsPresent=false` scritte dal
  comportamento vecchio e **non sono distinguibili** da file davvero spariti: la riga si ripara
  alla prima scansione che la guarda di nuovo, che è esattamente quando la verità diventa
  conoscibile. C'è un test che lo fissa.
- **UI**: lo switch di una cartella monitorata produce ora una riconciliazione vera, e lo store la
  buttava via — l'utente doveva fidarsi. Ora finisce sulla nota che il cambio filtro usa già, con
  il testo riscritto: dice **l'effetto** («Indice riallineato: N file inclusi · M esclusi»), che è
  vero per entrambe le cause, invece di nominare il filtro dei tipi che è solo una delle due.
- **Verifica**: xUnit **765 verdi** (+15, di cui 4 dal giro di review), Vitest **243 verdi** (+1),
  build backend pulita (warnings-as-errors), `ng build` ok (restano i 4 warning di budget SCSS,
  pre-esistenti).
  RED dimostrato rompendo il prodotto apposta: tolte le aree saltate → **4 rossi**; tolta la
  scrittura di `IsIncluded` dal merge → il ramo «torna dentro» diventa rosso da solo; tolta la
  riconciliazione dallo switch del root → **2 rossi**; tolti i due fix della review (il guard sul
  tipo del file saltato, i segmenti di path in `FilterWidened`) → **2 rossi**.
  **Misura, statement non millisecondi** (`CountingSqliteConnection`: i comandi raw del writer non
  passano dagli interceptor di EF): chiudere una scansione che non ha saltato nulla = **1
  statement**, esattamente come prima; **6** con un'area saltata, e **6 restano** che dietro
  quell'area ci siano 50 righe o 500.
  Harness sul ferro (`D:\Collaudo\A` → `C:\Collaudo\B`): **47 scenari, 47 PASS, 0 FAIL** (44 era la
  baseline; il nuovo `exclusion-vs-absence` si applica a tutte e tre le coppie), eseguito **due
  volte**, prima e dopo il giro di review. Costo di scansione invariato: 2 002 file, primo scan
  1,27 s / re-scan 0,68 s prima, 0,52–0,83 s / 0,55–0,61 s dopo. `appsettings.json` dell'harness
  rimesso byte-identico (sha256 `653f5990…` verificato).
La **code review finale** (indipendente, sulle modifiche di questo giro) ha trovato **due cose con
i denti**, entrambe corrette nel commit `ae8c679`, più le minori attorno.
La prima: la scansione registrava **ogni** file scavalcato per motivi di perimetro, e ognuno
diventava un INSERT in staging **dentro la transazione di chiusura** — quella che tiene l'unico
write lock di SQLite. Su un volume sorvegliato dalla radice sono i `desktop.ini` di ogni cartella
personalizzata, i `Thumbs.db`, il `pagefile.sys`: migliaia di stringhe tenute per tutta la
scansione e migliaia di round-trip **per non dire niente**, perché un file che l'allow-list rifiuta
non ha alcuna riga da correggere. Ora si registra un file saltato **solo se il filtro dei TIPI lo
avrebbe ammesso**, che è esattamente l'insieme che può avere una riga `IsIncluded = 1`.
La seconda: `FilterWidened` guardava solo le estensioni, quindi **togliere un segmento di path
escluso** — un allargamento di perimetro a tutti gli effetti: sotto non è mai stato indicizzato
niente — diceva all'utente che non serviva alcuna scansione.
Nel passaggio: le due metà del filtro si chiedono **una volta ciascuna** invece di richiedere il
perimetro sul ramo di scarto (`IsPathExcluded` fa uno `Split` per chiamata, e lo pagavamo due volte
per ogni file rifiutato di un volume); il log «marked excluded» non sostiene più che i file siano
ancora sul disco (la scansione non ha guardato, che è il motivo per cui la presenza è rimasta
ferma), e la stessa frase è corretta su `IBulkIndexWriter`, dove ora dice anche **cosa implica**
«lasciata com'era»; un file saltato la cui directory non si risolve non viene più scartato in
silenzio (§9); e il test del conteggio statement dichiara **ciò che non prova**.
**Limiti noti e accettati:**
- **`FilterReconciler` non conosce il perimetro, e ora quel flag è l'unico segno che l'esclusione
  esiste** *(sollevato dalla review, NON chiuso qui: chiuderlo richiede stato persistito nuovo —
  una colonna che dice **perché** una riga è esclusa — ed è un work package, vedi roadmap)*.
  Scenario: rendi nascosta `Photos\Private`, scansiona (righe `IsIncluded=0`, `IsPresent=1`, giusto),
  poi **cambia il filtro dei tipi o accendi/spegni un root qualsiasi**: la riconciliazione re-include
  in blocco tutto ciò che l'allow-list ammette sotto quel root, comprese quelle righe, e il
  contenuto della cartella nascosta ricompare nel Catalogo fino alla scansione successiva, che lo
  ri-esclude. Prima di 11g la stessa sequenza era innocua solo perché quelle righe portavano
  `IsPresent=0` (cioè la bugia che questo step ha tolto). Nessuna perdita di dati, finestra chiusa
  da qualunque scansione, `NeedsScan=true` sul percorso del root.
- **Dopo un riallargamento senza scansione, Catalogo e Ricerca non concordano**: la chiusura della
  scansione **pota** dall'FTS le righe che esclude, e `FilterReconciler` fa solo `ExecuteUpdate` —
  non ha mai risincronizzato l'indice di ricerca. Quindi le righe re-incluse si navigano nel
  Catalogo e **non si trovano in Ricerca** finché non passa una scansione. Pre-esistente per la
  metà «tipi», esteso ora alla metà «perimetro».
- **I due contatori della pagina Volumi descrivono perimetri diversi**: dopo aver spento un root il
  conteggio **file** va a zero (filtra `IsIncluded`), quello delle **cartelle** no (filtra solo
  `IsPresent`, e le directory non hanno un flag di inclusione — è la decisione presa qui).
- **Un file rifiutato SOLO dall'allow-list delle estensioni non viene registrato come saltato**: la
  sua riga è già `IsIncluded=0` nel catalogo (`FilterReconciler` la timbra nell'istante in cui il
  filtro cambia) e il pass degli assenti la ignora già. Registrarli vorrebbe dire portare ogni
  `.dll` di un volume sorvegliato dentro il merge per dire una cosa già detta. Se un giorno un
  restringimento di filtro sfuggisse alla riconciliazione, quelle righe tornerebbero a essere lette
  come assenti: è il limite, ed è pre-esistente.
- **Una riga già `IsPresent=false` dal comportamento vecchio e ancora fuori perimetro resta così.**
  Non l'abbiamo guardata, quindi non la correggiamo: converge quando rientra nel perimetro e una
  scansione la ritrova. Il contrario sarebbe dichiarare presente un file che potrebbe essere stato
  cancellato mesi fa.
- **La riconciliazione non legge il disco, quindi non conosce gli attributi.** Riaccendendo un root,
  `FilterReconciler` re-include tutto ciò che l'allow-list ammette — comprese le righe sotto una
  cartella nel frattempo diventata nascosta, che la scansione successiva ri-escluderà. È il prezzo
  di «senza ri-scansione»: l'alternativa sarebbe sondare il filesystem dentro una richiesta di
  Setup, cioè fare una scansione per non farla.
- **Spegnere l'ULTIMO root attivo** non passa dalla scansione (con zero root attivi `ScanService`
  esce subito, «nothing to scan»): l'esclusione la scrive la riconciliazione di Setup. Chi
  disattivasse un root scrivendo direttamente nel DB non vedrebbe muoversi niente finché un root
  non torna attivo.
- **`IndexUpdater` (fine di un rename/move) valuta `IsIncluded` con il filtro di default** quando
  il path di destinazione è fuori da ogni root attivo (regola pre-esistente di
  `RootFilterResolver`: «la risposta più larga»). Un file portato fuori perimetro da un job resta
  quindi incluso fino alla scansione successiva, che lo esclude. Si ripara da solo, ma le due
  strade non danno la stessa risposta nello stesso istante.
- **`SkippedAreas` interroga il perimetro su ogni directory del volume** presente a catalogo. È in
  memoria e senza query (la mappa è già in mano al merge), ma è O(directory) per scansione.
- **Flakiness pre-esistente incontrata** (una passata: `CatalogApiTests`, verde in isolamento e su
  una passata pulita, 761/761): è quella documentata da 11e/11f, non di questo giro.

### Fatto nello step 11f (2026-08-19, commit `20791fd`…`1fca8c3`)
**WP10 chiuso**, e con esso i **work package minori**: il prossimo è lo **step 12 (Playwright)**.
Doveva essere il giro meccanico; due unificazioni su dieci hanno scoperchiato un bug vero, che è
esattamente il motivo per cui il task chiedeva di partire dalle **due** copie e di decidere quale
sopravvive invece di prendere la prima.
- **K1 — una sola cascata per rename e move di cartella** (`CascadeDirMoveAsync`). Le due copie
  erano divergenti in **tre** punti: quella del rename scriveva il `Name` nuovo e non toccava mai
  `ParentId`, quella del move ri-genitorializzava e non scriveva mai il `Name`, e si comportavano
  in modo diverso quando la riga radice del sottoalbero mancava. **Il bug**: la copia del move
  scriveva `ParentId = null` per una destinazione **alla radice del volume**, ma `null` non è la
  radice in questo schema — la radice è una riga vera con `MaterializedPath` vuoto, quella a cui
  `DirectoryMerger` lega ogni cartella di primo livello e quella di cui il Catalogo elenca i figli.
  Una cartella spostata lì restava impeccabile nella tabella e **spariva dall'albero** fino alla
  scansione successiva. Ora la riga in cima prende sempre il nome nuovo e prende un parent nuovo
  **solo quando il path del parent è davvero cambiato**, risolto da `DirectoryResolver` (che alla
  radice risponde con la RIGA radice). La condizione non è un'ottimizzazione: risolvere sempre
  scriverebbe — e dove manca, creerebbe — una relazione che un rename non ha chiesto di spostare.
  Il ramo «riga in cima assente» tiene la versione del rename (prosegue): con un catalogo coerente
  le due sono indistinguibili, e con dei discendenti orfani riscrivere i loro path li lascia almeno
  d'accordo con il disco. Test RED→GREEN; il primo tentativo (risolvere sempre il parent) è stato
  bocciato da `DirectoryCollationTests`, che aveva ragione: è cambiato il codice, non il test.
- **K2 — una sola pulizia dei partial** (`PartialCleanup.RemoveAsync`, sei call site). Le copie
  divergevano su **chi persiste il puntatore azzerato**: il motore salvava da sé con
  `CancellationToken.None`, `QueueService` lasciava il save al chiamante. Vince il motore, perché
  il delete non è annullabile: una volta che i byte sono nel cestino, una riga che li nomina ancora
  è falsa — e con la copia di `QueueService` quella riga falsa sopravviveva alla richiesta che
  l'aveva prodotta (un retry che perde la corsa sullo stato cancella il partial, lancia, e
  `ChangeTracker.Clear()` si porta via il `TempPath` azzerato). Test RED→GREEN che inietta la
  cancellazione rivale nell'istante esatto.
- **K3 — `NormalizeReservationAsync` sul ledger.** Una sola guardia (`SpaceLedger.ReservationFor`,
  che coincide con i casi che `SpaceCheck.EvaluateHardAsync` lascia passare senza toccare il
  device: una definizione sola di «questo job ha bisogno di spazio») e tre entry point per una
  regola sola — scope proprio (retry), metà durevole nella transazione del chiamante (E8) e
  specchio in memoria dopo il commit. La differenza fra le copie era il **token**: il retry passava
  quello della richiesta. Ora i membri non ne prendono uno affatto, ed è la firma a dirlo: quando
  girano, lo stato `Pending` è già committato, quindi rinunciare a metà lascia un job eseguibile la
  cui domanda il ledger non conosce.
- **K4 — una sola stanza cross-volume** (`ApplyCrossVolumeDemandAsync`). Divergevano sul caso
  **zero byte**: il verdetto è identico (una domanda di zero non può essere infattibile — l'available
  è clampato a 0), ma MoveFile **sondava il device** e timbrava `EstimateIsLive` con la risposta.
  Su un drive dichiarato online che non risponde, i due producevano job visibilmente diversi: uno
  con la bandierina «stima non live» su un job che non ha alcun numero da qualificare, l'altro no.
  Vince MoveFolder, e la syscall sparisce con lui.
- **K6/K7 — `ScanPath` nel shared kernel** (`FileTracert.Contracts/Scanning`). Tre layer avevano
  bisogno delle stesse regole sui path relativi al volume; viveva in `Business`, quindi `Platform`
  non lo raggiungeva e si era riscritto normalize + join a mano, e `Host` attraversava un confine
  per un helper di stringhe. `WatchedRootPath.Conflicts` ora chiama `ScanPath.Overlaps`: le due
  copie erano **identiche**, ed è il punto — coincidevano per fortuna, e un fix a una avrebbe
  mancato l'altra (che è come è nato K5).
- **K11 — not-found tipizzato al posto dello string-sniffing.** `EntityNotFoundException` (in
  `Contracts/Errors`; deliberatamente **non** `KeyNotFoundException`, che il BCL lancia anche per
  una chiave di dizionario mancante, e la coda di lookup su dizionario ne fa parecchi) +
  `QueueExceptionFilter` sul controller al posto di sei try/catch. §9 voleva due cose e solo due
  action su sei le facevano: adesso tutte loggano per intero ciò che convertono. Mappatura
  preservata alla lettera, incluso il confine che sembra un'incoerenza e non lo è — ciò che manca
  nella **route** è 404, ciò che manca nel **body** è 400 — con un test che lo fissa.
- **K12 — il probe FTS dietro la sua interfaccia** (`IFileSearchIndex.IsEmptyAsync`). Il cast a
  `SqliteConnection` e la query a mano su una tabella virtuale stavano in `Host`.
- **K13 — solo ciò che è davvero morto.** `ScanPhase.Done`/`Failed` **non** lo sono più (10b) e
  restano; `ScanPhase.ResolvingPaths` non è prodotto da nessun `SetPhase` e se ne va. `IsStale` era
  un terzo campo per il bit già in `IsOnline`, e **non lo leggeva nessuno**: resta `DataIsLive`,
  che è nominato per ciò che descrive e che può onestamente smettere di rispecchiare `IsOnline`
  (`SpaceCheck` già distingue «il catalogo lo crede collegato» da «ha risposto alla sonda»).
  `completedCount` non aveva lettori.
- **K10 era già chiuso da 11d** (il chrome modale è `.ft-modal*` nel design system da quando i
  nuovi stati del picker hanno superato la soglia di *errore* del budget SCSS). Restava la barra
  azioni, ora `.ft-modal-footer`.
- **I tre lasciti di 11e**: il vero duplicato di `GroupBy(_ => 1)` era `VolumesController`, che
  ricontava file e byte con lo stesso filtro di `CatalogTotals` (le altre tre occorrenze aggregano
  tabelle diverse: astrarre *quello* sarebbe astrarre `GroupBy`); il DDL FTS5 era copiato in
  **sette** file di test e ora viene da `FileSearchIndexSchema`, che è ciò che esegue la migration;
  e una `DbUpdateException` non abortisce più l'intera passata di rivalutazione. Quest'ultimo ha
  richiesto più di un `continue`: il tentativo annullato lascia entità tracciate (le righe di
  ledger in staging, le directory dell'overlay) e il save del job successivo se le trascina dietro
  — un primo tentativo con `continue` trasformava il fallimento di un job in quello del job dopo.
  Quindi la passata segnala di essere stata interrotta, il chiamante scarta il change tracker e
  rilegge i candidati non ancora esaminati. Una query in più, solo quando qualcosa è fallito.
- **Discrepanze di layering, chiuse con codice e brief concordi**: `ScanPath` **spostato** in
  `Contracts`; `IBulkIndexWriter` **lasciato** in `Data/Indexing` e §3 corretto — le sue firme
  parlano in entità EF, portarlo in `Contracts` ci trascinerebbe il modello, cioè romperebbe
  «Contracts non dipende da nulla» per rispettarne la lettera. Ora §3 dice che *dove vive
  l'interfaccia lo decide la sua firma*.
- **Verifica**: xUnit **750 verdi** (+8), Vitest **242 verdi**, build backend pulita
  (warnings-as-errors), `ng build` ok (restano i 4 warning di budget SCSS, pre-esistenti).
  Harness sul ferro (`D:\Collaudo\A` → `C:\Collaudo\B`): **44 scenari, 44 PASS, 0 FAIL**, identico
  alla baseline di 11e. `appsettings.json` dell'harness rimesso byte-identico (sha256 verificato).
**Limiti noti e accettati:**
- **Il retry normalizza la riserva DOPO il suo commit**, il revaluator dentro la transazione.
  Allinearli è E8 applicato a un secondo call site, cioè una modifica di crash-safety al file più
  caldo della coda: non è roba da commit di dedup. La finestra è quella pre-esistente (un crash fra
  il commit e le chiamate al ledger lascia un `Pending` sotto-riservato).
- **`QueueExceptionFilter` cattura lo stesso insieme di prima**, `ArgumentException` e
  `InvalidOperationException`. Il che conserva un difetto noto: `ObjectDisposedException` deriva da
  `InvalidOperationException` e viene letta come 400. Lo era anche prima; allargare o restringere
  l'insieme è un cambio di comportamento e non appartiene a un giro di dedup. Il filtro sta **sul
  controller**, come chiedeva il task, quindi copre anche `List`, che prima non aveva try/catch:
  oggi è inerte (`ListAsync` non lancia nulla di quell'insieme), ma se un domani `List` acquisisce
  validazione, un guasto del server vi si presenterebbe come 400.
- ~~**La flakiness della suite sotto carico concorrente documentata da 11e è stata incontrata**
  (un test diverso a ogni giro — `DomainApiTests`, `SetupApiTests`, `RootsBySpecificityTests`,
  `Win32FileMoverTests` — sempre verde in isolamento e su una passata pulita, 750/750). Non è di
  questo giro: le passate incriminate non toccano i file modificati qui. Resta aperta.~~
  **Chiusa allo step 11i** (2026-08-19), con la causa misurata e non ipotizzata.
- ~~**La domanda di prodotto su `IsPresent=false` come «escluso dal filtro» è stata posta e NON
  decisa** (è §«Cosa resta all'umano»): vedi il punto 1 della roadmap. Nulla è stato mosso, e il
  test che fissa il comportamento attuale è ancora lì.~~ **Decisa dall'utente e implementata allo
  step 11g** (2026-08-19).

### Fatto nello step 11e (2026-08-19, commit `ec725c3`…`d7f7748`)
**WP9 chiuso** (E1, E3, E4, E5, E6, E7, E8; E2 era già chiuso allo step 9a). La radice è sempre la
stessa: **SQLite ha un solo scrittore**, quindi ogni statement di troppo su un percorso caldo non è
«un po' più lento», è tempo in cui non scrive nessun altro — scan e coda compresi.
**Ogni fix porta un numero misurato, e i millisecondi non sono mai il numero**: si contano
statement, transazioni, byte allocati, visite di riga. Un test che asserisce un conteggio fallisce
su qualunque macchina nell'istante in cui qualcuno rimette una query dentro un ciclo; un test che
asserisce un tempo racconta l'umore del portatile.
- **E3 — il COUNT della ricerca si ferma al cap.** `SELECT MIN(COUNT(*), 10000)` limitava il numero
  *stampato*, non il lavoro: visitava ogni match FTS e faceva due join per ciascuno prima di
  buttare via l'eccedenza. Ora `SELECT COUNT(*) FROM (SELECT 1 … LIMIT 10000)`, che dà la stessa
  risposta per ogni dimensione (`MIN(n, cap)` e «conta al più cap righe» coincidono sempre).
  Misurato **sullo statement di produzione**, non su una copia: `Files` viene ombreggiata da una
  view che porta una UDF contatore nella WHERE, così il numero è quante righe lo statement ha
  davvero percorso. Su 15 000 match il COUNT passa da **15 000 a 10 000** visite; il divario cresce
  con il match set (a 100 000 match: 10 000 invece di 100 000).
- **E7 — i watched root si ordinano una volta.** Quale root governa un item è una domanda che lo
  scan pone per **ogni voce enumerata**, milioni su un volume vero, e la risposta veniva calcolata
  ricostruendo ogni volta un `Where` + `OrderByDescending` + `FirstOrDefault`. L'ordinamento è una
  proprietà del **set di root**: non può cambiare da un item al successivo. Ora vive in un valore
  `RootsBySpecificity` — il tipo porta l'invariante, così un array non ordinato non si può passare
  per sbaglio (sarebbe una risposta sbagliata in silenzio, non un errore di compilazione) — e la
  domanda per item è una passeggiata first-match. Per strada `ScanPath.IsWithin` ha perso la sua
  allocazione: `path.StartsWith(root + '\\')` costruiva una stringa prefisso per root per item.
  Stessa regola, stessi tre casi, scritta su span. Misura con
  `GC.GetAllocatedBytesForCurrentThread` su 200 000 risoluzioni contro 4 root annidati:
  **83 200 000 byte → 0** (416 B per item; sul volume da 3 milioni di voci del finding, ~1,2 GB di
  allocazione che non avviene più). `IsWithin` **non aveva un test proprio** — era coperto solo
  attraverso i chiamanti, e uno di quelli è il guard di enqueue: ora ne ha uno che tiene la vecchia
  scrittura come **oracolo** e le confronta su radice del volume, uguaglianza, confine di segmento,
  path più corto del root, case folding nei due versi, non-ASCII e coppie surrogate — cioè dove
  l'assunzione «il case folding ordinale non cambia la lunghezza», su cui poggia la versione a span,
  poteva rompersi (`08d1e8c`).
- **E5 — i contatori del Catalogo non escono più dall'indice, e metà del finding non reggeva.**
  Prima di scrivere una riga è stata fatta la misura che il task chiedeva, e ha smentito la
  diagnosi: con le statistiche che l'applicazione **ha davvero** — non esegue mai `ANALYZE` — il
  pianificatore risponde al predicato proiettato in MULTI-INDEX OR, cioè **due seek**, non le
  scansioni di tabella che il finding assumeva. Il rewrite in count raggruppati misura 122–449 ms
  contro 176–239 ms della forma in essere, su 300 000 file in 499 sottocartelle: **dentro il
  rumore**, in cambio di tre round trip invece di uno. La forma resta. *(Nota per chi un giorno
  aggiungesse una manutenzione `ANALYZE`: con le statistiche popolate lo stesso piano collassa a
  scansioni e il listato passa a ~13 s. È un dirupo latente, non un problema di oggi.)*
  Quel che restava è reale e non è una questione di millisecondi: trovate le righe con il seek,
  SQLite risaliva alla **riga di tabella** di ogni file contato per leggere due booleani — un
  listato di 499 cartelle da ~600 file sono ~300 000 lookup per due numeri su un badge. I flag ora
  viaggiano **con la chiave**: gli indici FK di `Files` diventano
  `(DirectoryId, PendingDirectoryId, IsIncluded, IsPresent)` e
  `(PendingDirectoryId, IsIncluded, IsPresent)`. **Non è un indice in più**: guidano con la stessa
  foreign key, quindi la convenzione EF non crea più quelli stretti e il numero di B-tree per riga
  inserita non cambia — cosa che conta, perché uno scan inserisce milioni di righe. Verificato sul
  ferro: togliendo la migration i tempi di scan non si muovono (re-scan 1,25–1,58 s senza, 1,06–1,87 s
  con). Il ramo dominante ora legge `SEARCH … USING COVERING INDEX`.
- **E6 — un aggregato per tabella.** `GET /api/dashboard` faceva cinque domande al database per una
  striscia di card, e due percorrevano **due volte** la tabella più grande del database — quella su
  cui lo scan sta scrivendo. Ora tre, nella stessa forma `GroupBy(_ => 1)` che 11d aveva introdotto
  per `QueueTotals`, invece di un secondo idioma accanto al primo. Misura con un interceptor di
  comandi EF: **5 statement → 3**, di cui **2 passate su `Files` → 1**. Sparisce anche la stampella
  `totalFiles == 0 ? 0 : sum`, che esisteva perché `SUM` su zero righe è NULL e non entra in un
  `long`: senza righe non c'è gruppo, e `CatalogTotals.Empty` risponde per lui.
- **E1 — la lista della coda smette di materializzare ogni item.** Mostrava il path sorgente di un
  job caricandone l'**intera** collezione di item per prenderne il primo: per un `MoveFolder`
  cross-volume è un'entità per file, cioè 100 000 righe sull'heap per stampare un path — e di nuovo
  per ogni job della pagina. Ora due round trip aggregati per tutta la pagina (il minimo id per job,
  poi i path di quegli id), e «primo» è ancorato all'**id più basso** invece che all'ordine che il
  database restituisce a caso: una riga non deve iniziare a mostrare un file diverso per un motivo
  che non c'entra. Misura in byte allocati da un `ListAsync`: job da 20 item **56 240 B**, da 2 000
  item **2 479 712 B** (44×, ~1,2 KB per item) → **70 096 B** e **69 352 B**, piatto. Il job da
  100 000 item del finding passa da ~120 MB per caricamento di pagina agli stessi ~70 KB.
- **E4 — upsert FTS set-based per i rename di cartella.** `UpsertAsync` è un DELETE più un INSERT, e
  tre percorsi lo chiamavano **per file**: rename di cartella, move di cartella intra-volume, move
  cross-volume, più la riconciliazione del cancel. Un rename su 50 000 file erano 100 000 statement,
  e il ciclo che li produceva materializzava prima la `FileEntry` completa di ognuno per leggerne il
  nome. Ora il lotto è espresso **per directory**, con `IFileSearchIndex.SyncDirectoriesAsync` (§3:
  il SQL sta in `Data`) che fa `DELETE` + `INSERT … SELECT` per lotto di **cartelle**: nemmeno un id
  di file attraversa il confine. Misura su una tabella FTS5 vera con l'interceptor: 600 file
  **1 200 → 2 statement** — e 2 non è «600 arrotondato», è *una* cartella: il conto segue la forma
  dell'albero, mai il numero di file dentro. Allocazione, a caldo: **398 KB / 4 215 KB → 182 KB /
  182 KB** per 25 / 500 file, cioè **piatta**.
  La forma per-directory è la correzione della review (vedi sotto): il primo tentativo passava gli
  **id dei file**, e per poter *potare* le entry stantie doveva nominare anche le righe escluse e
  assenti — che sono esattamente quelle che un filtro ristretto accumula (dallo step 11a anche tutto
  ciò che sta sotto una cartella esclusa). Una cartella con 900 file esclusi e 100 indicizzati
  costava gli statement di mille: l'ottimizzazione di punta sarebbe regredita proprio sui cataloghi
  che ne hanno più bisogno. C'è un test su quel caso.
  **Due comportamenti convergono sulla regola invece di parafrasarla**, ed è voluto: il nome
  indicizzato è ora quello **proiettato** che la costante condivisa di §5/9b definisce — questo era
  l'unico dei quattro percorsi di popolamento a scrivere il nome fisico a mano — e un file escluso o
  assente ora **perde** la sua entry invece di essere saltato, quindi un rename di cartella non lo
  lascia più puntato al path vecchio. Il percorso del cancel ha anche spostato la sync **dopo** il
  `SaveChanges`: ricostruiva le entry da righe il cui nuovo `DirectoryId` non era ancora scritto, ed
  era corretto solo perché il path veniva passato a mano.
- **E8 — una sola transazione per sbloccare un job.** Erano tre scritture separate: stato e overlay
  sulla connessione del servizio, poi `ISpaceLedger.ReleaseAsync` che apre uno scope e una
  connessione propri, poi `ReserveAsync` che ne apre altri. Su SQLite sono tre turni all'unico write
  lock del processo, e una rivalutazione sblocca job in ciclo. Ora una: le metà **durevoli** del
  ledger passano dal contesto del chiamante (`SpaceLedger.DeactivateEntriesAsync` e
  `BuildReservationEntries` sono statiche esattamente per questo, e il completamento le usava già
  così); solo lo specchio in memoria segue il commit. **Non** reintroduce il deadlock che il retry
  evita committando prima: ciò che sarebbe un SQLITE_BUSY auto-inflitto è chiamare i metodi del
  ledger che aprono **scope propri** mentre si tiene il write lock, e quelli qui non si chiamano
  più. E **rafforza** la crash-safety invece di barattarla: la regola di WP1 (finding #5 — il
  movimento durevole del ledger nella stessa transazione del cambio di stato) sul rilascio **non
  valeva**, perché seguiva il commit; un crash lì in mezzo lasciava un job `Pending` con la riserva
  rilasciata e non ripresa, cioè sotto-riservato rispetto a tutta la coda. C'è un test che fa
  fallire la scrittura del ledger e trova il job ancora `Blocked` con la vecchia riserva in piedi.
  Misura: **2 transazioni esplicite → 1** (il terzo write è un `ExecuteUpdate` singolo, per cui EF
  non apre una transazione propria).
- **Verifica**: xUnit **735 verdi** (+72), build backend pulita (warnings-as-errors). Frontend non
  toccato, quindi nessun `ng build`. **RED dimostrato rimettendo il prodotto com'era**, un fix alla
  volta: 30 000 visite invece di 25 000 (E3), 83 MB allocati invece di 0 (E7), `SEARCH … USING
  INDEX` senza COVERING (E5), 5 statement invece di 3 (E6), 2,4 MB per pagina invece di 70 KB (E1),
  1 200 statement invece di 4, un file con rinomina in coda indicizzato sotto il nome fisico e un
  file escluso rimasto puntato al path vecchio (E4), 2 transazioni e un job `Pending` dopo una
  scrittura fallita del ledger (E8).
- **Harness sul ferro** (`D:\Collaudo\A` ↔ `C:\Collaudo\B` — il drive `E:` non esiste su questa
  macchina): **44 scenari, 44 PASS / 0 FAIL**, eseguito due volte (prima e dopo le correzioni della
  review). Passa anche `job-dependencies` sulla coppia *cross*, che allo step 10b era l'unico FAIL
  (pre-esistente).
  Tempi di scan su `D:\Collaudo\A`, 2 002 file, tre run per configurazione: primo scan
  **2,7–5,6 s**, re-scan **1,06–1,87 s**. Sono **più lenti** dell'ultima misura registrata (step 10a:
  1,08 s / 0,59 s) e **non per colpa di questo giro**: il log dell'harness dice che il giornale USN
  non è disponibile (processo non elevato), quindi ogni scan qui percorre il motore a
  **enumerazione**. Le due verifiche che contano sono state fatte a parte, revertendo un commit alla
  volta nella working directory: **E5 non muove i tempi** (senza la migration: re-scan 1,25–1,58 s;
  con: 1,06–1,87 s) ed **E7 nemmeno**, il che è **atteso** e va detto invece che nascosto —
  l'harness configura **un solo** watched root e 2 002 file sono tre ordini di grandezza sotto il
  volume su cui il difetto morde. La prova di E7 è il contatore di allocazione, non il cronometro.
La **code review finale** (indipendente, sulle modifiche di questo giro) non ha trovato nulla di
bloccante — ha verificato una per una le sette equivalenze, incluse le due delicate: il case folding
`OrdinalIgnoreCase` preserva la lunghezza (quindi l'`IsWithin` a span è esatto anche su coppie
surrogate, perché il ramo che potrebbe spezzarne una richiede `path[root.Length] == '\\'` e cade
comunque a `false`), e `OrderByDescending` è stabile (quindi il first-match sceglie lo stesso
elemento a parità di lunghezza). Ha trovato **due cose sopra la soglia**, entrambe corrette in
`d7f7748`: (1) le due chiamate allo specchio in memoria del ledger **dopo il commit** passavano
ancora `ct` — le righe sono già committate, quindi onorare il token lì separa il fatto durevole
dalla sua copia in memoria, che è ciò su cui ogni decisione di fattibilità viene calcolata, e non è
un rischio solo di shutdown perché `CancelAsync` esegue una rivalutazione sul token della richiesta;
ora `CancellationToken.None`, com'era già in `JobExecutionEngine`; (2) il resync FTS del rename di
cartella, per poter potare le entry stantie, nominava **anche** le righe escluse e assenti (vedi E4
sopra) — risolto passando alla forma per-directory, che è anche più economica. Tre rilievi minori
presi: l'asserzione di costo di E3 non fissa più un numero che dipende dal pianificatore, la
riconciliazione del cancel gira ora sul `FileSearchIndex` **vero** (era l'unico dei tre punti di E4
senza asserzioni sulle righe risultanti, ed è quello in cui la sync si è spostata da prima a dopo il
`SaveChanges`), e il piano del covering index è asserito su 20 000 righe invece di 4. I rilievi
lasciati consapevolmente sono in fondo.
**Limiti noti e accettati:**
- ~~**La suite è instabile sotto carico**, e questo giro allarga la finestra invece di crearla: 735
  verdi su una macchina tranquilla, ma in un run pieno **sotto build concorrente** 1–2 test di
  integrazione *Host* possono fallire con corse sulla **vita delle connessioni SQLite**
  (`ObjectDisposedException` su `sqlite3_create_collation`, oppure una scrittura all'Event Log
  Windows senza elevazione). **Il test che fallisce cambia a ogni run** — `AuthEndpointTests`,
  `DomainApiTests`, `DeviceWatcherWorkerTests`, `DatabaseInitializerTests` — e **tutti passano in
  isolamento** (verificato 4 run su 4 per ciascuno, sia con sia senza le correzioni della review).
  La causa è pre-esistente (TestServer paralleli che condividono connessioni in pool); i 72 test
  aggiunti qui, alcuni dei quali seminano 12–25 000 righe, aggiungono il carico che la rende
  visibile. Da chiudere tenendo esplicitamente aperta la `SqliteConnection` nei test Host, non
  ignorando un rosso.~~ **Chiusa allo step 11i** (2026-08-19): l'ipotesi «connessioni condivise»
  era vicina ma non esatta — le connessioni non erano condivise, il **pool** sì, ed era
  `SqliteConnection.ClearAllPools()` nei teardown a disporre l'handle nativo di chiunque altro.
  Vedi il paragrafo «Fatto nello step 11i». *(Della scrittura all'Event Log non si è più vista
  traccia: `AddWindowsService` registra quel provider solo quando il processo è davvero un
  servizio, e sotto `dotnet test` non lo è.)*
- **Il paging delle sottocartelle del Catalogo resta aperto** (§7 dice paging *ovunque*). Con
  l'indice covering il listato non paginato è una scansione di range sola, ma resta senza limite
  superiore: una cartella con 100 000 sottocartelle le restituisce tutte, e i due contatori
  correlati girano una volta per ciascuna. Chiuderlo cambia `CatalogChildrenDto` e la schermata
  (serve un «carica altro» per le cartelle): è una **decisione di prodotto**, non un'ottimizzazione,
  e questo task non tocca il frontend.
- **Il dirupo `ANALYZE`.** Tutte le misure di E5 valgono per un database senza statistiche, che è
  quello che l'app produce oggi. Se un giorno si aggiunge una manutenzione che esegue `ANALYZE`, il
  MULTI-INDEX OR del Catalogo va rivalutato: con `PendingDirectoryId` NULL su quasi tutte le righe
  le statistiche fanno sembrare quell'indice inservibile e il pianificatore torna alla scansione.
- **E3 non ha una prova di costo *automatica* sullo statement di produzione senza puntelli.** Il
  numero è misurato sullo statement vero, ma per farlo il test sostituisce `Files` con una view:
  SQLite non espone al client alcun contatore di lavoro per statement, e i millisecondi non sono un
  contatore. Il puntello vive nel test, mai nel prodotto.
- **Il costo in scrittura degli indici E5 è misurato su 2 002 file**, dove non si vede. Su un volume
  da milioni di righe le chiavi più larghe si pagano; restano comunque lo stesso *numero* di
  B-tree per riga, che è il termine che domina.
- **Il contatore `ChildCount` del Catalogo non è stato coperto** come quello dei file: gira sugli
  stessi predicati proiettati ma su `Directories`, i cui indici portano solo la chiave, quindi
  risale ancora alla riga per leggere `IsMaterialized`/`IsPresent`/`PendingState`. È la stessa
  argomentazione di E5 su un volume di righe molto più piccolo, e allargare quell'indice costerebbe
  anche `PendingState` (una stringa) nella chiave: lasciato **come decisione**, non come svista, in
  attesa di un numero che la giustifichi.
- **Una `DbUpdateException` sulla scrittura del ledger interrompe l'intera passata** di
  rivalutazione invece di saltare il solo job (le altre save passano da
  `SaveOrFollowConcurrentStateAsync`). Non è una regressione — anche prima l'eccezione usciva da
  `UnblockAsync` — e la transazione fa rollback, quindi nulla resta a metà; è resilienza (§9),
  candidata al giro di cleanup.
- **Duplicazione lasciata a 11f**: l'idioma `GroupBy(_ => 1)` è ora scritto tre volte
  (`CatalogTotals`, `VolumeTotals`, `QueueTotals`), e l'helper `FtsRows()` più la DDL della tabella
  FTS5 sono copiati in tre file di test. Il secondo è il più fastidioso: è la definizione del
  tokenizer, che può divergere in silenzio da quella della migration.

### Fatto nello step 11d (2026-08-19, commit `63a846d`…`af4196f`)
**WP7 chiuso** (C17, C25, C27, C29, C30, K8, K9, K14): i difetti che l'utente incontra
*mentre lavora* — un errore che non dice cosa è successo, un accodamento che si rompe a metà,
un dialog che si apre vuoto, una dashboard che dice zero mentre la Coda mostra job veri.
- **C25 — un gesto, una richiesta, una transazione.** Nuovo `POST /api/operations/enqueue-batch`
  e `IQueueService.EnqueueBatchAsync`. **Decisione: tutto o niente.** Il picker faceva un POST per
  file: un fallimento all'elemento N lasciava 1..N−1 in coda senza che nulla a schermo lo dicesse,
  e la reazione ovvia — ripremere «Accoda» — li riaccodava come dipendenti di sé stessi. O l'intera
  selezione entra in coda o non entra niente: allora l'errore è leggibile, il retry è **lo stesso
  gesto**, e non resta uno stato intermedio da spiegare. Il prezzo — un elemento sbagliato ferma
  gli altri quarantanove — è pagato consapevolmente: il parziale è onesto **solo** se la risposta
  enumera quali sono passati, e una coda che l'utente deve riconciliare riga per riga è proprio ciò
  che stiamo togliendo. In caso di fallimento il 400 dice **quale** elemento («Elemento 2 di 50»)
  e che **nulla** è stato accodato. Tutto ciò che l'enqueue singolo fa per job continua a farlo per
  ogni job: il guard è interrogato per ogni elemento (e gli elementi dello stesso batch **si vedono
  a vicenda**, perché sono inseriti sulla stessa connessione prima della domanda), `SequenceOrder`
  resta assegnato dentro la transazione contro l'indice unico (C26/9c), overlay e righe di ledger
  sono nella stessa unità di lavoro. `EnqueueAsync` è ora un batch da uno: un solo percorso di
  accodamento. In più il verdetto di spazio porta avanti **il peso del batch stesso**: il mirror
  in memoria del ledger conosce questi job solo dopo il commit, quindi senza accumulo cinquanta
  move da 1 GB su un drive da 10 GB sarebbero pesati tutti contro lo stesso spazio libero intatto
  e nascerebbero tutti `Pending`.
- **C17 — l'errore arriva intero a chi lo sa leggere.** L'interceptor rilanciava
  `new Error(message)`: status e body sparivano un livello sopra il codice che ne aveva bisogno,
  quindi ogni `instanceof HttpErrorResponse` a valle era falso e la gestione strutturata dei 400
  era codice morto **che non sembrava morto**. Leggeva anche `err.error.message` mentre il backend
  risponde `{ error: … }`, così a video finiva il raw «Http failure response … 400». Ora rilancia
  l'errore originale e **una sola** funzione (`core/http/http-error.ts`) lo traduce: prima la nostra
  forma `{ error }`, poi ProblemDetails, poi un'etichetta per status, e **mai** la riga di trasporto
  (l'URL resta nel log, dov'è utile). La usano l'interceptor per il toast e tutti gli store per il
  proprio signal `error`, quindi toast e schermata non possono divergere.
  `shared/api/operation-error.ts` era la stessa funzione con un secondo nome ed è stata eliminata.
- **C27 — il dialog si apre quando ha i dati.** Il picker lanciava `loadList()` e leggeva
  `catalogable()` alla riga dopo. Ora **aspetta**, e dice in quale dei tre casi si trova: in lettura
  (skeleton della forma del select, così nulla salta quando arriva il vero), fallita (messaggio +
  «Riprova»), oppure nessun volume catalogabile (avviso neutro che rimanda a Volumi — non è un
  errore, è un vicolo cieco con una via d'uscita). Store già caldo = si sceglie subito e si aggiorna
  sotto: un caricamento mostrato sopra dati che abbiamo sarebbe un flicker che non significa niente.
  La lista cartelle non dice più «Cartella vuota» quando la verità è che manca il volume.
- **C29 — il timer muore con la vista.** Il debounce da 300 ms della Ricerca log sopravviveva alla
  navigazione: riscriveva i filtri di uno store applicativo e spendeva una richiesta per una vista
  morta. `DestroyRef.onDestroy` lo azzera. Gli altri timer del client (`RealtimeService`,
  `RealtimeBridge`, `ToastService`, `QueueStore`) sono di singleton applicativi: non hanno questa
  forma, e sono stati controllati uno per uno.
- **C30 — la Dashboard conta i job veri.** `QueueTotals.ComputeAsync` produce i quattro numeri in
  **un solo** aggregato (quattro conteggi sarebbero quattro scansioni della stessa tabella per una
  striscia di card letta a ogni caricamento). Le definizioni sono scritte accanto, perché ogni card
  afferma qualcosa di preciso: *in coda* = ogni job non terminale (le altre due sono il suo
  dettaglio); *in corso* = solo gli stati che muovono byte davvero; *bloccate* = per qualunque
  motivo; `PendingBytes` = byte ancora da scrivere dei job fermi su una **risorsa** (spazio o volume
  scollegato), che è esattamente ciò che dice l'etichetta sopra — un job in attesa di un altro job è
  bloccato ma non sta aspettando spazio su un disco.
  **Decisione realtime: sì, con rilettura coalescata.** `JobStateChanged` porta un id e uno stato,
  mai i byte: non c'è niente da patchare in memoria, quindi la scelta era tra rileggere e mostrare
  una card sbagliata in silenzio. Rileggiamo, al massimo una volta al secondo qualunque sia la
  raffica, e **non** si rilegge finché lo store non ha statistiche (la prima lettura non è ancora
  avvenuta o è fallita: martellare un servizio giù una volta per transizione non aiuta nessuno).
  Chiude il limite noto dello step 10c.
- **K8, K9, K14 — la stessa verità in un posto solo.** Gli stati job diventano un unico
  `Record<JobState, JobStateKind>` accanto al tipo, **esaustivo per costruzione**: un nuovo stato
  nell'union non compila finché non viene classificato, che è la proprietà che i due `Set` scritti a
  mano non avevano (uno era `Set<string>`, dove un refuso era invisibile). `Blocked` è un genere a
  sé e non una sfumatura di «in coda». Le categorie file diventano una mappa sola, con etichette
  **singolari** ovunque: la stessa stringa nomina la categoria su un chip di filtro e marca **un**
  file in entrambe le liste, dove il plurale sarebbe semplicemente sbagliato; `Other` guadagna un
  tag proprio (`ALT`) invece di prendere in prestito `???`, che torna a essere ciò per cui esiste —
  il marchio di un valore che questa build non conosce — e diventa finalmente filtrabile in Ricerca.
  Il campo «nuova cartella» inline usa `validateLeafName` come l'altro dialog, con lo stesso
  messaggio: `foo\bar` non è più accettato in un posto e rifiutato nell'altro.
- **Fuori elenco, dal brief del task: il margine ha finalmente un consumatore.** Lo step 11b ha
  separato `RequiredBytes` da `MarginBytes` sul server, ma nessuna schermata leggeva il secondo — e
  il deficit che entrambe mostrano **contiene** il margine. Spostare 40 GB su un drive con 40,8 GB
  liberi diceva «mancano 1,2 GB»: un numero che l'utente non trova da nessuna parte nelle dimensioni
  dei propri file, e che si legge come un difetto dell'app invece che come una politica dell'app.
  Ora il picker dice «mancano X · richiesto Y + Z di margine di sicurezza · disponibile W», e nomina
  il margine accanto al richiesto anche quando il batch ci sta; la riga della Coda aggiunge
  «· margine Z» al deficit. Nello stesso giro la bandierina «il dato non è live» smette di essere un
  `~` spiegato solo da un `title` (niente su touch, niente per uno screen reader) e diventa le parole
  «ultimo dato noto» nello stile ambra che il design system usa già per questo.
- **Design system**: la shell dei modali (backdrop, pannello, header, body) era identica
  byte-per-byte nei due dialog sotto due insiemi di nomi ed è diventata `.ft-modal*`; lo shimmer
  dello skeleton è `.ft-skel` invece di una seconda copia. Stessa logica di `_data-views.scss` (C8).
- **Verifica**: xUnit **663 verdi** (+18), Vitest **242 verdi** (+35), build backend pulita
  (warnings-as-errors), `ng build` ok con i **4 soli warning di budget SCSS pre-esistenti** (le
  aggiunte del picker lo avevano spinto oltre la soglia di **errore**: rientrato togliendo CSS morto
  e hoistando la shell condivisa, non alzando il budget). RED dimostrato rompendo il prodotto
  apposta, non solo per costruzione: threading della domanda di batch tolto → il secondo move nasce
  `Pending` invece che `Blocked`; picker rimesso al loop → **5** rossi; interceptor riportato a
  `new Error` → **3** rossi; `await` sul caricamento volumi tolto → **2** rossi; `DestroyRef` tolto →
  **1** rosso; contatori Dashboard e patch realtime tolti → **2** rossi; `validateLeafName` sostituito
  dal controllo di non-vuoto → **3** rossi. Harness sul ferro (`D:\Collaudo\A` ↔ `C:\Collaudo\B`,
  coppie intra ×2 + cross): **44 scenari, 44 PASS, 0 FAIL** — incluso `job-dependencies` cross, che
  allo step 10b era l'unico FAIL ed è stato chiuso allo step 11a. Rieseguito 44/44 anche dopo le
  correzioni della review. *(Nota: `DatabaseInitializerTests` WAL è caduto una volta con due
  `ng build` in parallelo sulla stessa macchina, poi verde in isolamento e verde sulla suite
  completa senza carico concorrente: è la flakiness già documentata, non una regressione.)*
La **code review finale** (indipendente, sulle sole modifiche di questo giro) non ha trovato
BLOCKER e ha trovato **due MAJOR reali**, entrambi corretti in `af4196f`. Il primo è il più serio
del giro: tutto ciò che segue il **commit** del batch prendeva ancora il token della richiesta, e
`RegisterReservationInMemoryAsync` comincia aspettando un semaforo — che su un token già annullato
lancia subito. Un abort in quella finestra (l'utente chiude il dialog; la transazione di un batch
può essere lunga) lasciava N prenotazioni **nel database e nessuna in memoria**: da lì ogni
fattibilità **sotto-conta** la domanda su quel volume, cioè la direzione che *sovra-impegna* un
disco, e il mirror si ricostruisce dal DB solo all'avvio. Dopo il commit non c'è più nulla da
annullare, quindi il post-commit gira con `CancellationToken.None`. Il secondo: il batch era
**illimitato** e il guard è quadratico dentro un'unica transazione di scrittura esclusiva — la
selezione del Catalogo si accumula tra le pagine, quindi un «seleziona tutto» su una cartella
grande era una strada reale per tenere il write lock oltre il busy timeout e far fallire i
checkpoint del processor **su un job che stava copiando**. Ora c'è un tetto di **500** con un 400
che lo nomina. Corretti anche tre rilievi minori: il wrapper «elemento N, e nulla è stato
accodato» copre l'intero elemento e non solo la sua costruzione; il change tracker viene azzerato
sul percorso di fallimento; e la schermata di conferma conta anche le operazioni parcheggiate per
**spazio o volume** — che pesare il batch come una domanda sola rende raggiungibili — invece di
annunciare un successo liscio. Verificati e dichiarati puliti dalla review: rollback su ogni
percorso di fallimento, eventi realtime e `_signal` solo **dopo** il commit, guard interrogato per
ogni elemento con gli elementi dello stesso batch che si vedono, accumulo della domanda coerente
con la semantica del ledger e `EnqueueAsync` invariato, retry di `SequenceOrder` ancora valido in
batch, `QueueTotals` davvero tradotto in SQL, nessun chiamante rimasto ad assumere il vecchio
contratto dell'interceptor, e nessuna perdita visiva nella de-duplicazione SCSS.
**Limiti noti e accettati:**
- **Nessun fallimento parziale, per costruzione.** Chi un giorno preferisse il parziale dovrà
  cambiare *anche* la forma della risposta (quali elementi sono passati), non solo il servizio.
- **Il tetto di 500 è una scelta di prodotto travestita da numero.** Una selezione più grande viene
  rifiutata con un messaggio che dice di dividerla; l'alternativa era una transazione di minuti che
  può far fallire i checkpoint di un job in copia. Da rivalutare se qualcuno accoda davvero
  decine di migliaia di file in un gesto.
- **Un batch resta 2N messaggi hub** (uno `JobStateChanged` e uno `ProjectionChanged` per job)
  mentre `_signal` è uno solo. Il client li coalesce dallo step 10c, quindi è costo, non
  correttezza; accorparli è materia di 11e.
- **Dashboard e Coda contano popolazioni diverse**: la Dashboard aggrega **tutta** la tabella, i
  chip della Coda contano la **pagina** corrente (50 righe). Sopra i 50 job i due numeri
  divergono. I chip pagina-locali sono pre-esistenti; è la Dashboard che diventa vera a renderlo
  visibile. Da chiudere con i contatori lato server (11e/E6 tocca lo stesso controller).
- **`SourceVolumeLabel`/`TargetVolumeLabel` sono null nella risposta di enqueue**, batch incluso
  (C32, pre-esistente): il picker non li legge.
- **La rilettura della Dashboard è una richiesta, non una patch**: su una coda molto attiva sono al
  massimo 60 aggregati al minuto finché la schermata resta aperta. L'efficienza dell'endpoint è
  materia di 11e.
- **`volumesError` del picker legge il signal `error` di uno store condiviso**: in linea di
  principio un fallimento sollevato in quell'istante dalla schermata Volumi verrebbe letto come
  proprio. È il prezzo di non dare al dialog una seconda copia della lista, ed è scritto accanto
  al codice.
- **Nessuna prova in browser**: la copertura è Vitest (render inclusi) più l'harness sul ferro per
  l'accodamento reale. La prova end-to-end vera resta lo **step 12** (Playwright).
- **Deviazione dallo split dei commit del task**: la shell condivisa dei modali e lo skeleton
  condiviso non erano nel piano. Sono entrati perché gli stati nuovi del picker avevano spinto il
  suo SCSS oltre la soglia di **errore** del budget, e la scelta era tra togliere duplicazione
  reale o alzare il budget. Segnalato qui invece di essere nascosto in un commit di refactor.

### Fatto nello step 11c (2026-08-19, commit `7a14786`…`5f3a0fb`)
**WP8 chiuso** (C18, C23, C24, C28): i quattro difetti che si notano solo quando serve la
diagnostica — cioè quando qualcosa è già andato storto.
- **C18 — il token arriva fino all'enumerazione.** `ScanService` aveva il token vero e passava
  `CancellationToken.None` a `ReadFullSnapshot` e a `Enumerate`: la fase più lunga di una
  scansione (dump MFT / camminata delle directory, minuti su un volume grosso) era
  **incancellabile**, quindi uno stop del servizio a metà scansione sfondava lo
  `ShutdownTimeout` e finiva in kill sporco — l'opposto del §3. Ora il token scende alle port
  **e** il loop che le consuma lo controlla a ogni item: entrambe le implementazioni `Platform`
  lo onorano già, ma quel loop è l'unico pezzo che gira fra due `await`, e una port che
  ignorasse il token non deve poter tenere in ostaggio l'intero servizio. Nessun commit avviene
  durante l'enumerazione, quindi una scansione annullata non lascia checkpoint (§9a) e la
  successiva riconverge. I due `CancellationToken.None` sui `CommitAsync` restano dove sono: un
  commit non si annulla a metà, e il commento accanto lo dice già. L'esclusione ereditata di 11a
  non è toccata — il controllo sta **prima** della risoluzione del root e di `ExcludedSubtrees`.
- **C23 — la coda dei log viene drenata allo stop.** `SqliteLogProcessor` era registrato come
  **istanza pre-costruita** e il container non dispone ciò che non ha creato lui, mentre
  `SqliteLoggerProvider.Dispose` era un no-op che si appoggiava alla premessa opposta: nessuno
  chiudeva la coda, e fino a ~10 000 record morivano con il processo a ogni stop — proprio i
  record dello shutdown, gli unici che servono a capire perché si è fermato. Ora c'è
  **`LogFlushService`**, hosted service registrato **per primo** e quindi fermato **per ultimo**
  (i servizi si fermano in ordine inverso di registrazione), così la coda si chiude quando ogni
  worker ha già scritto il proprio saluto. Le alternative sono state scartate con motivo: il
  container non può costruire il processor (il sink deve esistere prima che qualunque cosa
  logghi, cioè molto prima del container), e un `finally` attorno a `app.Run()` non verrebbe mai
  eseguito sotto `WebApplicationFactory`, cioè non sarebbe testabile. L'attesa è **capata** —
  `FileTracert:LogDrainTimeoutSeconds`, default 5 s contro i 30 s di `ShutdownTimeout` — e il
  token di stop dell'host la capa una seconda volta: un drain senza cap trasforma un servizio
  che si ferma in un servizio che non si ferma. Quando vince il cap, i record rimasti (in coda
  **più** il lotto in scrittura) vengono contati e dichiarati, non dimenticati in silenzio.
  L'ordine di stop non è dato per buono: c'è un test in cui un worker registrato **dopo** il
  flush logga dal proprio `StopAsync` e quel record deve trovarsi nel DB log.
- **C24 — il sink non tace più.** I tre `catch` nudi violavano il §9 nell'unico componente il cui
  mestiere è rendere leggibili i guasti. Il sink non può loggare su sé stesso, quindi la traccia
  esce **fuori** dal sink: ogni perdita incrementa `DroppedRecordCount`/`FailedRecordCount` e
  lascia un breadcrumb throttlato su **stderr** (visibile in console, innocuo come servizio) e su
  **`Trace`** (debugger / DebugView in entrambi), con l'eccezione **completa** via `ToString()`.
  Due dettagli non ovvi: un canale `DropWrite` risponde «scritto» anche quando butta via il
  record, quindi il contatore dei drop si aggancia alla callback `itemDropped` di
  `Channel.CreateBounded` (con `TryWrite == false` che ora significa solo «coda chiusa», perdita
  della stessa specie); e un consumer che muore **chiude** la coda, così i record successivi si
  contano come drop invece di accumularsi in una coda che nessuno legge.
  `OperationCanceledException` resta a basso rumore (solo `Trace`): è lo shutdown, non un
  difetto — la stessa regola applicata in 10b a `RealtimeEvents`.
- **C28 — la ricerca nei log è letterale.** Il filtro `Category` era escapato, la search una riga
  sotto no: cercare `100%` trovava ogni riga con «100», `file_name` trovava «fileXname». Ora
  `EscapeLike` + `ESCAPE '\'` su `Message` e `Exception`; anche l'escape dell'escape è coperto,
  quindi un path come `C:\Temp` continua a trovare sé stesso.
- **Verifica**: xUnit **645 verdi** (+15), build backend pulita (warnings-as-errors, anche in
  Release). RED dimostrato rompendo il prodotto apposta e riportandolo a posto: token buttato
  → 3 rossi (la scansione con enumeratore lento esce in 2 m 38 s invece dei 5 s di budget);
  contatori del sink disattivati → 2 rossi; registrazione del flush tolta → 2 rossi; escape
  della search tolto → 2 rossi; clamp del budget tolto → 2 rossi; riepilogo su DB log tolto
  → 1 rosso. Harness sul ferro (`D:\Collaudo\A` → `C:\Collaudo\B`, la coppia
  interna disponibile su questa macchina: **il drive E: non esiste più**): **44 scenari, 44 PASS,
  0 FAIL, 0 SKIP** — identico alla baseline di 11b, `offline-unplug` escluso come sempre
  (`SemiAutomatic=false`).
- **Deviazione dallo split dei commit del task**: C24 è stato committato **prima** di C23. Il
  breadcrumb del cap di drain usa l'helper introdotto da C24; l'ordine inverso avrebbe prodotto
  un commit che scrive una riga grezza per poi riscriverla subito dopo.
La **code review finale** (due passaggi indipendenti sulle modifiche di questo giro) ha trovato
**sei** rilievi reali, tutti corretti — `5f7c4cd` il primo, `5f3a0fb` gli altri:
1. **Doppio conteggio al cap.** Allo scadere del drain i record rimasti venivano sommati a
   `FailedRecordCount`, ma il consumer da cui si esce **continua a girare**: potevano essere
   scritti davvero (perdita dichiarata e mai avvenuta) o fallire nel `catch` del consumer, che
   li avrebbe contati una **seconda** volta. Ora hanno un contatore proprio,
   `AbandonedRecordCount`, con il significato onesto: «abbiamo smesso di aspettarli, se siano
   atterrati non c'era più nessuno a vederlo».
2. **`LogDrainTimeoutSeconds` era l'unica opzione non clampata** dell'host: un valore negativo
   arrivava a `Task.Delay` e usciva come eccezione da `StopAsync` — uno shutdown fallito causato
   proprio dal codice che esiste per renderlo pulito — e `0` rinunciava all'istante a ogni stop
   dichiarando perso tutto. Fuori range → default, controllato nel **costruttore**, così nessun
   chiamante può sbagliarlo.
3. **Un host che fallisce all'AVVIO non ha una stop sequence**, quindi `LogFlushService` non
   drenava: una migration che lancia è esattamente il momento in cui quei record servono. Ora
   `Program` drena in un `finally` attorno a inizializzazione e `Run`, sull'istanza che possiede
   il composition root (dopo `RunAsync` il container è già disposto e non potrebbe restituirla —
   per questo `AddSqliteLogging` torna a restituire il processor). Verificato che **non** scatta
   sotto `WebApplicationFactory`: la coda del test host è ancora aperta e accetta record due
   secondi dopo l'avvio.
4. **I contatori non avevano un lettore dove il prodotto gira davvero**: stderr è scartato dal
   servizio Windows e `Trace` richiede un debugger attaccato. Il riepilogo di fine run viene
   ora scritto **anche nel DB log**, dritto attraverso lo store e attorno alla coda chiusa
   (nessuna ricorsione). Non sul percorso di rinuncia: lì lo store è proprio ciò che non ha
   risposto, e scrivergli spenderebbe il budget che il cap ha appena imposto.
5. **Throttle dei breadcrumb a una sola casella**: una raffica di drop poteva mangiare la
   finestra di un guasto vero del sink. Ora drop e failure hanno una casella ciascuno.
6. **Test C18 dipendente dalla risoluzione del timer**: su una macchina con timer a 1 ms i 1 000
   item si percorrono dentro il budget e il codice non fixato passerebbe. Ora asserisce **anche**
   che la camminata è stata interrotta (`Remaining > 0`).
Verificati puliti: nessun catch che possa risalire da `StopAsync`, nessuna attesa illimitata né
deadlock nel drain, l'ordine di stop (confermato con un probe su net10, oltre che dal test),
l'esclusione ereditata di 11a intatta, nessun checkpoint scritto da uno scan annullato, escaping
LIKE completo e unico `LIKE` scritto a mano nel codebase, layering, test contro l'implementazione
reale senza rischio di hang. Da una rilettura mia, nello stesso giro (`066a89b`): il fallback su
stderr tollera anche `ObjectDisposedException` — un breadcrumb non deve poter far fallire uno
stop.
**Limiti noti e accettati:**
- **Il `GenericWebHostService` si ferma dopo il flush.** È registrato da
  `WebApplication.CreateBuilder` prima di qualunque nostra riga, quindi è l'unico che stoppa
  dopo la chiusura della coda: le ultime righe di Kestrel non finiscono nel DB log (restano su
  console). Sono categorie `Microsoft.*`, già capate a Warning da `LogCategoryPolicy`.
- **Ciò che viene loggato dopo il drain è perso per il DB log**, per costruzione: la coda è
  chiusa, e i record vengono contati come drop. Il provider console resta attivo.
- **`LogFlushService` non parla via `ILogger`**, di proposito: gira mentre la pipeline di logging
  si smonta, e un provider già disposto trasforma una chiamata di log in un'eccezione che fa
  fallire lo stop (visto davvero, con il provider EventLog sotto il test host). Quello che il
  drain ha da dire lo dice su stderr/`Trace`.
- **I contatori di perdita non hanno un endpoint dedicato**: il riepilogo di fine run finisce nel
  DB log (quindi nella schermata Log, categoria `FileTracert.Host.Logging.SqliteLogProcessor`) e
  nei breadcrumb; leggerli **durante** il run richiederebbe un endpoint diagnostico, materiale da
  11d.
- **Il drain sul fallimento di avvio non ha un test automatico**: `WebApplicationFactory` non
  esegue il `finally` di `Program` (verificato con un probe, non assunto), quindi la copertura
  possibile sarebbe un test che avvia un processo vero — territorio dello step 12.
- **Una scansione annullata resta segnata `Failed` dal tracker** (`ScanStatusTracker.Fail` nel
  `catch` di `ScanVolumeAsync`): comportamento pre-esistente, non toccato. In shutdown non si
  vede — il client se ne è già andato — ma «annullata» e «fallita» restano la stessa cosa per il
  tracker.
- **Nessuno scenario harness nuovo**: nessuno di questi fix cambia il comportamento su file veri
  (lo diceva già il task). La suite è stata comunque eseguita per intero.

### Fatto nello step 11b (2026-08-19, commit `82bf73a`…`0bba4bf`)
**WP6 chiuso** (finding 10): la fattibilità a spazio smette di credere alle proprie stime.
- **Il ricontrollo hard legge il disco.** `JobExecutionEngine` confrontava la domanda con
  `Volumes.FreeBytesLastKnown`, scritto dall'ultimo `VolumeSync`: bastava che un altro processo
  scrivesse decine di GB nel frattempo e il job passava il check per morire disk-full a metà, con
  la destinazione a pezzi e il sorgente ancora al suo posto — il «copiare sulla fiducia di una
  stima» vietato dal §4. Ora i byte liberi arrivano dal **dispositivo**, attraverso la port
  (`Business` non vede Win32, §3).
- **`IVolumeProbe.TryGetFreeBytes`**, non `TryGetByGuid`: quest'ultimo enumera **tutti** i volumi
  e risolve la topologia dei dischi via WMI per rispondere, che è troppo lavoro per l'unico numero
  che serve prima di ogni job cross-volume. Una `GetDiskFreeSpaceEx` sul volume GUID path, che fa
  anche da **prova di presenza**: un volume smontato fallisce la chiamata invece di restituire un
  numero vecchio. `null` non è un'eccezione, è il volume che dice «non ci sono» (log completo con
  l'errore Win32, §9).
- **`SpaceCheck` (Business/Operations) è l'unico posto che risponde «ci sta?»**, in due viste:
  **planning** (preview/enqueue: le liberazioni promesse contano, e un volume che non risponde
  ripiega sull'ultimo valore noto invece di far rifiutare il job — §4 «mai rifiutare all'enqueue»)
  e **hard** (esecuzione e rivalutazione: nessun credito alle promesse, e un volume che non
  risponde **blocca** il job, `TargetVolumeOffline`, riserva mantenuta, riattivabile). È
  **scoped** e memoizza per volume: una passata di rivalutazione giudica tutti i candidati su
  **una** fotografia del drive, e una lista di cinquanta job bloccati costa un probe per volume,
  non cinquanta.
- **La rivalutazione usa lo stesso oggetto dell'engine.** Liberare un job su un numero che
  l'engine contraddice un istante dopo è il ping-pong `Blocked → Pending → Blocked` senza che si
  muova un byte.
- **`SpaceMarginPercent` è finalmente un consumatore vero** (era seedato a 3 e letto da nessuno).
  Il margine è una percentuale **della domanda**, non del libero: copre lo scarto fra la somma
  delle dimensioni e ciò che atterra davvero (slack di cluster, metadati, stream) più chi scrive
  sul drive mentre copiamo — tutte cose che crescono con l'operazione, non con il disco. Una
  percentuale del libero pretenderebbe 60 GB di franco per spostare un kilobyte su un volume da
  2 TB, e quasi nulla su un volume pieno: esattamente al contrario. Il ledger lo riceve come
  parametro (`marginBytes`) e **non** legge le settings da solo — è un singleton su un percorso
  caldo; `SpaceCheck` legge la percentuale una volta per scope e la **clampa a 0–50%**, con log:
  un 300 per errore di battitura non deve parcheggiare la coda in silenzio. `FeasibilityResult`
  riporta `RequiredBytes` e `MarginBytes` **separati**, così la domanda resta la dimensione onesta
  dell'operazione. Il margine vale anche in planning: promettere all'enqueue spazio che l'engine
  poi rifiuta sposta solo la delusione più avanti.
- **`EstimateIsLive` dice la verità**: prima significava «la riga del volume dice `IsOnline`»,
  cioè un dato dell'ultimo sync vestito da lettura fresca. Ora è vero **solo** quando il numero
  è stato letto dal dispositivo in quell'istante; quando il volume non risponde si mostra
  comunque l'ultimo valore noto — è il migliore che c'è — ma marcato non-live (stesso principio
  dell'indicatore di connessione di 10c). Preview, enqueue e la colonna fattibilità della Coda
  passano dallo stesso `SpaceCheck`: il deficit mostrato per un job bloccato viene dal **medesimo**
  ricontrollo che l'ha parcheggiato, margine incluso, invece che da una stima che citava un altro
  numero.
- **Il decremento accumulato a fine job è stato tolto** (la domanda esplicita del task). Il
  completamento sottraeva i byte del job a `FreeBytesLastKnown` del target e accreditava quelli
  del sorgente. Con il probe che scrive ciò che misura, quell'aritmetica cade su un numero che
  **già** contiene i byte appena scritti: l'ha beccato l'harness sul ferro, con la stima esattamente
  una dimensione-job sotto la verità fino al probe successivo. E la metà sorgente non era comunque
  vera: i file vanno nel **cestino**, quindi quei byte non sono liberi finché il cestino non si
  svuota — accreditarli è proprio l'ottimismo che fa partire una copia che il drive non può
  chiudere. Sopravvive il probe: `FreeBytesLastKnown` contiene **solo misure** (volume sync, o il
  refresh del ricontrollo hard). La seconda metà del finding #7 («il retry non deve decrementare
  due volte») diventa irraggiungibile invece che gestita.
- **Verifica**: xUnit **631 verdi** (+18), build backend pulita (warnings-as-errors). RED dimostrato
  rimettendo il prodotto com'era: valore stantio al posto del probe → **7 test rossi**; margine
  ignorato nel ledger → **2**; planning sulla stima → **3**. Harness sul ferro **44 scenari,
  44 PASS / 0 FAIL** sulla coppia cross `D:\Collaudo\A` → `C:\Collaudo\B` (E: non esiste più su
  questa macchina; il volume secondario è stato preso su C:). Fra questi il nuovo
  `live-space-recheck` e il nuovo `space-margin`. Nota: `job-dependencies` cross, FAIL
  pre-esistente dallo step 10b, **passa** da 11a. Flakiness pre-esistenti osservate una volta
  ciascuna sulla suite completa e verdi in isolamento (`LogsApiTests`, `DatabaseInitializerTests`,
  `DomainApiTests`): DB reali in `%TEMP%` condiviso, non regressioni di questo giro.
La **code review finale** (indipendente, sulle modifiche di questo giro) ha trovato sei cose reali,
tutte corrette nel commit `0bba4bf`, nessuna bloccante:
1. il probe restituiva `lpTotalNumberOfFreeBytes` invece di `lpFreeBytesAvailableToCaller` — sotto
   una quota NTFS è spazio su cui *questo* processo non può scrivere, quindi il check sarebbe
   passato e la copia sarebbe morta lo stesso: ora si prende il **minore** dei due;
2. un volume presente ma momentaneamente illeggibile veniva parcheggiato su `TargetVolumeOffline`
   con una frase **in inglese** che nominava il volume per Id, sotto un'etichetta della Coda che
   dice «volume di destinazione offline»: etichetta e messaggio descrivevano due situazioni
   diverse, una delle quali falsa. Stessa lingua e stesso nome del volume di `VolumeOfflineGate`,
   con «non risponde» al posto di «non è collegato»;
3. tolto il fold, `FreeBytesLastKnown` restava alla misura **pre-copia** proprio dopo il job che
   ha spostato più byte di tutti — e lo leggono Dashboard, `VolumeStatusChanged` e il fallback del
   planning. Il completamento ora **ri-misura** i due volumi (una lettura, non l'aritmetica che è
   stata tolta): l'invariante «solo una misura scrive quella colonna» regge senza lasciarla
   vecchia di un job intero;
4. la lista della Coda era diventata un percorso che tocca il dispositivo: una syscall e, per un
   job bloccato su un drive rimosso, **due Warning per ogni refresh di schermata**, più il rischio
   di piantare la richiesta API su un device mezzo staccato. `SpaceCheck` non interroga più un
   volume che il catalogo sa già scollegato;
5. `required + margine` poteva **overfloware** a una domanda negativa e dichiarare fattibile un job
   impossibile (ora satura, fallendo *chiuso*), e il segnaposto «niente da controllare» dichiarava
   una misura mai fatta (ora esplicitamente non-live, e la lista non allega alcuna fattibilità a un
   job che non ha una domanda di spazio);
6. l'harness non seedava `AppSettings`, quindi **sul ferro tutti gli scenari giravano a margine 0**
   e il default di produzione (3%) non veniva mai esercitato.
La review ha verificato pulite: layering (§3), assenza di duplicazione (tutti i call site di
`ComputeFeasibilityAsync` passano da `SpaceCheck`), rilascio del ledger ancora dentro la
transazione terminale, nessuna riserva fantasma, nessun `Failed` al posto di un `Blocked`
recuperabile, vita del memo corretta per ogni scope di produzione, e test veri (ledger/engine/SQLite
reali, sostituita solo la port di piattaforma).

**Deviazioni e limiti noti:**
- **Un job ripreso da un checkpoint non ri-controlla lo spazio** (il ricontrollo sta dentro
  `if (job.State == JobState.Pending)`): un job interrotto in `Copying` riprende la copia senza
  chiedere di nuovo al disco. È **pre-esistente** e non è stato toccato qui, ma dopo questo giro è
  l'ultimo buco rimasto nel «mai copiare sulla fiducia di una stima» — candidato per i WP minori.
- **Il planning continua ad accreditare come liberazione i byte che finiscono nel cestino**
  (`includeQueuedLiberations: true` conta `FreedBytesSource`). È coerente con §4 (il planning non
  rifiuta mai), ma va detto accanto al punto sopra: è una promessa che la vista hard non onora.
- **`FeasibilityResult.MarginBytes` non ha ancora un lettore**: il deficit mostrato in Coda
  **include** il margine, quindi l'utente legge un numero più grande della dimensione
  dell'operazione senza la spiegazione — che esiste nel DTO ma la UI non la usa (è 11d).
- **Gli scenari harness non riempiono il drive**, come invece suggeriva il task. Con la fattibilità
  che legge il dispositivo, la scarsità si arrangia sul lato **domanda** (dimensione indicizzata,
  o `RequiredBytesTarget` del job già accodato = «un altro processo ha riempito il disco dopo
  l'enqueue»): è lo stesso confronto visto dall'altro capo. Una zavorra vera avrebbe dovuto
  portare a zero un volume da centinaia di GB — e sul ferro disponibile il volume di destinazione
  è **C:**, il disco di sistema. Non è un test, è un disservizio, e un processo ucciso a metà
  lascerebbe il drive pieno.
- **La ripresa «A libera lo spazio che serve a B» ora dipende dalla fisica.** Finché i sorgenti
  finiscono nel cestino, il completamento di A **non** libera davvero i byte, e B resta `Blocked`
  finché il cestino non viene svuotato (prima ripartiva sulla stima e sarebbe morto a metà copia).
  `fifo-auto-recovery` è stato riscritto di conseguenza: B è bloccato dallo spazio che A ha
  **prenotato**, e riparte da solo quando A rilascia. Se in futuro si vorrà la vecchia promessa,
  la strada è svuotare/contabilizzare il cestino, non tornare a fidarsi di una stima.
- **`BlockedJobRevaluator` esegue i gate offline/spazio prima del refresh degli snapshot**
  (~:92/:101): segnalato in 11a, **non** riordinato qui — non è materia di questo task.
- Il **frontend non è stato toccato** (è 11d).

### Fatto nello step 11a (2026-08-19, commit `455c9ac`…`c399048`)
**WP5 chiuso**: i quattro difetti di correttezza dell'indice che sopravvivevano fino al re-scan
successivo (#6, C19, C16, P2) e il **FAIL harness pre-esistente** di `job-dependencies` sulla
coppia *cross*.
- **#6 — l'FRN non attraversa i volumi.** L'`UsnFileRef` è l'identità di un file **dentro un
  volume** (gli indici MFT bassi si ripetono su ogni volume NTFS): portarselo dietro faceva
  scattare l'indice unico `(VolumeId, UsnFileRef)` **dopo** che i byte erano già stati spostati,
  ribaltando a `Failed` un job fisicamente riuscito — e il retry saltava gli item `Done` e
  ribatteva sulla stessa violazione. Azzerato nella **stessa** `SaveChanges` che sposta la riga
  (una transazione a parte lascerebbe la finestra aperta), da un unico helper
  `IndexUpdater.RepointToVolume` usato dai **tre** percorsi che cambiano volume a un file:
  completamento `MoveFile`, `MoveFolder` cross e la **riconciliazione del cancel**, che aveva lo
  stesso difetto e non era nel finding. `QuickHash`/`Hash` restano: sono funzione del contenuto,
  non del volume, e l'unico lettore (`BulkIndexWriter.ScanMerge`) li tratta come fatti che uno
  scan non ri-deriva.
- **C19 — il rename ricalcola estensione e categoria.** `RenameFileIndexAsync` scriveva il nuovo
  `Name` e basta: `foto.jpg` rinominato `foto.txt` restava `Image` con estensione `jpg` fino al
  re-scan, e i filtri di Ricerca ci lavoravano sopra. Ora entrambi si ri-derivano con gli
  **stessi** helper della pipeline di scansione, e `IsIncluded` si riconcilia con
  `FileFilter.ShouldIncludeFile` — §4, mai un delete: fuori dall'allow-list → `false`, di nuovo
  dentro → `true`, con l'FTS che segue nella stessa direzione. Il test asserisce che **la ricerca**
  lo trova nella categoria nuova, non solo che l'entità è cambiata.
- **C16 — l'esclusione si eredita.** NTFS non propaga Hidden/System ai figli, e il filtro
  guardava solo gli attributi *propri*: i file dentro una cartella nascosta passavano, e la
  risalita che costruisce l'albero **resuscitava** la cartella esclusa come materializzata.
  Ora ogni directory scartata registra il proprio path in `ExcludedSubtrees` e un secondo passo
  butta via tutto ciò che sta a o sotto di esso — il che chiude anche la seconda metà: un file
  che lo scan non tiene non può creare i propri antenati. Raccolta **in streaming** e applicata
  a fine enumerazione, non propagata durante il cammino, perché **solo uno dei due motori
  cammina un albero**: il dump MFT non garantisce padre-prima-dei-figli, quindi un flag portato
  giù funzionerebbe su exFAT e mancherebbe in silenzio su NTFS. Un test per motore, e quello USN
  alimenta i record **figli-per-primi** apposta.
- **P2 — una collation sola per `MaterializedPath`.** SQL usava BINARY, la memoria
  `OrdinalIgnoreCase`: su un case-variant i due non erano d'accordo e la find-or-create
  inseriva una **seconda** riga per la stessa cartella. Risolto **sulla colonna** (`NOCASE` +
  migration `MaterializedPathNoCase`), non sui call site: chi confronta quel path sta in cinque
  posti e sistemarne quattro è il modo in cui il difetto torna. SQLite non altera una colonna sul
  posto, quindi la migration ricostruisce la tabella e con essa l'indice — che *deve* essere
  ricostruito, perché un indice conserva la collation con cui è stato creato. Stessa concordanza
  poi portata in memoria: i due `FirstOrDefault(d => d.MaterializedPath == oldPath)` che
  sceglievano la **radice del sottoalbero** usano ora `ScanPath.SamePath` (senza, un
  `MoveFolder` intra non cascadava nulla e un `RenameFolder` lasciava il `Name` vecchio).
- **Il FAIL harness cross — il dipendente segue il proprio file.** Diagnosticato, non assunto:
  una riproduzione in-process mostra il rename liberato che conserva `SourceVolumeId = 1` dopo che
  il file è atterrato sul volume 2. Risolvere l'item per identità dà il **path** giusto, ma un
  path è solo metà di «dove»: l'engine cercava il file sul drive sbagliato, `IOException` generica,
  `Failed` terminale per un'operazione perfettamente eseguibile un drive più in là. Ora
  `JobSnapshotRefresher` raccoglie il volume di ogni item risolto per identità e ri-punta il job.
  La **forma** del job non cambia mai: un *rename* muove entrambi i capi (la destinazione si deriva
  dal source aggiornato), un *move cross* muove il solo source e resta cross; ogni altro caso viene
  **parcheggiato con un messaggio** invece di essere riscritto. Per i casi seguiti il ledger non
  richiede nulla: entrambi i chiamanti (`BlockedJobRevaluator.UnblockAsync`,
  `QueueService.RetryAsync`) rifanno release-then-reserve subito dopo il refresh, quindi la voce di
  liberazione segue il nuovo volume da sola.
- **Un helper nuovo, non due regole.** La domanda «quale filtro governa QUESTO path?» serviva fuori
  dalla pipeline di scansione (il rename che ricontrolla la propria inclusione): la regola «radice
  attiva più specifica» vive ora una volta sola in `RootFilterResolver.MostSpecificRoot`, e
  `ScanService` la chiama al posto della propria copia inline.
- **Verifica**: xUnit **613 verdi** (+35), build backend pulita (warnings-as-errors). RED
  dimostrato **prima** di ogni fix, rompendo il prodotto apposta quando serviva. Harness sul ferro:
  **42 scenari, 42 PASS, 0 FAIL** su coppia intra ×2 + coppia **cross**, incluso `job-dependencies`
  cross che era il FAIL pre-esistente dello step 9c. Misura scan invariata: primo scan 0,68–1,88 s,
  re-scan 0,56–0,61 s su 2 002 file. Migration provata anche su una **copia del DB di produzione**
  (114 132 directory, 742 033 file, 28 job): righe intatte, `foreign_key_check` 0 violazioni,
  `integrity_check` ok.
La **code review finale** (indipendente, sulle modifiche di questo giro) ha trovato una cosa
**bloccante** e quattro minori, tutte corrette nel commit `91dca4b`. La bloccante: un `MoveFile`
**intra-volume** il cui file aveva nel frattempo cambiato drive veniva ri-puntato là in silenzio —
il `TargetRelativePath` di un move è un percorso che l'utente ha scelto *su quel volume* e non
nomina alcun drive, quindi seguire il file avrebbe scritto file veri su un disco mai scelto, senza
un messaggio. Ora parcheggia `Blocked` con la spiegazione. Le altre: `RetryAsync` committava lo
snapshot **riscritto a metà** quando il refresh si arrendeva (e quella coppia path/volume incoerente
è esattamente ciò che `PendingWorkGuard` legge come rivendicazione); il ramo FTS del rename non
guardava `IsPresent`; e tre commenti che promettevano più di quanto il codice facesse. La review ha
verificato uno per uno, e trovati puliti: layering (§3), assenza di catch muti (§9), propagazione
del `CancellationToken`, completezza del fix #6 (l'unico assegnamento a `FileEntry.VolumeId` in
tutto il backend), l'SQL reale generato da `InSubtree` e la migration (nessun trigger, FTS5 non
toccata, indici non-unique quindi nessun rischio da duplicati pre-esistenti).
**Limiti noti e accettati:**
- **Il gate offline e quello di spazio girano PRIMA del refresh** (`BlockedJobRevaluator`
  :92/:101 vs `UnblockAsync`). Se il file è atterrato su un volume online ma il **vecchio** source
  è scollegato, il gate incolpa il vecchio volume e il refresh non parte mai: il job resta
  parcheggiato per un'operazione eseguibile, finché l'utente non fa «Riprova» (che passa dal
  refresh). Ordinamento pre-esistente, non introdotto qui; riordinarlo tocca la macchina della coda
  ed è materiale da WP minori, non da 11a.
- **Il guard viene interrogato con volume e path PRE-refresh** (`FindConflictAsync` prima di
  `RefreshSnapshotsAsync`, sia nel revaluator sia in `RetryAsync`). Era già vero per i path; da
  questo giro può cambiare anche il volume. Limitato: l'engine intercetta come
  `Blocked(NameCollision)` e `FinalizePartial` rifiuta un target esistente, quindi nulla viene mai
  sovrascritto — ma l'invariante «una sola operazione pendente per entità» non è garantita per
  quella coppia. Stesso WP dell'altro.
- ~~**`IsPresent=false` per una decisione di filtro**: un file già indicizzato che finisce sotto
  una cartella diventata nascosta viene marcato assente invece che escluso. Fissato da un test,
  non benedetto.~~ **Chiuso allo step 11g**: ora è `IsIncluded=false` con la presenza intatta, e
  quel test è stato riscritto per asserire il comportamento giusto.
- **`NOCASE` piega solo l'ASCII** (stesso limite del merge di scan, step 9a): un case-variant
  **non ASCII** resta due path diversi, in SQL come in memoria. Costa una riga in più, mai una in
  meno. Le righe già duplicate in un DB esistente **non** vengono fuse dalla migration: sarebbe
  una migrazione di *dati* (ri-puntare file, job e overlay). Sul DB di produzione reale ce ne sono
  **zero**.
- **Tre letture in più sulla transazione di completamento** di un rename (`ExtensionCategories`,
  `AppSettings`, `WatchedRoots`), cioè con il write lock di SQLite in mano. Un rename = un job, il
  volume è basso; sollevarle prima del `BeginTransaction` è materiale da **11e** (efficienza).
- **Harness non elevato**: il giornale USN non è disponibile senza elevazione, quindi gli scan del
  collaudo sono passati dal motore a **enumerazione** (fallback previsto e loggato). La coppia
  cross usata è `D:\Collaudo\A` + `C:\Collaudo\B`, non `E:\` come da procedura: il drive E:
  non era collegato. La scratch area su C: è stata creata e rimossa a fine collaudo.

### Fatto nello step 10c (2026-08-18, commit `856af4b`…`9d0d8f4`)
Il frontend **ascolta**. I tre poll (Coda ogni 2,5 s, campanella ogni 30 s, tracker scansioni a
cadenza adattiva) sono spariti: restano una lettura ciascuno all'avvio, per lo stato che esisteva
già quando l'app si è aperta, e una dopo una richiesta di ri-scansione.
- **`RealtimeService` in `core/realtime/`** — `HubConnectionBuilder` su `/hubs/events`, token in
  **query string** (`?access_token=`), che è il contratto di 10b: l'handshake WebSocket del
  browser non può mettere header custom. Senza token si connette lo stesso — il 401 che segue è
  un segnale più chiaro di un client che non prova. Il servizio non conosce store né schermate:
  possiede la connessione e la sua **onestà**, cioè il signal `status`
  (`connecting | connected | reconnecting | offline`) e l'hook `onReconnected`.
- **Riconnessione a due strati.** La rampa automatica di SignalR arriva a 20 s e poi molla; da lì
  il retry è nostro, ogni 15 s, così un Host giù per un minuto viene ripreso senza ricaricare la
  pagina. **Ogni tentativo dopo il primo che riesce vale come recupero**, incluso un primo
  handshake fallito e ritentato: l'app è stata cieca allo stesso modo. Al recupero il bridge
  **rilegge**, perché i messaggi emessi mentre il socket era giù non tornano indietro e un client
  che riprende in silenzio mostra una schermata vecchia come se fosse viva.
- **`RealtimeBridge` è l'unico punto che conosce entrambi i mondi**: gli eventi patchano gli stessi
  SignalStore che le schermate già leggono, quindi nessun componente sa che esiste SignalR (§8).
  L'app initializer risolve il token **e poi** avvia — un initializer solo, non due: initializer
  separati vengono invocati in ordine ma non *attesi* in ordine, e una connessione aperta prima
  del token è un 401 garantito.
- **Patch mirate.** `JobProgress` muove il contatore di **una riga** e non ricarica mai la lista
  (l'engine lo emette una volta al secondo per job). `JobStateChanged` su un job fuori pagina
  significa lista vecchia → **una** ricarica, coalescata; un messaggio prima del primo load viene
  scartato, non c'è nulla da invecchiare. `VolumeStatusChanged` muove `dataIsLive`/`isStale`
  insieme a `isOnline`, così un dato *last-known* non resta mai vestito da live. `ScanProgress`
  fa upsert per volume e **droppa il volume sul frame terminale** `Done`/`Failed`, che è ciò che
  distingue «finita» da «connessione caduta». `NotificationRaised` incrementa il badge in locale e
  rilegge la riga **solo a pannello aperto** (il payload è snello per contratto).
  `ProjectionChanged` invalida Catalogo e Ricerca; le raffiche (un enqueue di 50 file = 50 job =
  50 eventi) sono **coalescate a 300 ms**, e una raffica che nomina due volumi diversi si allarga
  a «tutto ciò che è a schermo» invece di sceglierne uno.
- **Riconnessione = rilettura di ciò che è visibile, e solo quello**: dashboard, scansioni e
  campanella sempre; coda, volumi, catalogo e ricerca solo se l'utente c'è davvero stato.
- **Indicatore in shell (skill `impeccable`)** — **muto quando funziona**: la titlebar ha già un
  tray permanente «servizio attivo», e un secondo badge sempre acceso per il caso sano sarebbe
  cromatura che non significa mai niente; anche il primo tentativo tace, perché un avviso a ogni
  avvio a freddo insegna a ignorarlo. Compare quando le schermate hanno smesso di ricevere: ambra
  in riconnessione, rosso quando SignalR ha mollato, con l'etichetta a portare lo stato (mai solo
  colore). Da offline dice che quello a schermo è l'ultimo dato ricevuto e offre **«Riconnetti»**
  invece di far aspettare il timer. Stesso vocabolario del flag di scansione, alternativa
  `prefers-reduced-motion`, e il lato destro della titlebar diventa **un cluster flex con un gap
  solo**, così un elemento che va e viene non deve più sapere chi ha accanto.
- **Verifica**: Vitest **207 verdi** (+41), `ng build` ok (restano i 4 warning di budget SCSS,
  pre-esistenti), xUnit **578 verdi** (backend intatto: non è stato toccato un file). RED
  dimostrato rompendo il prodotto apposta — poll rimessi, routing del bridge tolto, token fuori
  dall'url, indicatore sempre visibile, patch della coda sostituita da una ricarica, frame
  terminale della scansione ignorato: **14 test rossi su 7 file**, poi tutti verdi al ripristino.
La **code review finale** (sulle modifiche di questo giro) ha trovato due cose, entrambe corrette
nel commit `9d0d8f4`: le raffiche di `ProjectionChanged` ricaricavano Catalogo e Ricerca una volta
per job (50 file accodati = 100 richieste per un solo gesto dell'utente); e `RealtimeService.stop()`
non aveva né chiamante né test, quindi è stato tolto insieme al flag `stopped` che serviva solo a
proteggerlo — ogni ramo rimasto nella macchina a stati è un ramo che un test percorre.
**Limiti noti e accettati:**
- **Lo split dei commit devia dal task**: i timer non se ne vanno in un commit proprio, viaggiano
  con gli store. Lo store che smette di pollare e la shell che smette di chiederglielo sono la
  **stessa compilazione**; separarli avrebbe lasciato un commit che non compila.
- **`RealtimeBridge` sta in `core/` e importa gli store delle feature.** È il punto di
  composizione e la dipendenza va in un verso solo (nessuno store importa il bridge), ma il
  prezzo è che gli store — non i componenti — finiscono nel bundle iniziale.
- **La Dashboard non reagisce ai `JobStateChanged`**: i contatori job delle card si aggiornano al
  caricamento della schermata e alla riconnessione, non a ogni transizione. Sarebbe una richiesta
  per transizione; da valutare nei WP minori se dà fastidio all'uso.
- ~~**Nessuna prova su ferro del push**~~ — **chiusa allo step 12b**: `specs/realtime.spec.ts`
  guida il socket vero verso `/hubs/events` del Host vero, con gli eventi provocati **fuori** dal
  browser. Resta scoperto il solo `JobProgress` (vedi il paragrafo di 12b).

### Fatto nello step 10b (2026-08-18, commit `6d41a46`…`f816ce2`)
Il §7 esiste davvero lato server: l'hub, i sei messaggi, l'autenticazione e i punti di emissione.
Il frontend continua a pollare fino a 10c — questo checkpoint chiude verde senza toccare Angular.
- **Port + record in `Contracts/Realtime/`** (`IRealtimePublisher`, i cinque messaggi di §7 più
  `NotificationRaised`, `RealtimeMethods` con i nomi dei metodi client, `NullRealtimePublisher`).
  `Business` pubblica attraverso la port e non vede SignalR: c'è un **test di layering** che
  fallisce se `Contracts`/`Data`/`Platform`/`Business` legano un assembly `Microsoft.AspNetCore.*`.
- **`RealtimeEvents` (Business) è l'unico varco** verso il trasporto. Esiste per due motivi: il
  `catch` che rende la pubblicazione best-effort (§9 — resilienza sì, silenzio no: eccezione
  loggata **per intero**, `OperationCanceledException` a Debug perché è solo lo shutdown) sta lì
  **una volta sola** invece che in dodici call site; e i payload si costruiscono dalle entità in
  un punto solo, così un evento ha una forma sola ovunque venga alzato. Il DI di `Business` lega
  la port a `NullRealtimePublisher` con `TryAdd` (harness e avvii senza trasporto), `Host` la
  **sostituisce** con `Replace`.
- **`FileTracertHub` su `/hubs/events`**, vuoto di proposito: flusso unidirezionale server →
  client, **broadcast** senza gruppi né subscribe — single-user su loopback, "broadcast" e "mandalo
  all'unica UI" sono la stessa cosa, e un protocollo di sottoscrizione aggiungerebbe solo stato che
  può divergere da ciò che il client mostra. `AddJsonProtocol` con `JsonStringEnumConverter`, come
  la Web API: gli enum viaggiano **come nomi**, che è il contratto su cui 10c scriverà i tipi TS.
- **Token anche su `/hubs/*`**, header **oppure** `?access_token=…` — l'handshake WebSocket del
  browser non può mettere header custom. Confronto sempre fixed-time, 401 senza. Il compromesso
  (una query string finisce nei log) è **scritto accanto al codice** e verificato, non dato per
  buono: `LogCategoryPolicy` tiene `Microsoft.AspNetCore.Hosting.Diagnostics` sotto Warning a
  qualunque livello utente, quindi la request line non viene mai scritta — c'è un test che lo dice.
- **Punti di emissione, uno per evento**: `JobExecutionEngine` (ogni transizione persistita, i
  terminali, il blocco) e `QueueService` (enqueue/cancel/retry) per `JobStateChanged`;
  `BlockedJobRevaluator` per il rilascio e per il cambio di motivo; `VolumeSyncService` per
  `VolumeStatusChanged`; `ScanStatusTracker` per `ScanProgress`; `NotificationService` per
  `NotificationRaised`. `ProjectionChanged` esce **solo dove l'overlay si è davvero mosso**
  (terminali, enqueue, retry, rilascio): un `Blocked` **conserva** l'overlay e quindi tace.
  Scelto `QueueService` invece di `OverlayWriter` perché `OverlayWriter` scrive **dentro** la
  transazione, e la regola è pubblicare **dopo il commit**.
- **Throttle**: `JobProgress` sulla cadenza già usata per il salvataggio (`ProgressSaveInterval`,
  1/s) ma su **un solo orologio per l'intero job** — quello del DB riparte a ogni file, quindi un
  job da migliaia di file piccoli avrebbe spedito un messaggio per file; più un tick finale forzato,
  senza il quale una copia che finisce dentro la finestra lascia la barra a un messaggio dal 100%.
  `ScanProgress` throttlato a 500 ms per volume, ma **inizio, cambi di fase e frame terminale
  passano subito**: `Complete`/`Fail` ora spediscono un ultimo frame con `Done`/`Failed` prima di
  togliere la voce, altrimenti il client non distingue «finita» da «connessione caduta».
- **Verifica**: xUnit **578 verdi** (+20), build backend pulita (warnings-as-errors). RED
  dimostrato togliendo le chiamate di emissione dal prodotto: **8 su 8** i test degli emettitori
  falliscono. I test dell'hub usano un client SignalR **vero** (`HubConnectionBuilder`) sul
  `TestServer` — niente token e token sbagliato → 401, token in query string → connesso, un
  enqueue via API produce `JobStateChanged`, una notifica produce `NotificationRaised`, e i
  messaggi sono asseriti **come JSON grezzo** perché è quello che prova il contratto enum-stringa.
  Un publisher che lancia a ogni invio lascia comunque il job `Completed`.
La **code review finale** (sulle modifiche di questo giro) ha trovato due cose, entrambe corrette:
il cancel pubblicava il proprio stato **dopo** `RevaluateAsync`, quindi un client poteva vedere un
dipendente liberato prima dell'annullamento che l'aveva liberato; e una `OperationCanceledException`
del trasporto in chiusura veniva loggata come Error. Verificati uno per uno: nessuna pubblicazione
dentro una transazione (controllati anche i call site di `INotificationPublisher`), nessun evento
alzato in due posti, e il `ProjectionChanged` di un job cross-volume non nomina alcun volume — due
sono cambiati, e nominarne uno farebbe aggiornare al client la metà sbagliata.
**Limiti noti e accettati:**
- **Harness sul ferro**: 42 scenari, **41 PASS / 1 FAIL**. Il FAIL è `job-dependencies` sulla
  coppia *cross*, ed è **pre-esistente**: riprodotto identico su `7a87fd5` (HEAD prima di questo
  task) in un worktree separato. È il limite già documentato allo step 9c — il replay degli
  snapshot copre solo i move/rename di cartella **intra-volume**, quindi un dipendente che segue un
  move *cross-volume* resta con il path vecchio; qui però finisce `Failed` invece che `Blocked`
  con un messaggio. Da chiudere nei work package minori, non in 10b.
- `ScanStatusTracker` pubblica **fire-and-forget**: è un tracker sincrono in mezzo alla pipeline di
  scansione e non deve mettere il trasporto sul percorso critico. `RealtimeEvents` non lancia mai,
  quindi nessun task resta faulted; il prezzo è che due frame possono arrivare fuori ordine — ognuno
  porta i contatori completi e il frame terminale è quello su cui la UI si regola.
- Nessuno scenario harness nuovo: l'harness non monta l'hub (lo dice il task). La copertura del
  trasporto vero è nei test di integrazione sopra — e, **dallo step 12b**, negli E2E, che lo
  guidano dal browser sul socket vero.

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