# Code Review Handoff — FileTracert

> **Data:** 2026-07-12 · **Branch:** `develop` @ `66f0371` (working tree pulito)
> **Metodo:** review multi-agente a effort xhigh — 10 finder indipendenti (5 correctness,
> 3 cleanup, altitude, conformità CLAUDE.md) → dedup ~75 candidati → 5 verifier batch
> (protocollo 3-stati) → sweep finale sui gap.
> **Esito verifica:** 30 CONFIRMED · 2 PLAUSIBLE · 0 REFUTED sui 32 candidati verificati;
> +8 candidati dallo sweep (investigati in profondità, non passati dal verifier formale).
>
> **Scopo di questo file:** permettere ad altri agenti di riprendere il lavoro di fix
> senza rifare l'analisi. Ogni finding ha ancora file:riga e scenario di fallimento.
> Le righe si riferiscono al commit sopra: verificarle se il file è cambiato.

---

## Contesto — task ancora mancanti (NON ri-litigare)

Dal piano di CLAUDE.md §10 mancano: **step 9** (modello di proiezione: overlay
`Pending*` + dipendenze), **step 10** (DeviceWatcher + rivalutazione job + SignalR
end-to-end), **step 12** (test completi). La UI di scrittura coda (step 11) è però
**già shippata prima** di 9 e 10, quindi le lacune di proiezione/dipendenze sono
visibili all'utente oggi.

I debiti già documentati in CLAUDE.md §11 (truncate-per-volume che cancella
l'overlay, transazione monolitica di scan → SQLITE_BUSY, WalCheckpointWorker come
cerotto, classificazione Cloud) **non sono ri-riportati** qui se non per conseguenze
nuove e distinte.

**Filo conduttore emerso:** la state machine della coda è solida sul percorso felice
ma quasi ogni step viola l'idempotenza al crash, e il contratto §4/§5 (Blocked come
stato di parcheggio riattivabile, proiezione come verità) è oggi bypassato: i job
finiscono in `Failed` **terminale** dove la spec vuole `Blocked` riattivabile.

---

## TOP 15 — correctness, ordinati per severità (tutti CONFIRMED salvo indicato)

### 1. MoveFolder cross-volume: perdita dati silenziosa sui file non copiati
- **Dove:** `src/backend/FileTracert.Business/Operations/JobExecutionEngine.cs:402` (`DeleteSourceSubtreeAsync`) + `QueueService.cs:561` (`ExpandSubtreeAsync`)
- **Difetto:** l'espansione crea item solo per i file `IsPresent && IsIncluded`; la delete finale ricicla però l'**intero sottoalbero fisico** (SHFileOperation FO_DELETE ricorsivo). File esclusi dal filtro, non ancora scansionati o `IsPresent=false` vengono cancellati senza essere mai stati copiati.
- **Aggravante:** su volumi senza cestino (exFAT rimovibili) `FOF_ALLOWUNDO` (`NativeShell.cs:20`) degrada a delete permanente.
- **Scenario:** cartella 'Foto' indicizzata solo-immagini con sidecar `.xmp`/`.txt` → job `Completed`, sidecar spariti per sempre.
- **Direzione fix:** la delete del sorgente deve cancellare **solo ciò che è stato copiato e verificato** (delete per-item + rimozione dir solo-se-vuote), oppure il job deve enumerare il disco reale a esecuzione e fallire/bloccarsi se trova contenuto non previsto.

### 2. Race Cancel vs TransitionAsync: ricicla i sorgenti di un job annullato
- **Dove:** `JobExecutionEngine.cs:449-450` (`TransitionAsync`), re-read a mano a righe 186/195/202/209; `QueueService.cs:157-165` (`CancelAsync`)
- **Difetto:** nessun concurrency token nel modello (grep `IsConcurrencyToken|IsRowVersion|ConcurrencyCheck`: zero hit). `TransitionAsync` è un UPDATE cieco per PK: un `Cancelled` committato tra il re-read (202) e il `SaveChanges` (203) viene sovrascritto con `DeletingSource`. Il token del `JobCancellationRegistry` non salva: viene trippato **dopo** il commit, e il worker successivo registra un token fresco (`QueueProcessorWorker.cs:69`).
- **Scenario:** utente cancella durante Verifying → il motore procede a riciclare i sorgenti di un job annullato. Finestra di millisecondi ma riarmata a ogni edit della state machine (finestra identica anche tra 186→190).
- **Direzione fix:** concurrency token EF (rowversion) o `UPDATE ... WHERE State = @expected` condizionale; rimuovere i re-read a mano una volta garantito il guard.

### 3. Nessun gate offline: job su volume scollegato → Failed terminale
- **Dove:** `QueueProcessorWorker.cs:120-121` (nessun check `Volume.IsOnline` nella query Runnable); `Win32FileMover.cs:130-132` (throw 'no mount point'); `JobExecutionEngine.cs:102` (catch generico → `SetFailedAsync`)
- **Difetto:** `JobBlockReason.TargetVolumeOffline`/`SourceVolumeOffline` esistono **solo nell'enum** (`JobBlockReason.cs:7-8`), mai assegnati. `BlockedJobRevaluator.cs:41` filtra solo `InsufficientSpace`.
- **Scenario:** la promessa centrale del prodotto (accodare con drive scollegato, eseguire al remount) muore in pochi secondi: job Failed terminale, riserva rilasciata, nulla lo riprende al mount. Lo step 10 non avrà alcuna popolazione `Blocked(Offline)` da svegliare.
- **Direzione fix:** gate `IsOnline` in `PeekNextRunnableJobAsync` (o all'enqueue → subito `Blocked(TargetVolumeOffline)`); eccezione tipizzata `VolumeOfflineException` (il pattern `NameCollisionException` esiste già a riga 97) mappata a Blocked; estendere il revaluator ai block reason offline.

### 4. Tre step non idempotenti al crash (§4 'idempotenza di ogni step')
- **Dove:** `JobExecutionEngine.cs`
  - **Verifying** (:312-328): crash tra `FinalizePartial` (rename partial→final, :324) e `SaveChanges` (:328) → al resume l'item è ancora `Copied`, il verify cerca il `.fadit-partial` già rinominato (`Win32FileMover.cs:88` → false) → Failed. Il retry ricopia e `FinalizePartial` lancia `NameCollisionException` (`Win32FileMover.cs:100-101`) contro il **proprio output** → `Blocked(NameCollision)` per sempre.
  - **DeletingSource** (:356-363): un solo `SaveChanges` DOPO il loop di recycle: crash a metà → resume ri-ricicla path mancanti → `NativeShell.cs:33-34` throw → Failed; `RetryAsync` non resetta i `Verified` (`QueueService.cs:194-197`) → fallisce a ogni retry, per sempre.
  - **Op semplici intra-volume** (:128-142): `File.Move` non checkpointato (solo `StartedUtc`), job resta Pending∈Runnable: re-run dopo crash → `FileNotFoundException` → Failed benché l'op sia riuscita; `IndexUpdater` mai eseguito → catalogo/FTS stantii fino al re-scan.
- **Direzione fix:** ogni step deve essere resume-aware: Verifying accetta "final esiste + partial assente" come già-finalizzato; DeletingSource persiste per-item o tollera path già assente; op semplici rilevano "target esiste + source assente" come già-applicato e completano.

### 5. Riserve fantasma nel ledger sopravvivono al restart
- **Dove:** `SpaceLedger.cs:142` (`RebuildFromDbAsync` ricarica `.Where(e => e.IsActive)` senza filtrare per stato del job); deattivazione sempre in transazione separata: `CancelAsync` (157→165), `CompleteJobAsync` (473-474), `SetFailedAsync` (502-503)
- **Difetto:** crash nella finestra commit-stato-terminale → `ReleaseAsync` = riserva orfana `IsActive` su job terminale, ricostruita a ogni riavvio. Nessuna riconciliazione esiste; i `Cancelled` non sono nemmeno retryabili (`RetryAsync` 186-188).
- **Scenario:** feasibility sottostima lo spazio per sempre → job futuri `Blocked(InsufficientSpace)` su volume libero.
- **Direzione fix:** rilascio ledger nella **stessa transazione** del cambio di stato terminale, + riconciliazione al rebuild (scartare entries di job terminali).

### 6. ~~Move cross-volume mantiene UsnFileRef del volume sorgente~~ — **CHIUSO allo step 11a** (2026-08-19, commit `455c9ac`)
- **Dove:** `IndexUpdater.cs:86-87` (e :168 per MoveFolder): `file.VolumeId = targetVolumeId` ma `UsnFileRef` mai azzerato (assegnato solo in `ScanService.cs:395`); indice unico filtrato `(VolumeId, UsnFileRef)` in `FileEntryConfiguration.cs:45-47`
- **Scenario:** FRN del sorgente uguale a un FRN già indicizzato sul target (indici MFT bassi si ripetono su ogni volume NTFS) → `DbUpdateException` DOPO il move fisico completato → job ribaltato a Failed; retry salta i Done e risbatte sulla stessa violazione → loop permanente. Senza collisione: FRN stantio inquina il futuro matching dei delta USN.
- **Direzione fix:** azzerare `UsnFileRef` (e `QuickHash`? valutare) sul cambio volume; il re-scan del target lo riassegnerà.
- **Fatto:** `IndexUpdater.RepointToVolume` azzera l'FRN nella **stessa** `SaveChanges` che sposta la riga, sui **tre** percorsi che cambiano volume a un file (completamento `MoveFile`, `MoveFolder` cross, e la riconciliazione del cancel, che aveva lo stesso difetto e non era nel finding). `QuickHash`/`Hash` restano: sono funzione del contenuto, non del volume, e l'unico lettore (`BulkIndexWriter.ScanMerge`) li tratta come fatti che uno scan non ri-deriva.

### 7. IndexUpdater dopo Completed: ribalta il job e raddoppia il decremento spazio
- **Dove:** `JobExecutionEngine.cs:211-212`: `CompleteJobAsync` (persiste Completed + rilascia ledger) POI `UpdateAfterCompletionAsync` senza catch interno → eccezione (es. SQLITE_BUSY, occorrenza viva nel repo) → catch :102 → `SetFailedAsync` su job già Completed. Retry: fasi no-op, ma `CompleteJobAsync` :466-468 sottrae `RequiredBytesTarget` da `FreeBytesLastKnown` una seconda volta.
- **Direzione fix:** index update PRIMA del commit di Completed (o nella stessa transazione); decremento `FreeBytesLastKnown` idempotente (o derivato dal probe, non accumulato).

### 8. ~~Guard di enqueue con tre punti ciechi~~ — **CHIUSO allo step 9c** (`PendingWorkGuard`; snapshot ricalcolati allo sblocco per 8a)
- **Dove:** `QueueService.cs:593-595` (`GuardFileAsync` matcha solo `i.FileId == fileId`), :612-616 (`GuardDirectoryAsync` ispeziona solo il lato SOURCE su `Job.SourceVolumeId`)
- **Difetti:** (a) op cartella pendente (item `FileId=null`, righe 411/509) invisibile a `GuardFileAsync` → file-op sotto cartella con rename pendente accettata: il folder-job esegue prima (FIFO), lo snapshot `SourceRelativePath` del file-job è morto → `FileNotFoundException` → Failed permanente (retry non ricalcola lo snapshot); (b) i **TARGET** dei job pendenti non sono mai controllati: RenameFolder su una cartella destinazione di un move pendente → `EnsureTargetDirectory` resuscita il path vecchio (cartella rinominata + 'Docs' ricreata col file dentro); (c) i job `CreateFolder` (zero item) sono invisibili a `GuardDirectoryAsync`.
- **Direzione fix:** guard unificato che confronta path sorgente **e** target di tutti i job pendenti (incl. CreateFolder) contro sorgente e target del nuovo job, con matching subtree case-insensitive (vedi anche Cleanup C5).

### 9. ~~Proiezione (§5 CRITICO) + dipendenze non implementate ma UI write shippata~~ — **CHIUSO** (proiezione allo step 9b, dipendenze allo step 9c)
- **Dove:** `QueueService.cs:47-85` (`EnqueueAsync`), :292-333 (`BuildJobAsync`), :335-349 (`BuildCreateFolderAsync`)
- **Fatti verificati:** grep `Pending(State|Name|JobId|DirectoryId|ParentId) =` su src/backend → **solo migrations**; CreateFolder non crea righe `Directories(IsMaterialized=false)`; FTS aggiornata solo post-esecuzione (`IndexUpdater.UpdateAfterCompletionAsync`); validazione contro `MaterializedPath` (stato disco), non proiezione; `DependsOnJobId` mai assegnato; seconda op su entità pendente → `EntityAlreadyPendingException` → **409** (`OperationsController.cs:46-48`) invece di `Blocked(DependencyPending)`; `CancelAsync` senza logica dipendenti (`DependencyCancelled` mai assegnato).
- **Nota:** è lo step 9 pianificato — NON un bug da fixare in fretta, ma da tenere in cima al backlog perché già user-visible (ricerca non trova il nome rinominato, cartella accodata invisibile nel catalogo, cancellazione prerequisito non blocca i dipendenti che poi ricreano la cartella cancellata).

### 10. Ricontrollo spazio 'hard' su valore stantio e senza margine
- **Dove:** `JobExecutionEngine.cs:160-161` passa `tgtVol.FreeBytesLastKnown`; `SpaceLedger.cs:194-196` calcola senza termine di margine; `SpaceMarginPercent` seedato a 3 (`DatabaseInitializer.cs:95`) con **zero consumatori** (grep: entity + migrations + seed)
- **Scenario:** 40 GB scritti sul target da altro processo dopo l'ultimo VolumeSync → move da 35 GB passa il check e muore disk-full a metà. Esattamente il 'copiare sulla fiducia di una stima' vietato da §4.
- **Direzione fix:** probe live `GetDiskFreeSpaceEx` via `IVolumeProbe` al momento dell'esecuzione + applicare `SpaceMarginPercent`.

### 11. Filtro date ricerca: confronto lessicale rotto (verificato empiricamente)
- **Dove:** `FileSearchIndex.cs:240/245`: bind `ToString("o")` (`2026-07-10T00:00:00.0000000Z`) vs TEXT `ModifiedUtc` salvato come `2026-07-03 14:20:29.912` (spazio, no T/Z) su **entrambi** i percorsi di scrittura (BulkIndexWriter e EF — verificato nel DB di produzione `%LOCALAPPDATA%\FileTracert\filetracert.db`). `' '`(0x20) < `'T'`(0x54).
- **Scenario:** `modifiedFrom` a mezzanotte esclude ogni file modificato quel giorno; `modifiedTo` include l'intero giorno oltre il cutoff. Silenzioso.
- **Direzione fix:** bindare nello stesso formato di storage (`yyyy-MM-dd HH:mm:ss.FFFFFFF`) o confrontare su julianday/unixepoch.

### 12. DateTime Kind=Unspecified → tutti i timestamp UI sfalsati dell'offset locale
- **Dove:** `FileTracertDbContext.cs:24-27` (nessun `ConfigureConventions`/converter UTC; `HasConversion` solo su enum); `Program.cs:62-67` (solo `JsonStringEnumConverter`, nessun converter che appenda Z); `relative-time.pipe.ts:18` (`Date.parse` su stringa senza offset → ora locale)
- **Scenario:** macchina UTC+2: volume visto ora → '2 h fa'; offset negativi → tutto 'adesso'. Ogni timestamp DB-sourced in Dashboard/Catalogo/Ricerca/Coda/notifiche.
- **Direzione fix:** value converter globale `DateTimeKind.Utc` in `ConfigureConventions` (una riga per tipo) — fixa serializzazione JSON in cascata.

### 13. ~~Rivalutazione Blocked: mancano i trigger ('a ogni evento', §4)~~ — **CHIUSO** (offline al WP2, trigger su cancel allo step 9c)
- **Dove:** `BlockedJobRevaluator.cs:41` (filtra solo `InsufficientSpace`); unico caller `QueueProcessorWorker.cs:83-88` (solo dopo un Completed); `CancelAsync` rilascia la riserva (:165) ma non segnala (`_signal.Signal()` solo a righe 81/223) né rivaluta; Blocked ∉ `JobStates.Runnable` (`JobStates.cs:12-19`) → il poll da 30 s non lo tocca; nessun trigger mount/refresh.
- **Scenario:** cancello il job che ostruiva → il job Blocked ora fattibile resta Blocked per sempre (nessun altro job completa).
- **Direzione fix:** rivalutare su cancel + su volume-mount (aggancio per step 10) + endpoint refresh; estendere ai block reason offline (finding 3).

### 14. Cancel a metà Verifying/DeletingSource: né rollback né indice
- **Dove:** `QueueService.cs:171` + `CleanupPartials` (salta item con `TempPath == null`) — *dallo sweep, non passato dal verifier formale ma investigato a fondo*
- **Scenario:** cancel mid-Verifying → copie già finalizzate restano orfane sul target (mai indicizzate, mai ripulite); cancel durante DeletingSource → sorgenti già nel cestino ma righe `Files` ancora `IsPresent=1` → Catalogo/Ricerca mostrano file inesistenti + duplicati non tracciati sul target.
- **Direzione fix:** al cancel, riconciliare per-item: Verified → indicizzare la copia sul target (o riciclarla); Done → marcare il source `IsPresent=false`.

### 15. Albero Directories fantasma dopo MoveFolder cross-volume completato
- **Dove:** `IndexUpdater.cs:143` (`MoveFolderCrossIndexAsync` ri-punta solo `Files`) — *dallo sweep*
- **Scenario:** il sottoalbero `Directories` del sorgente (fisicamente riciclato) resta `IsMaterialized=true` col vecchio `MaterializedPath` → albero fantasma navigabile nel Catalogo; op successive verso quei path validano contro directory riciclate → fail o ricreazione silenziosa dell'albero cancellato. Persiste fino al full re-scan.
- **Direzione fix:** in `MoveFolderCrossIndexAsync` marcare/rimuovere il sottoalbero Directories sorgente (coerente con la policy no-hard-delete: `IsPresent=false` se si aggiunge il flag alle dir, o delete se accettato).

---

## Correctness fuori dal cap 15 (fixare comunque)

| # | Dove | Difetto | Verdetto |
|---|------|---------|----------|
| ~~C16~~ **CHIUSO 11a** | `FileFilter.cs:74-76` + `ScanService.cs:473-476` | Esclusione Hidden/System non propagata ai discendenti: i file dentro una dir nascosta passano `ShouldIncludeFile` (attributi propri puliti, NTFS non eredita Hidden) e `Ensure(parent)` **resuscita** la dir esclusa con `IsMaterialized=true` → alberi nascosti interamente indicizzati. **Fatto** (`c40ca0d`): ogni directory scartata dal filtro registra il proprio path in `ExcludedSubtrees`, e un secondo passo scarta tutto ciò che sta a o sotto di esso — raccolta in streaming e applicata a fine enumerazione, perché solo il motore a enumerazione cammina un albero (il dump MFT non garantisce padre-prima-dei-figli). Scartare i file discendenti chiude anche la seconda metà: un file che lo scan non tiene non può creare i propri antenati | CONFIRMED |
| C17 | `error.interceptor.ts:33,48` | Rilancia `new Error(message)` → ogni `instanceof HttpErrorResponse` a valle (operation-error.ts:11) sempre false = gestione strutturata 409/400 **codice morto**; inoltre legge `err.error?.message` ma il backend manda `{ error: ... }` → l'utente vede sempre il raw 'Http failure response … 400' | CONFIRMED |
| C18 | `ScanService.cs:270,288` | `CancellationToken.None` passato a `ReadFullSnapshot`/`Enumerate` (il token vero c'è ed è scartato) → fase di enumerazione (minuti) incancellabile → shutdown del servizio mid-scan supera `ShutdownTimeout` → kill sporco | CONFIRMED |
| ~~C19~~ **CHIUSO 11a** | `IndexUpdater.cs:59-61` | Rename non ricalcola `Extension`/`Category` (assegnate solo in `ScanService.cs:389-390`) → filtri ricerca (`FileSearchIndex.cs:217,225`) e `FilterReconciler.cs:62-65` operano su valori morti fino al re-scan. **Fatto** (`00195c0`): `RenameFileIndexAsync` ri-deriva entrambi con gli **stessi** helper della pipeline di scansione e riconcilia `IsIncluded` con `FileFilter.ShouldIncludeFile` (§4: mai un delete), aggiornando l'FTS nella stessa direzione. La regola «radice attiva più specifica» vive ora una volta sola in `RootFilterResolver.MostSpecificRoot`, usata anche da `ScanService` | CONFIRMED |
| C20 | `Win32FileMover.cs:38,50` | Collisione intra-volume → `IOException` grezza → Failed terminale invece di `Blocked(NameCollision)` (solo `FinalizePartial` lancia l'eccezione tipizzata) → l'enum è di fatto irraggiungibile per le op intra-volume | sweep |
| C21 | `QueueService.cs:518` + engine | MoveFolder cross-volume di cartella vuota/tutta-esclusa: `ExpandSubtreeAsync`=[] → la state machine marcia Pending→Completed **senza una syscall**: niente copiato, sorgente intatto, target mai creato — successo mentito | sweep |
| C22 | `QueueService.cs:478-541` | Move di cartella **dentro sé stessa** non rifiutato all'enqueue (né dal picker) → `Directory.Move(A, A\B\A)` → IOException → Failed invece di 400 | CONFIRMED |
| C23 | `Program.cs:37-40` + `SqliteLoggerProvider.cs:26-29` | `SqliteLogProcessor` registrato come istanza pre-costruita (DI non dispone istanze esterne) e `Dispose` del provider è no-op che si affida a quella premessa falsa → `DisposeAsync` mai chiamato → coda log (fino a ~10k record, inclusi gli errori di shutdown) persa a ogni stop | CONFIRMED |
| C24 | `SqliteLogProcessor.cs:60,66,79` | Tre `catch { }` nudi senza alcuna traccia (violazione §9 'no silent catch'); mitigante: il sink non può loggare su sé stesso, ma manca anche un breadcrumb Console/Debug | CONFIRMED |
| C25 | `operation-picker.ts:200-204` | Preview batch atomica (`previewBatch`) ma enqueue = **loop client** di POST singoli: fallimento all'item N lascia 1..N-1 accodati, `completed` non emesso, il re-click riparte da 1 → 409 EntityAlreadyPending che seppellisce l'errore vero. Nessun endpoint batch server-side | CONFIRMED |
| ~~C26~~ **CHIUSO 9c** | `QueueService.cs:295` | `SequenceOrder = MaxAsync()+1` letto fuori dalla transazione d'insert, nessun guard di unicità → enqueue concorrenti = ordine duplicato → la feasibility FIFO (che salta solo `e.SequenceOrder > mine`) doppia-conta reciprocamente le riserve | sweep |
| C27 | `operation-picker.ts:61` | `ngOnInit` lancia `volumes.loadList()` senza await e legge subito `volumes.catalogable()` → con store freddo il dialog si apre senza volume preselezionato né albero, `canSubmit` false senza spiegazione | sweep |
| C28 | `SqliteLogStore.cs:187` | Search log interpolata in `LIKE '%...%'` **senza** `EscapeLike` (il filtro Category una riga sopra è escapato) → cercare `100%` o `file_name` produce match wildcard errati | sweep |
| C29 | `logs.ts:53-56` | Timer debounce 300 ms senza cleanup su destroy (`implements OnInit` soltanto) → naviga via entro 300 ms → timer orfano muta lo store root-scoped + HTTP per vista morta | CONFIRMED |
| C30 | `DashboardStatsAssembler.cs:21-25` | Contatori coda hardcoded 0 ('placeholder step 8') ma la coda è shippata e `dashboard.ts:46,58-59` li renderizza → dashboard sempre a 0 mentre la pagina Coda mostra job reali | CONFIRMED |
| C31 | `SearchController.cs:42` + `catalog.models.ts:194-195` | Latente: backend richiede DateTimeKind≠Unspecified (400 senza 'Z') ma i tipi TS sono `string \| null` senza normalizzazione UTC → il primo input date UI romperà ogni ricerca filtrata | PLAUSIBLE-latente |
| C32 | `QueueService.cs:702` | `EnqueueAsync` risponde con `SourceVolumeLabel`/`TargetVolumeLabel` sempre null (nav property mai caricate; volumi letti AsNoTracking) — innocuo oggi solo perché il picker scarta la risposta | minor |

### PLAUSIBLE — servono test per confermare
| # | Dove | Ipotesi | Come chiudere |
|---|------|---------|---------------|
| P1 | `NtfsUsnReader.cs:263-264` | `nodes[frn] = ...` last-write-wins: se `FSCTL_ENUM_USN_DATA` emette un record per hard-link name, sopravvive un solo path (ordine-dipendente) | Test runtime: `mklink /H`, due link, contare le entry nello snapshot |
| ~~P2~~ **CHIUSO 11a** | `IndexUpdater.cs:185-186` | Confermato e chiuso (`8b0f106`): la colonna `Directories.MaterializedPath` porta ora `COLLATE NOCASE` (configuration + migration `MaterializedPathNoCase`, che ricostruisce la tabella e con essa l'indice — un indice conserva la collation con cui è stato creato). SQL e memoria dicono la stessa cosa. **Limite noto**, lo stesso già registrato per il merge di scan allo step 9a: `NOCASE` piega solo l'ASCII. Le righe già duplicate in un DB esistente **non** vengono fuse: sarebbe una migrazione di dati (ri-puntare file, job e overlay), non di schema |

---

## Efficienza (tutti CONFIRMED; radice comune: SQLite single-writer)

| # | Dove | Spreco | Alternativa |
|---|------|--------|-------------|
| E1 | `QueueService.cs:259` (`ListAsync`) | `.Include(j => j.Items)` per leggere solo `items.First()?.SourceRelativePath`; con MoveFolder da 100k file il poll UI da 2.5 s materializza 100k+ entità a colpo | Proiettare il primo path in SQL, niente Include |
| E2 | `ScanService.cs:362` (`PersistAsync`) | Directory inserite per-riga via `AddRange+SaveChanges` (~200k INSERT singoli su C:) dentro la transazione monolitica; `IBulkIndexWriter.BulkInsertDirectoriesAsync` **definito e mai chiamato** (grep: solo definizione) | Usare il bulk writer + commit per batch (si collega al rework §11 già pianificato per step 9) |
| E3 | `FileSearchIndex.cs:113-119` | `SELECT MIN(COUNT(*),10000)` non limita il lavoro: COUNT visita ogni match FTS + 2 join per riga | `SELECT COUNT(*) FROM (SELECT 1 … LIMIT 10000)` |
| E4 | `IndexUpdater.cs:232-242` (+ :176) | Upsert FTS per-file = 2 statement autocommit ciascuno (DELETE+INSERT, nessuna transazione ambiente) → rename di cartella da 50k file = 100k commit WAL | Set-based `INSERT…SELECT` — il pattern esiste già in `SyncVolumeFromDbAsync` (`FileSearchIndex.cs:39-53`) |
| E5 | `CatalogController.cs:82` | 2 subquery COUNT correlate per subdir, lista subdir non paginata, indice `IX_Files_DirectoryId` non copre i flag | Covering index `(DirectoryId, IsIncluded, IsPresent)` o count raggruppato unico |
| E6 | `DashboardController.cs:27` | `LongCountAsync` + `SumAsync` = 2 full scan sequenziali della tabella Files | Aggregato single-pass o stat cache |
| E7 | `ScanService.cs:233` | Catena LINQ + `OrderByDescending(k.Length)` ricostruita **per ogni item enumerato** (~3M alloc/sort su volume grosso) | Pre-sort dei root una volta, foreach first-match |
| E8 | `BlockedJobRevaluator.cs:64-75` | 3 transazioni di scrittura per ogni job sbloccato (SaveChanges + Release in scope nuovo + Reserve in altro scope) | Batch in una transazione |

---

## Cleanup / dedup (post-C7/C8 — cosa hanno mancato)

| # | Dove | Duplicazione / complessità | Fix |
|---|------|---------------------------|-----|
| K1 | `IndexUpdater.cs:101-136` vs `:210-226` | `MoveFolderIntraIndexAsync` ≈ `CascadeDirRenameAsync` (già divergenti: una torna su topDir null, l'altra procede) | Un solo `CascadeDirMoveAsync` (rename = move con parent invariato) |
| K2 | `QueueService.cs:229-250` vs `JobExecutionEngine.cs:561-586` | `CleanupPartials` duplicato (già divergenti sul token di persist) | Helper condiviso |
| K3 | `BlockedJobRevaluator.cs:69-75` vs `QueueService.cs:214-220` | Blocco release-then-reserve copiato (guardie già divergenti su `TargetVolumeId.HasValue`) | `NormalizeReservationAsync` su `ISpaceLedger` |
| K4 | `QueueService.cs:450-464` vs `:521-538` | Stanza cross-volume (TotalBytes/feasibility/Blocked) duplicata tra MoveFile e MoveFolder | `ApplyCrossVolumeDemandAsync` privato |
| ~~K5~~ **CHIUSO 9c** | `QueueService.cs:612`, `:553`, `JobExecutionEngine.cs:392`, `:427` | Matching subtree reimplementato ≥5 volte con **semantiche divergenti** (StartsWith case-sensitive in EF vs `OrdinalIgnoreCase` in memoria; `ScanPath.IsWithin` esiste ed è inusato lì) — correlato ai finding 8 e P2 | Un solo predicato subtree (in-memory + EF-translatable) con policy case/separator unica |
| K6 | `WatchedRootPath.cs:13,84` | `Normalize` byte-identico a `ScanPath.Normalize`, `IsAncestor` ≈ `ScanPath.IsWithin` (stesso assembly) | Chiamare ScanPath |
| K7 | `SearchController.cs:76`, `ManagedFileSystemBrowser.cs:30,37` | Path-join volume-relative reimplementato in Host e Platform (ScanPath è internal a Business) | Spostare ScanPath in Contracts |
| K8 | `queue.ts:11-12` vs `queue.store.ts:28-29` | `ACTIVE_STATES`/`TERMINAL_STATES` duplicati; copia store non tipizzata (`Set<string>`) → typo invisibile; la copia store governa il poll 2.5 s, quella component il rendering | Export unico accanto al tipo `JobState` |
| K9 | `catalog.ts:14-22` vs `search.ts:34-40` | Mapping categoria→label/icona duplicato e **già divergente** (singolare vs plurale, 'Other' assente in search) | Mappa condivisa |
| K10 | `name-dialog.scss` vs `operation-picker.scss` | ~90 righe di chrome modale duplicate (differiscono solo per width) | Mixin/classe nel design system (`styles/_components.scss`) |
| K11 | `OperationsController.cs:41+` | Try/catch identico copiato su 5 action, con 404 deciso da `ex.Message.Contains("not found")` (string-sniffing: una riformulazione lo rompe) | Exception filter a livello controller + `NotFoundException` tipizzata |
| K12 | `ScanService.cs:366`, `DatabaseInitializer.cs:137-143` | `PRAGMA defer_foreign_keys` in **Business** e probe FTS raw + cast `(SqliteConnection)` in **Host** — fuori dal boundary `IFileSearchIndex`/`IBulkIndexWriter` (§3) | Metodi dedicati sulle interfacce (`IsEmptyAsync`, unit-of-work con FK deferite) |
| K13 | vari | Stato morto/ridondante: `ScanPhase.Done/Failed/ResolvingPaths` mai prodotti (completamento = entry che sparisce dal tracker!); `IsOnline`+`DataIsLive`+`IsStale` = 1 bit in 3 campi; `completedCount` computed inusato | Ridurre |
| K14 | `operation-picker.ts:142` vs `name-dialog.ts:62` | `confirmNewFolder` valida solo non-vuoto; `validateLeafName` (name.util.ts) esiste e l'altro dialog lo usa → `foo\bar` passa in un dialog, bloccato nell'altro | Usare `validateLeafName` |

---

## Pacchetti di lavoro suggeriti (per agenti fix)

Ordine consigliato — WP1 è prerequisito dello step 9:

- **WP1 — State machine crash-safe** *(finding 2, 4, 5, 7 + C20)*: concurrency token su `OperationJob.State`; resume-awareness dei 3 step; release ledger transazionale con lo stato terminale; index-update prima/dentro il commit di Completed; `VolumeOfflineException`+`NameCollisionException` mappate a Blocked. Un unico rework in `JobExecutionEngine` + `SpaceLedger`.
- **WP2 — Gate offline + trigger rivalutazione** *(finding 3, 13)*: gate IsOnline, block reason offline assegnati, revaluator esteso (cancel/mount/refresh). Prepara il terreno allo step 10.
- **WP3 — MoveFolder sicuro** *(finding 1, 14, 15 + C21, C22)*: delete solo-copiato, riconciliazione al cancel, cleanup Directories sorgente, no-op e move-into-self rifiutati all'enqueue.
- ~~**WP4 — Guard di enqueue unificato** *(finding 8 + C26 + K5)*~~ — **CHIUSO allo step 9c (2026-08-16, commit `c9f0b34`…`616c25e`)**: `ScanPath.Overlaps` è l'unico predicato di sottoalbero (case-insensitive, segment-aware) e `PendingWorkGuard` l'unico posto che lo interroga, su source **e** target di ogni job non terminale, `CreateFolder` incluso; `SequenceOrder` è assegnato dentro la transazione di insert con indice unico e migration che rinumera i duplicati. Nello stesso giro: la metà «dipendenze» del **finding 9** (`DependsOnJobId` assegnato e ripuntato, `Blocked(DependencyPending)` al posto del 409, `DependencyCancelled` senza cascata, snapshot ricalcolati allo sblocco = fix reale del finding 8a) e l'ultima metà del **finding 13** (`CancelAsync` rivaluta e segnala). Dettagli, deviazioni dal piano e limiti noti in CLAUDE.md → «Fatto nello step 9c».
- ~~**WP5 — Correttezza indice/ricerca** *(finding 6, 11, 12 + C19, C16, P2)*~~ — **CHIUSO** (finding 11 e 12 nel giro «date/UTC»; finding 6, C19, C16, P2 allo **step 11a**, 2026-08-19, commit `455c9ac`…`c399048`). Nello stesso giro è stato chiuso anche il **FAIL harness pre-esistente** di `job-dependencies` sulla coppia cross: un dipendente segue ora il proprio file quando questo cambia volume. Dettagli e limiti noti in CLAUDE.md → «Fatto nello step 11a».
- **WP6 — Spazio** *(finding 10)*: probe live + margine da AppSettings.
- **WP7 — Frontend error/UX** *(C17, C25, C27, C29, C30, C31, K8, K9, K14)*.
- **WP8 — Logging/shutdown** *(C18, C23, C24, C28)*.
- **WP9 — Efficienza** *(E1-E8)*: da fare insieme al rework scan §11 dove si sovrappone (E2).
- **WP10 — Cleanup** *(K1-K14)*: meccanico, basso rischio, buon candidato per agente separato.

> Nota trasversale: molti fix di WP1/WP3 cambiano lo stesso file (`JobExecutionEngine.cs`) — assegnarli allo **stesso** agente per evitare conflitti. WP5/WP9 toccano `IndexUpdater.cs`/`FileSearchIndex.cs` in punti sovrapposti (E4 vs finding 6): coordinare.
