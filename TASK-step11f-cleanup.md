# TASK — Step 11f: cleanup, dedup e discrepanze di layering (WP10)

> **Sessione dedicata, agente singolo. Ultimo dei sei task dei WP minori**
> (`TASK-step11-overview.md`): unifica helper che gli altri cinque stanno modificando —
> eseguirlo prima significa unificare la versione sbagliata.
> **Prerequisito: 11a, 11b, 11c, 11d, 11e mergiati**, suite verde, working tree pulito.
> Riferimenti: `CLAUDE.md` §3 (layering, shared kernel, SQLite dietro le interfacce), §9
> (niente codice duplicato); `CODE-REVIEW-HANDOFF.md` → K1, K2, K3, K4, K6, K7, K10, K11,
> K12, K13.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.
> ⚠️ Per il lavoro su SCSS/componenti (K10) usare la skill `impeccable`.

## Perché

Ogni voce qui è **duplicazione già divergente**: due copie della stessa regola che si sono
allontanate. Non è estetica — è il meccanismo con cui un fix applicato a una copia lascia
il bug vivo nell'altra (K5, chiuso allo step 9c, era esattamente questo e produceva
comportamenti diversi tra memoria e SQL).

K5 è chiuso. K8, K9 e K14 sono nel task 11d (frontend). Restano:

| # | Dove | Duplicazione | Fix |
|---|------|--------------|-----|
| K1 | `IndexUpdater.MoveFolderIntraIndexAsync` (~:115) vs `CascadeDirRenameAsync` (~:300) | Due volte la cascata sui path delle sottocartelle, **già divergenti** (una esce se la dir radice è null, l'altra prosegue) | Un solo `CascadeDirMoveAsync` — un rename è un move con parent invariato |
| K2 | `JobExecutionEngine.CleanupPartialsAsync` (~:953, 4 chiamanti) vs `QueueService.CleanupPartials` (~:398, 2 chiamanti) | Pulizia dei `.fadit-partial` duplicata, già divergente sul token di persistenza | Helper condiviso, una semantica sola |
| K3 | `BlockedJobRevaluator` (~:220-226) vs `QueueService` (~:380-386) | Blocco *release-then-reserve* copiato, guardie già divergenti | `NormalizeReservationAsync` su `ISpaceLedger` |
| K4 | `QueueService` — stanza cross-volume (TotalBytes / feasibility / Blocked) ripetuta tra MoveFile e MoveFolder | Due copie della stessa decisione di fattibilità | Un `ApplyCrossVolumeDemandAsync` privato |
| K6 | `Setup/WatchedRootPath.cs:13` (`Normalize`) e `:77` (`IsAncestor`) vs `Scanning/ScanPath` | `Normalize` byte-identico; `IsAncestor` ≈ `ScanPath.IsWithin` — **stesso assembly** | Chiamare `ScanPath` |
| K7 | `ScanPath` vive in `Business/Scanning` ed è usato da `Host` (`SearchController.cs:~104`) | Un helper di dominio condiviso che sta nel posto sbagliato | Spostarlo in `Contracts` (§3: shared kernel). Il brief lo chiede esplicitamente |
| K10 | `name-dialog.scss` vs `operation-picker.scss` | ~90 righe di chrome modale duplicate (differiscono per la sola larghezza) | Mixin/classe nel design system (`styles/`), skill `impeccable` |
| K11 | `Host/Controllers/OperationsController.cs` — try/catch ripetuto su 5 action, con il **404 deciso da `ex.Message.Contains("not found")`** (righe ~115 e ~134) | String-sniffing: riformulare un messaggio rompe il routing degli status | Exception filter a livello controller + eccezione tipizzata (`NotFoundException`). Ricordare che §9 vuole anche il **log** dell'eccezione convertita: oggi c'è solo su `Enqueue` (aggiunto in 9c), gli altri tacciono |
| K12 | `Host/Infrastructure/DatabaseInitializer.cs:~145` — `(SqliteConnection)db.Database.GetDbConnection()` per il probe FTS raw | SQLite-specific **fuori** dal boundary `IFileSearchIndex`/`IBulkIndexWriter` (§3) | Metodo dedicato sull'interfaccia (es. `IsEmptyAsync`) |
| K13 | vari | Stato ridondante: `IsOnline` + `DataIsLive` + `IsStale` = un bit in tre campi; `completedCount` computed inusato. **Nota**: `ScanPhase.Done/Failed` **non** sono più morti (li produce `ScanStatusTracker.Complete/Fail` dallo step 10b) — verificare prima di togliere | Ridurre, ma solo ciò che è davvero morto |

## Due discrepanze di layering da chiudere (dal brief, non dalla review)

1. **§3 dice** che `IBulkIndexWriter` e `IFileSearchIndex` stanno entrambi in `Contracts`.
   Sul codice: `IBulkIndexWriter` è in `Data/Indexing`, `IFileSearchIndex` in
   `Contracts/Search`. Segnalato allo step 9a e mai chiuso. **Decidere una volta**: o si
   sposta l'interfaccia (e allora servono i DTO di scambio in `Contracts`), o si corregge
   §3 perché la posizione attuale è quella giusta — `IBulkIndexWriter` parla di entità
   `Data`, e portarlo in `Contracts` trascinerebbe lì il modello. Qualunque esito, il
   codice e il brief devono dire la stessa cosa a fine task.
2. **K7** sopra: `ScanPath` in `Contracts`.

## Una decisione di prodotto (fermarsi e chiedere)

Dopo lo step 9a, un file che sta **fuori dai watched root attivi** viene marcato
`IsPresent=false` al primo scan del volume. Non è una regressione (prima veniva
*cancellato* dal truncate), ma ora è **visibile** come «assente» invece che sparito.

Le opzioni sono diverse fra loro per significato — «assente dal disco», «fuori dal
perimetro scelto», «non ancora guardato» — e la scelta cambia cosa vede l'utente nel
Catalogo. **È una decisione di prodotto: chiedere all'utente, non decidere da soli**
(`CLAUDE.md` → «Cosa resta all'umano»). Se la risposta arriva, implementarla in questo
task se è piccola; altrimenti aprire un task suo.

## Lavoro

Regole del giro:

- **Il cleanup non cambia comportamento.** Ogni unificazione parte dalle **due** versioni
  divergenti: capire quale delle due è corretta (con un test che le distingue), tenere
  quella, e scrivere nel commit qual era la differenza. Unificare "prendendo la prima"
  è come lanciare una moneta sul comportamento del prodotto.
- Se durante l'unificazione emerge un bug vero (probabile su K1, K2, K3), è un **fix**:
  commit separato, con il suo test RED→GREEN, non nascosto dentro il refactor.
- I test esistenti sono la rete: nessuno di essi va "aggiustato" per far passare il
  refactor. Se un test rosso ha ragione, ha ragione.

Ordine consigliato: K6, K7 e K12 (meccanici, chiudono anche il layering) → K11 → K1, K2,
K3, K4 (semantica delicata, coda) → K10 → K13.

## Split dei commit (indicativo)

1. `refactor(contracts): ScanPath moves to the shared kernel` — K7 + K6.
2. `refactor(data): the FTS emptiness probe lives behind its interface` — K12.
3. `refactor(host): typed not-found instead of message sniffing` — K11 (+ log §9).
4. `refactor(business): one cascade for folder rename and move` — K1.
5. `refactor(business): one partial cleanup` — K2.
6. `refactor(business): NormalizeReservationAsync on the ledger` — K3.
7. `refactor(business): one cross-volume demand routine` — K4.
8. `refactor(frontend): modal chrome in the design system` — K10.
9. `chore: drop the state that is actually dead` — K13.

## Test

- Nessun test **nuovo** è richiesto dove il refactor è puramente meccanico e la copertura
  esistente già distingue i comportamenti. Dove le due copie divergevano, serve invece un
  test che fissa **quale** semantica sopravvive (RED sulla copia sbagliata prima
  dell'unificazione).
- Vitest per K10 (il chrome modale condiviso non deve cambiare struttura DOM in modo che
  rompa i test dei dialog).
- Suite completa verde a fine task: è il criterio principale di un refactor.

## Harness sul ferro (obbligatorio)

K1, K2, K3 e K4 stanno nel percorso della coda: far girare **tutta** la suite harness
applicabile sulla coppia configurata e confrontarla con la baseline dell'ultimo task
(nessun FAIL nuovo, nessuno scenario diventato inapplicabile). Riportare i numeri.
Rimettere `appsettings.json` come stava.

## Definition of done

- xUnit + Vitest verdi, build backend pulita (warnings-as-errors), `ng build` ok.
- Harness senza FAIL nuovi.
- Le due discrepanze di layering chiuse: **codice e `CLAUDE.md` §3 concordi**.
- La domanda di prodotto (file fuori dai watched root) posta all'utente e la risposta
  registrata — implementata o rimandata con un task suo.
- **Code review finale** indipendente: nessun cambio di comportamento non voluto, no
  silent catch (§9), layering (§3), zero duplicazione residua tra le copie unificate.
- `CLAUDE.md`: paragrafo «Fatto nello step 11f» + chiusura della sezione «Work package
  minori» nella roadmap (il prossimo è lo **step 12 — Playwright**); in
  `CODE-REVIEW-HANDOFF.md` marcare K1/K2/K3/K4/K6/K7/K10/K11/K12/K13 come chiusi.
