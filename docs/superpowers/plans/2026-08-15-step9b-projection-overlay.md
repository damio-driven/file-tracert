# TASK — Step 9b: modello di proiezione, overlay `Pending*` scritto all'enqueue

> **Branch:** `develop` (nessun branch nuovo) · **Base:** `04793ae`
> **Prerequisito già posato:** step 9a — un re-scan fa merge, quindi `Pending*` e `Files.Id`
> sopravvivono. Scrivere l'overlay ha finalmente senso.
> **Chiude:** la prima metà del finding 9 del `CODE-REVIEW-HANDOFF.md` (§5 CRITICO di CLAUDE.md
> non implementato mentre la UI di scrittura è già shippata).
> **Questo file È il piano.** Implementare per commit, nell'ordine dato.
> **Sessione dedicata.** Tocca i file caldi della coda (`QueueService`, `IndexUpdater`,
> `JobExecutionEngine`): agente unico, niente parallelo. Fermarsi ai checkpoint.
> **Le decisioni tecniche qui sotto sono già prese** (regola CLAUDE.md del 2026-08-11):
> implementarle, non richiederne conferma. Se una si rivela sbagliata scrivendo il codice,
> cambiarla e **documentare il perché** nel commit.

---

## 1. Il problema, in una schermata

`grep -rn "Pending\(State\|Name\|JobId\|DirectoryId\|ParentId\) =" src/backend` trova **solo le
migration**. Nessun percorso di codice scrive l'overlay. Conseguenze già visibili all'utente:

1. Accodo un rename → il Catalogo continua a mostrare il nome vecchio, la Ricerca non trova
   quello nuovo (`FileSearchIndex` indicizza `f.Name` fisico —
   `FileTracert.Data/Search/FileSearchIndex.cs`, tutte le `INSERT … SELECT`).
2. Accodo un `CreateFolder` → nessuna riga `Directories` viene creata
   (`QueueService.BuildCreateFolderAsync`): la cartella non esiste in proiezione, quindi non
   si può nemmeno navigarci dentro per accodarci roba.
3. Accodo un move → il file resta mostrato nella cartella sorgente fino all'esecuzione. §5 dice
   l'opposto: *«Se accodo lo spostamento di un file e poi lo cerco, lo trovo già nella
   destinazione, con un badge di stato»*.
4. `CatalogFileDto.ProjectedState` e `SearchResultDto.ProjectedState` sono la **stringa
   letterale `"None"`** (`CatalogController`, `SearchController`) — il badge nel frontend
   (`catalog.html`, `search.html`: `@if (file.projectedState !== 'None')`) esiste già ed è
   codice morto.

---

## 2. Cosa deve fare il rework

**L'overlay è la verità di ciò che la UI mostra.** Tre pezzi, in quest'ordine logico:

| Pezzo | Regola |
|---|---|
| **Scrittura** | L'enqueue scrive i campi `Pending*` sull'entità **nella stessa transazione** del job (come già fa la riserva del ledger, `QueueService.EnqueueAsync`). Job e overlay commitano insieme o non commitano. |
| **Lettura** | Catalogo, Ricerca e FTS leggono il **nome proiettato** (`PendingName ?? Name`) e la **posizione proiettata** (`PendingDirectoryId ?? DirectoryId`, `PendingParentId ?? ParentId`). |
| **Pulizia** | L'overlay si azzera **solo** su stato terminale: `Completed` (dentro la transazione di completamento, dove l'`IndexUpdater` applica il fatto fisico), `Cancelled`, `Failed`. `Blocked` **conserva** l'overlay — il job è ancora in coda e la proiezione deve continuare a mostrarlo. |

### Mappa operazione → overlay

| Job | Entità | Campi scritti |
|---|---|---|
| `RenameFile` | `Files` (source) | `PendingName = NewName`, `PendingState = PendingRename`, `PendingJobId` |
| `RenameFolder` | `Directories` (source) | `PendingName = NewName`, `PendingState = PendingRename`, `PendingJobId` |
| `MoveFile` | `Files` (source) | `PendingDirectoryId = <dir target risolta>`, `PendingState = PendingMove`, `PendingJobId` |
| `MoveFolder` | `Directories` (source) | `PendingParentId = <dir target risolta>`, `PendingState = PendingMove`, `PendingJobId` |
| `CreateFolder` | `Directories` (**nuova riga**) | riga con `IsMaterialized = false`, `IsPresent = false`, `PendingState = PendingCreate`, `PendingJobId`, `Name`/`MaterializedPath`/`ParentId` risolti sulla proiezione |

**Decisioni prese, da non re-litigare:**

- **Un `MoveFolder` NON scrive overlay sui figli.** L'overlay sta solo sulla riga della cartella;
  i path dei discendenti seguono automaticamente perché il path proiettato si calcola risalendo
  i parent con gli overlay applicati (§5). Vale anche per il cross-volume da migliaia di file:
  un overlay per job, non uno per item.
- **Cross-volume:** l'overlay resta sulla riga **sorgente**; `PendingDirectoryId`/`PendingParentId`
  puntano a una `Directories` che vive su un **altro volume**. Il volume proiettato di un'entità è
  quindi *il volume della sua directory proiettata*, non `Files.VolumeId`. La riga cambia
  `VolumeId` solo all'esecuzione (`IndexUpdater.MoveFileIndexAsync` lo fa già).
- **La dir target di un move può essere una riga `PendingCreate`.** È esattamente il caso §5
  «creo in coda la cartella X e poi ci sposto dentro dei file»: la risoluzione del target
  all'enqueue lavora sulla proiezione, quindi trova la riga anche se `IsMaterialized = false`.
  Se non esiste nessuna riga per quel path, l'enqueue la crea `IsMaterialized = false` **senza**
  overlay (è una cartella che l'engine creerà comunque via `EnsureTargetDirectory`) — riusare
  `IndexUpdater.FindOrCreateDirAsync` invece di riscrivere la risalita dei parent.
- **`IsPresent = false` sulle righe `PendingCreate`**: la cartella non è mai stata vista sul disco,
  e il pass degli assenti del `DirectoryMerger` la marcherebbe comunque così. La visibilità nel
  Catalogo **non** si decide più su `IsMaterialized && IsPresent` ma su
  `(IsMaterialized AND IsPresent) OR PendingState <> None` (vedi §4).
- **Ordine di scrittura**: l'overlay si scrive **dopo** il gate offline
  (`QueueService.ApplyOfflineGateAsync`) e **indipendentemente** dall'esito — un job
  `Blocked(volume offline)` è comunque in coda, quindi in proiezione.
  ⚠️ **Eccezione che arriva con 9c** (vedi §9): un job che nasce
  `Blocked(DependencyPending)` **non** scrive l'overlay, perché l'entità è già di un altro job.
  Finché 9c non c'è, quel caso è impossibile: il guard attuale (`GuardFileAsync`/
  `GuardDirectoryAsync`) rifiuta la seconda op con 409. **Scrivere l'overlay dietro un singolo
  punto di uscita** (un metodo `ApplyOverlayAsync`) così che 9c debba solo renderlo condizionale.

---

## 3. Nome proiettato in FTS (§5)

La colonna `name` di `FileSearchIndex` deve contenere **`PendingName ?? Name`**. Tutte le
`INSERT … SELECT` di `FileTracert.Data/Search/FileSearchIndex.cs`
(`SyncVolumeFromDbAsync`, `RebuildAsync`, `SyncFilesAsync`) usano oggi `f.Name`: passano a
`COALESCE(NULLIF(f.PendingName, ''), f.Name)`.

**La colonna `path` resta il path fisico della directory + nome proiettato del file.** §5 è
esplicito: *«Un rename-cartella non tocca l'FTS»*. Un file dentro una cartella con rename pendente
si trova ancora col path vecchio in ricerca *path completo*, mentre a display il path mostrato è
quello proiettato. È una scelta, non una svista: va scritta come commento sul metodo, perché
sembra un'incoerenza a chi legge.

`UpsertAsync(fileId, name, path)` è già parametrico: i chiamanti (enqueue, cancel, completamento)
gli passano il nome **proiettato** corrente. Aggiungere un helper unico che, dato un `FileEntry`
+ la sua directory, produce la coppia `(nomeProiettato, pathProiettatoPerFts)`: serve in almeno
tre punti e duplicarlo è come partono le divergenze (cfr. K5).

**Efficienza:** l'upsert FTS per-file è 2 statement in autocommit (E4 nel handoff). Un enqueue
tocca **una** entità: accettabile, non aprire il rework set-based qui. Se il `MoveFolder`
cross-volume finisse a fare N upsert all'enqueue, è il segnale che si sta scrivendo overlay sui
figli — che è vietato sopra.

---

## 4. Lettura: Catalogo, Ricerca, path proiettato

### 4.1 Catalogo (`FileTracert.Host/Controllers/CatalogController.cs`)

Il figlio di una cartella si decide sulla **posizione proiettata**, non su quella fisica:

- sottocartelle: `COALESCE(d.PendingParentId, d.ParentId) == parentId`
- file: `COALESCE(f.PendingDirectoryId, f.DirectoryId) == parentId`
- visibilità dir: `(d.IsMaterialized AND d.IsPresent) OR d.PendingState <> None`
- il nome mostrato è `PendingName ?? Name`; l'ordinamento è sul nome **proiettato**
- gli stessi predicati valgono per i due contatori (`ChildCount`, `FileCount`), altrimenti il
  badge «3 file» e la lista che ne mostra 4 si contraddicono.

`CatalogFileDto.ProjectedState` / `CatalogDirDto` (che oggi **non ha** il campo) espongono
`PendingState.ToString()`. Aggiungere a `CatalogDirDto` `ProjectedState` e a entrambi
`PendingJobId` (nullable): la UI ci aggancia il link alla riga di coda, e senza il job id il
badge è un vicolo cieco.

### 4.2 Ricerca (`SearchController`)

`SearchResultDto.Name` = nome proiettato; `RelativePath` = **path proiettato**;
`ProjectedState` reale; `VolumeId`/`VolumeLabel`/`VolumeIsOnline` = quelli del **volume
proiettato** (la directory proiettata può stare su un altro volume). Il `"None"` hardcoded sparisce.

### 4.3 Path proiettato — dove vive

Nuovo componente in `FileTracert.Business/Projection/` (es. `ProjectedPathResolver`), **non**
nei controller: Host può dipendere da Business, il contrario no (§3). Contratto:

- carica **una volta** l'insieme pendente del/dei volumi coinvolti
  (`Directories` con `PendingState <> None`) — è piccolo per costruzione (MVP: una op per entità);
- risale i parent applicando gli overlay e restituisce il path proiettato per una lista di
  directory id, in batch (i risultati sono paginati: costo trascurabile, §5);
- **protezione dai cicli**: `PendingParentId` che punta dentro il proprio sottoalbero non deve
  mandare in loop infinito la risalita. `QueueService.BuildMoveFolderAsync` già rifiuta il move
  dentro sé stessa (C22) ma solo intra-volume: il resolver si difende da solo, con un limite di
  profondità e un log a livello `Warning` se lo raggiunge (mai un `catch` muto, §9).

---

## 5. Pulizia dell'overlay (crash-safety)

Un overlay che sopravvive al proprio job è peggio di nessun overlay: mostra un file in una
cartella dove non è e non arriverà mai.

- **Completed**: `IndexUpdater.UpdateAfterCompletionAsync` applica il fatto fisico
  (`file.Name = …`, `file.DirectoryId = …`) e **nello stesso `SaveChanges`** azzera
  `PendingName`/`PendingDirectoryId`/`PendingParentId`/`PendingState = None`/`PendingJobId = null`.
  Gira già dentro la transazione di completamento di `JobExecutionEngine.CompleteJobAsync` →
  gratis in termini di atomicità.
  Per `CreateFolder`: la riga `PendingCreate` diventa `IsMaterialized = true`, `IsPresent = true`,
  overlay azzerato. `FindOrCreateDirAsync` deve **ritrovare** quella riga (match per
  `VolumeId + MaterializedPath`) e non crearne una seconda.
- **Cancelled**: `QueueService.CancelAsync` azzera l'overlay nella **stessa transazione** del
  passaggio a `Cancelled` (dove già rilascia il ledger). Una riga `PendingCreate` cancellata resta
  `IsMaterialized = false`, `IsPresent = false`, overlay `None` → invisibile nel Catalogo, riga
  conservata (§6, no hard-delete).
  Vale anche per il ramo `HandleConcurrentStateChangeAsync` del `JobExecutionEngine`, che gestisce
  il cancel arrivato durante l'esecuzione.
- **Failed**: `JobExecutionEngine.SetFailedAsync` azzera l'overlay insieme allo stato terminale.
  Il job è ripescabile con *Riprova* (`RetryAsync`) → **`RetryAsync` ri-scrive l'overlay** quando
  riporta il job a `Pending`, altrimenti un retry perde la proiezione.
- **Blocked**: overlay **conservato**, in ogni ramo (`SetBlockedAsync`, gate offline all'enqueue,
  `BlockedJobRevaluator.KeepBlockedAsync`).
- **Riavvio / job orfani**: all'avvio dell'host, un pass di riconciliazione azzera gli overlay che
  puntano a un `PendingJobId` inesistente o già terminale. È la rete di sicurezza per ogni crash
  fuori transazione — piccolo, una query, va in `DatabaseInitializer` o in un seeder dedicato.

**Il re-scan non tocca l'overlay** — garantito dallo step 9a (il merge aggiorna solo i fatti
fisici). Non c'è nulla da fare qui, ma il test di non regressione va aggiunto lo stesso (§7.6).

---

## 6. Commit previsti

1. **`feat(business)`** — `ApplyOverlayAsync` + scrittura dell'overlay all'enqueue per i 5 tipi di
   job, nella transazione del job; `CreateFolder` che crea la riga `Directories`. Test unitari.
2. **`feat(business)`** — pulizia dell'overlay sugli stati terminali (`IndexUpdater`,
   `CancelAsync`, `SetFailedAsync`, `RetryAsync`) + pass di riconciliazione all'avvio.
3. **`feat(data)`** — nome proiettato in FTS (`COALESCE` nelle `INSERT … SELECT` + helper della
   coppia nome/path per gli upsert singoli).
4. **`feat(business)`** — `ProjectedPathResolver` (risalita con overlay, batch, guardia sui cicli).
5. **`feat(host)`** — Catalogo e Ricerca leggono la proiezione: predicati `COALESCE`, visibilità
   delle cartelle pendenti, `ProjectedState`/`PendingJobId` reali nei DTO, path proiettato.
6. **`feat(frontend)`** — badge di stato leggibili in Catalogo/Ricerca (etichette italiane: *in
   creazione*, *in rinomina*, *in spostamento*), modelli TS allineati ai DTO, riga cliccabile verso
   la coda. **Usare la skill `impeccable`** (CLAUDE.md §2/§8). Test Vitest.
7. **`test(harness)`** — scenario sul ferro (§8).
8. **`docs`** — aggiornare CLAUDE.md: §6 con i campi DTO nuovi se cambiano, roadmap con quanto
   fatto e quanto resta a 9c.

Un commit per preoccupazione: se il 5 diventa enorme, spezzare Catalogo e Ricerca.

---

## 7. Test (RED prima del GREEN, contro SQLite vero)

In `tests/FileTracert.Tests`, contro l'implementazione reale (mai mock del componente sotto esame):

1. **Enqueue rename → overlay scritto**: `PendingName`, `PendingState = PendingRename`,
   `PendingJobId` = id del job. Se l'insert del job fallisce, **nessun** overlay resta (transazione).
2. **Enqueue move → il file compare nella cartella destinazione** interrogando l'endpoint del
   Catalogo, e **non** più in quella sorgente.
3. **`CreateFolder` → la cartella esiste in proiezione**: appare tra i figli del parent, è
   navigabile, e un `MoveFile` verso di lei si accoda senza errore *prima* che il job di creazione
   sia eseguito (il caso §5 delle op che si validano sulla proiezione).
4. **Ricerca sul nome proiettato**: accodo un rename da `a.jpg` a `tramonto.jpg`, cerco
   `tramonto` → il file esce. Cerco `a.jpg` → non esce.
5. **Completamento azzera l'overlay** e applica il fatto fisico; **cancel** azzera l'overlay e
   lascia la riga com'era; **failed** azzera; **retry** ri-scrive; **blocked** conserva.
6. **Non regressione 9a**: overlay scritto → re-scan del volume → overlay ancora lì, `Files.Id`
   invariato (è il test che protegge l'intero castello).
7. **Path proiettato**: rename di una cartella con dentro tre file → i tre risultati di ricerca
   mostrano il path nuovo; l'FTS **non** è stata riscritta (asserire che gli id FTS non cambiano —
   §5 dice che il rename-cartella non la tocca).
8. **Move cross-volume**: il volume mostrato per il file pendente è quello **di destinazione**.
9. **Overlay orfano** (job cancellato a mano nel DB, simulando un crash) → il pass di
   riconciliazione all'avvio lo azzera.

Attenzione ai test esistenti che assumono `ProjectedState == "None"` o la posizione fisica nel
Catalogo (`catalog.store.spec.ts`, `search.store.spec.ts`, i test di `CatalogController`): vanno
**adeguati**, non aggirati. Se un test verde diventa rosso, capire *perché* prima di toccarlo.

---

## 8. Harness (obbligatorio, CLAUDE.md «Test»)

Nuovo scenario in `FileTracert.HardwareSmoke`, es. `projection-overlay`:

1. arrange su volume reale (`D:\Collaudo\A`), indicizza;
2. accoda `CreateFolder` + un `MoveFile` **dentro** la cartella appena accodata (dipendenza
   implicita sulla proiezione, non ancora sulla coda: quella è 9c);
3. assert **prima** dell'esecuzione: la cartella è tra i figli, il file è dentro di lei, i badge
   di stato sono quelli attesi, la ricerca per nome proiettato trova il file;
4. lascia eseguire la coda; assert **dopo**: overlay azzerato ovunque, cartella
   `IsMaterialized = true`, file fisicamente al suo posto, Catalogo identico a prima ma senza badge;
5. ri-scansiona il volume e ri-assert (chiude il cerchio con 9a).

Lo scenario `rescan-preserves-overlay` scrive oggi i campi `Pending*` **a mano** (lo dichiara nel
commento, perché a 9a l'enqueue non li scriveva): ora l'enqueue li scrive davvero → **aggiornare
lo scenario** perché passi dalla via reale, e togliere il commento che spiega il trucco.

PASS obbligatorio sul ferro configurato. `E:\Collaudo\B` **non esiste più** su questa macchina:
con un solo volume si ottiene la coppia *intra*, sufficiente per questo scenario. Rimettere
`appsettings.json` dell'harness a `Enabled: false` a fine sessione.

---

## 9. Consegna a 9c (leggere prima di chiudere)

9c aggiunge le **dipendenze tra job** e il **guard di enqueue unificato**. Due punti di contatto
da lasciare puliti:

- `ApplyOverlayAsync` deve essere **un solo punto di uscita**, chiamato dall'enqueue: 9c lo renderà
  condizionale (un job che nasce `Blocked(DependencyPending)` non possiede l'entità e **non** scrive
  overlay; lo farà quando si sblocca). Non spargere assegnazioni `Pending* =` in giro.
- Oggi la seconda op sulla stessa entità è respinta con 409 (`EntityAlreadyPendingException` →
  `OperationsController`). **Non toccare quel comportamento qui**: è 9c che lo sostituisce con
  `Blocked(DependencyPending)`. Se un test di 9b ne dipende, scriverlo in modo che 9c debba
  cambiarne l'aspettativa in un punto solo.

---

## 10. Criteri di accettazione

1. Accodare una qualsiasi delle 5 operazioni muta **immediatamente** la proiezione: Catalogo e
   Ricerca mostrano nome/posizione/volume di destinazione con il badge giusto.
2. Un `CreateFolder` accodato è navigabile e accetta operazioni al suo interno.
3. La ricerca trova il **nome proiettato** e non trova più quello vecchio.
4. Overlay azzerato su Completed/Cancelled/Failed, conservato su Blocked, ri-scritto su Retry,
   nessun overlay orfano dopo un riavvio.
5. Un re-scan non perde né overlay né identità (non regressione 9a).
6. Suite verde (xUnit + Vitest), build backend pulita (warnings-as-errors), scenario harness PASS
   sul ferro.

## 11. Code review finale (obbligatoria)

Review indipendente delle modifiche: correttezza vs criteri e scenari di fallimento; no silent
catch (§9); layering (§3 — il resolver in Business, non nei controller; nessuna SQLite-specific
fuori da `IFileSearchIndex`/`IBulkIndexWriter`); no duplicazione (il calcolo nome/path proiettato
esiste **una** volta sola); test reali RED→GREEN; idempotenza e crash-safety dei punti in cui
l'overlay si scrive e si azzera. Riportare cosa è stato trovato e corretto, o perché un rilievo è
stato lasciato consapevolmente.

## 12. Fuori scope

`DependsOnJobId`, `Blocked(DependencyPending)`, `DependencyCancelled`, guard di enqueue unificato,
`SequenceOrder` transazionale → **9c**. Device watcher e SignalR real-time → **step 10**. Chaining
di più operazioni sulla stessa entità → **fase 2** (§11). Non anticipare nulla di tutto questo: se
un fix ne richiede un pezzo, fare il minimo indispensabile e **segnalarlo**.
