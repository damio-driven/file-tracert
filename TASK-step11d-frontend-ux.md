# TASK — Step 11d: errori e UX del frontend (WP7)

> **Sessione dedicata, agente singolo.** Quarto dei sei task dei WP minori
> (`TASK-step11-overview.md`). Prerequisito: working tree pulito, suite verde (xUnit +
> Vitest), Host chiuso.
> Riferimenti: `CLAUDE.md` §8 (architettura frontend, design system), §7 (API surface),
> §9 (no silent catch); `CODE-REVIEW-HANDOFF.md` → C17, C25, C27, C29, C30, K8, K9, K14.
> ⚠️ **Per tutto il lavoro UI usare la skill `impeccable`** (§2 e §8 del brief).
> ⚠️ Un pezzo di questo task è **backend** (endpoint batch di enqueue, `OperationsController`
> + `QueueService`): file caldi della coda → nessun altro agente su quei file in parallelo.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

Sono i difetti che l'utente incontra **mentre lavora**: un errore che non dice cosa è
successo, un accodamento che si rompe a metà lasciando la coda sporca, un dialog che si
apre vuoto, una dashboard che dice zero mentre la Coda mostra job veri.

Stato verificato su `88571aa` (riverificare le righe):

| # | Dove | Cosa succede oggi |
|---|------|-------------------|
| C17 | `core/**/error.interceptor.ts` (`return throwError(() => new Error(message))`) + `shared/api/operation-error.ts:14` (`e instanceof HttpErrorResponse`) | L'interceptor **rimpiazza** l'errore HTTP con un `Error` generico → ogni `instanceof HttpErrorResponse` a valle è sempre falso: la gestione strutturata dei 400 è **codice morto**. In più `describe()` legge `err.error?.message`, ma il backend risponde `{ error: … }` → l'utente vede il raw `Http failure response … 400`. |
| C25 | `shared/components/operation-picker/operation-picker.ts:~183` (`previewBatch`) vs `~194-215` (`enqueue`: `for (const item of this.items())` con una POST per item) | La **preview** è atomica lato server, l'**enqueue** è un loop client. Fallimento all'item N: 1..N-1 restano accodati, `completed` non viene emesso, il re-click riparte da 1 → la seconda passata trova le entità già pendenti e accoda dei `Blocked(DependencyPending)` che l'utente non ha chiesto (dopo 9c non è più un 409, ma resta un risultato che nessuno voleva). Manca l'endpoint batch lato server. |
| C27 | `operation-picker.ts:62-64`: `ngOnInit` fa `void this.volumes.loadList()` e **subito dopo** legge `this.volumes.catalogable()` | Con store freddo il dialog si apre senza volume preselezionato e senza albero, `canSubmit` falso senza che nulla spieghi perché. |
| C29 | `features/logs/logs.ts:38-56`: `searchTimer` con `setTimeout(…, 300)`, la classe implementa **solo** `OnInit` | Navigando via entro 300 ms il timer sopravvive: muta uno store root-scoped e spara una HTTP per una vista morta. |
| C30 | `Business/Dashboard/DashboardStatsAssembler.cs:21-25` (`QueuedJobs: 0, BlockedJobs: 0, RunningJobs: 0, PendingBytes: 0`, commento «placeholder step 8») + `features/dashboard/dashboard.ts` che li renderizza | La coda è shippata da tempo: la Dashboard mostra **zero** mentre la pagina Coda mostra job reali. |
| K8 | `features/queue/queue.ts:15-16` vs `features/queue/queue.store.ts:36-37` | `ACTIVE_STATES`/`TERMINAL_STATES` duplicati; la copia nello store è `Set<string>` **non tipizzata** → un typo è invisibile al compilatore. |
| K9 | `features/catalog/catalog.ts:15-22` (`CATEGORY_LABELS`/`CATEGORY_ICONS`) vs `features/search/search.ts:43-48` (`CATEGORIES`) | Mappa categoria→label/icona duplicata **e già divergente** (`Other` assente in Ricerca). |
| K14 | `operation-picker.ts:144` (`confirmNewFolder`, valida solo non-vuoto) vs `shared/validation/name.util.ts` (`validateLeafName`, usato dall'altro dialog) | `foo\bar` passa in un dialog e viene bloccato nell'altro. |

In coda, il limite noto dello step 10c: **la Dashboard non reagisce a `JobStateChanged`**
(i contatori si aggiornano al load e alla riconnessione). Chiuso C30, decidere se aggiungere
la patch realtime o lasciarlo — e **scriverlo**, in un modo o nell'altro.

## Lavoro

### 1. C17 — l'errore arriva intero a chi lo sa leggere

L'interceptor logga e mostra il toast, ma **rilancia l'errore originale**
(`HttpErrorResponse`), non un `Error` nuovo. `describe()` deve leggere la forma vera del
body del backend (`{ error: … }`), tenendo un fallback per le altre.

Verificare poi che `operation-error.ts` — oggi morto — funzioni davvero: un test che passa
un 400 del backend e si aspetta il messaggio strutturato, non la stringa `Http failure`.

### 2. C25 — un gesto dell'utente = una richiesta

Aggiungere l'endpoint **batch** di enqueue (`POST /api/operations/enqueue-batch`, simmetrico
a `preview-batch` che già esiste) e usarlo dal picker al posto del loop.

Decisioni da prendere e **motivare nel commit**:
- **atomicità**: o tutto o niente (una transazione, nessun job creato se uno fallisce) è
  ciò che rende l'errore recuperabile e il re-click innocuo. Il costo è che un batch da
  50 file con un file problematico non accoda gli altri 49: se preferisci il parziale,
  allora la risposta deve dire **quali** sono passati e quali no, e la UI deve mostrarlo —
  un parziale silenzioso è la cosa che stiamo togliendo.
- l'ordine dei job dentro il batch (`SequenceOrder`) resta assegnato dentro la transazione
  d'insert con l'indice unico (9c): non aggirarlo.
- il guard di enqueue (`PendingWorkGuard`) va interrogato per **ogni** elemento del batch,
  come oggi: un batch non è un lasciapassare.
- la risposta deve permettere alla UI di dire quante operazioni sono state accodate **in
  attesa** (`Blocked(DependencyPending)`), come già fa la schermata di conferma dopo 9c.

### 3. C27 — il dialog si apre quando ha i dati

`await` sul caricamento dei volumi prima di leggere `catalogable()`, con uno stato di
caricamento visibile (skill `impeccable`: mostrare che sta caricando, non un dialog vuoto
che sembra rotto). Se il caricamento fallisce, dirlo dentro il dialog invece di lasciare
`canSubmit` falso senza spiegazione.

### 4. C29 — il timer muore con la vista

`DestroyRef`/`OnDestroy` che azzera il timer. Se ci sono altri timer o subscription della
stessa forma nel frontend, chiuderli nello stesso commit (grep `setTimeout`/`setInterval`
su `src/frontend/src`).

### 5. C30 — la Dashboard conta i job veri

`DashboardStatsAssembler` calcola davvero `QueuedJobs`, `BlockedJobs`, `RunningJobs`,
`PendingBytes` dalla tabella `OperationJobs` (attenzione: un aggregato solo, non quattro
query — vedi anche E6 nel task 11e, che tocca lo stesso controller: se le due cose
collidono, questo task fa la correttezza e 11e l'efficienza).

Poi la decisione sul realtime: `JobStateChanged` è già instradato dal `RealtimeBridge`
(10c). Aggiungere la patch dei contatori Dashboard è economico **se** si tratta di
aggiornare numeri già in memoria; diventa caro se serve una GET per transizione. Scegli,
implementa o non implementare, e scrivi il perché.

### 6. K8, K9, K14 — la stessa verità in un posto solo

- `ACTIVE_STATES`/`TERMINAL_STATES`: un export unico tipizzato `Set<JobState>` accanto al
  tipo, importato da store **e** componente.
- Mappa categorie: una sola, con tutte le categorie (`Other` inclusa), condivisa tra
  Catalogo e Ricerca. Etichette al **plurale o singolare, una scelta sola**.
- `confirmNewFolder` usa `validateLeafName` come l'altro dialog, con lo stesso messaggio.

## Split dei commit (indicativo)

1. `feat(host): batch enqueue endpoint` — backend, con i suoi test.
2. `fix(frontend): one request per gesture in the operation picker` — C25 lato client.
3. `fix(frontend): the interceptor rethrows the HTTP error` — C17.
4. `fix(frontend): the picker opens with its data` — C27.
5. `fix(frontend): the search timer dies with the view` — C29.
6. `feat(backend+frontend): the dashboard counts real queue jobs` — C30.
7. `refactor(frontend): one source for job states, categories and leaf-name validation` — K8, K9, K14.

## Test (RED prima del GREEN)

- **Vitest**: interceptor che rilancia un `HttpErrorResponse` (RED oggi: `instanceof` falso);
  picker che con store freddo aspetta i volumi; timer di ricerca azzerato al destroy
  (RED: oggi scatta dopo il destroy); un solo POST per un batch di N item (RED: oggi N);
  set di stati e mappa categorie importati da un unico modulo (un test che rompe se
  divergono di nuovo).
- **xUnit**: endpoint batch — successo, fallimento a metà con la semantica scelta, guard
  interrogato per ogni item, `SequenceOrder` senza duplicati sotto concorrenza; contatori
  Dashboard su un DB con job in stati diversi (RED: oggi zero).

## Verifica finale

- `dotnet test` verde, `npx vitest run` verde, `ng build` ok (i **4 warning di budget SCSS
  sono pre-esistenti**: se ne compaiono altri, sono tuoi).
- Passata UI con la skill `impeccable` su ciò che è stato toccato (dialog, dashboard,
  messaggi d'errore): stessa famiglia visiva e stesso vocabolario del resto dell'app,
  niente colore da solo a portare informazione.
- **Code review finale** indipendente sulle modifiche del giro; riportare rilievi e
  correzioni.
- `CLAUDE.md`: paragrafo «Fatto nello step 11d» con la decisione presa su atomicità del
  batch e su Dashboard/realtime; in `CODE-REVIEW-HANDOFF.md` marcare C17/C25/C27/C29/C30/
  K8/K9/K14 come chiusi.
