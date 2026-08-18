# TASK — Step 10b: Hub SignalR + messaggi tipizzati (backend)

> **Sessione dedicata, agente singolo.** Secondo dei tre checkpoint dello step 10.
> **Prerequisito: 10a mergiato** (`TASK-step10a-device-watcher.md`), suite verde, working
> tree pulito, Host chiuso.
> Riferimenti: `CLAUDE.md` §3 (layering + shared kernel), §7 (**SignalR hub — messaggi
> tipizzati**), §5 (proiezione), §9 (no silent catch).
> ⚠️ Tocca i **file caldi della coda** (`JobExecutionEngine`, `QueueService`): agente
> unico, niente parallelo su questi file.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

`CLAUDE.md` §7 elenca l'hub e i suoi messaggi come parte dell'API surface, ma nel backend
**non esiste nulla**: nessun hub, nessun `IHubContext`, nessun contratto di messaggio
(grep `Hub` su `src/backend` trova solo commenti). Il frontend ha già il pacchetto
`@microsoft/signalr` in `package.json` e un `core/realtime/realtime.service.ts` che è un
placeholder dichiarato *«wired at step 10»*.

Conseguenza attuale: ogni schermata che deve restare viva **polla**. La Coda ricarica ogni
2,5 s (`src/frontend/src/app/features/queue/queue.ts`), le notifiche hanno un loro
`setInterval` (`src/frontend/src/app/app.ts`), lo stato di scansione si legge solo con
`GET /api/scans/status`.

10b costruisce **il lato server**: i contratti dei messaggi, l'hub, l'autenticazione e i
punti di emissione. Il frontend continua a pollare fino a 10c — questo checkpoint si
chiude verde senza toccare Angular.

## Vincolo di layering (§3) — la regola che decide il design

`Business` dipende **solo** da `Contracts` + `Data`. SignalR è una dipendenza di `Host`.
Quindi:

- in `Contracts` va una **port interface** (es. `Realtime/IRealtimePublisher`) più i
  **record dei messaggi**;
- `Business` (engine, queue service, scan tracker, notification service) pubblica
  attraverso la port;
- `Host` implementa la port con `IHubContext<…>` e la registra in DI.

Nessun `Microsoft.AspNetCore.SignalR` nel csproj di `Business`, `Data` o `Platform`: se ce
lo trovi, il design è sbagliato.

## Messaggi (da §7, più uno)

I cinque del brief:

| Messaggio | Quando | Payload minimo |
|---|---|---|
| `VolumeStatusChanged` | volume online/offline, free bytes rinfrescati | volumeId, isOnline, freeBytesLastKnown, lastSeenUtc |
| `JobProgress` | avanzamento byte di un job in copia | jobId, bytesProcessed, totalBytes |
| `JobStateChanged` | ogni transizione di stato, enqueue, cancel, retry, block | jobId, state, blockReason, errorMessage |
| `ScanProgress` | avanzamento scansione volume | il DTO esistente `ScanStatusDto` |
| `ProjectionChanged` | l'overlay `Pending*` è cambiato (§5) | volumeId (o null = globale), jobId |

Più **`NotificationRaised`** (id, severity, title, timestampUtc): la roadmap dello step 10
dice esplicitamente «progress/**notifiche**/coda in tempo reale», ed è ciò che permette a
10c di spegnere il poll delle notifiche. È un'aggiunta rispetto alla lista di §7: **va
documentata in `CLAUDE.md` §7** a fine task.

Regole sui payload:
- **Piccoli**: id + i campi che cambiano. Chi vuole il resto rifà la GET. Non spedire
  `OperationJobDto` interi a ogni tick.
- Enum serializzati **come stringhe**, coerenti con `Program.cs` (`JsonStringEnumConverter`)
  e con i tipi TS che 10c dovrà scrivere.
- Date **UTC** (§6).

## Lavoro

### 1. Contratti in `Contracts/Realtime/`

`IRealtimePublisher` con un metodo per messaggio (tipizzato, non `object`), più i record.
La port deve essere `Task`-based e accettare un `CancellationToken`.

### 2. Hub in `Host/Realtime/`

Un solo hub (es. `FileTracertHub`), mappato su `/hubs/events`. Non serve nessun metodo
client→server per l'MVP: il flusso è unidirezionale server→client. Se aggiungi un
`Subscribe`/gruppo per volume, motivalo — altrimenti tieni broadcast, siamo single-user.

`Program.cs`: `AddSignalR()`, `MapHub<FileTracertHub>("/hubs/events")`, registrazione della
implementazione della port.

### 3. Autenticazione dell'hub (attenzione)

`TokenAuthMiddleware` (`Host/Infrastructure/TokenAuthMiddleware.cs`) oggi protegge `/api/*`
e `/health` leggendo l'header `X-FileTracert-Token`. **Il transport WebSocket del browser
non può impostare header custom**: il client SignalR passa il token in query string
(`?access_token=…`).

Quindi:
- estendi il middleware a `/hubs/*`, accettando **in alternativa** il token in query string
  **solo** su quel path;
- mantieni il confronto **fixed-time** (`CryptographicOperations.FixedTimeEquals`) già usato;
- senza token o con token sbagliato: **401**, come per `/api`.

Nota di sicurezza da scrivere nel commento: il token in query string è meno riservato di un
header (finisce in log e telemetria HTTP). Qui è accettabile perché il binding è
`127.0.0.1` e il token è un segreto locale monoutente (§3 «Security locale»), ma va detto,
non subito di nascosto. Verifica che i nostri log non stampino la query string a livello
default (`LogCategoryPolicy` tiene le categorie framework a Warning: confermalo con un test
o con una prova, non a memoria).

### 4. Punti di emissione

Aggancia la port dove lo stato cambia davvero, **senza** duplicare logica:

- `Business/Operations/JobExecutionEngine.cs` — `JobStateChanged` su ogni transizione
  persistita (c'è già un punto unico, `TransitionAsync`, più i terminali
  `CompleteJobAsync` / `SetFailedAsync` / `SetBlockedAsync`); `JobProgress` durante la
  copia, **con la stessa cadenza già usata per il salvataggio del progresso**
  (`ProgressSaveInterval`, 1/sec): non emettere per ogni buffer da 80 KB.
- `Business/Operations/QueueService.cs` — enqueue, cancel, retry → `JobStateChanged`;
  enqueue/cancel/terminali → `ProjectionChanged` (l'overlay è cambiato, §5).
- `Business/Projection/OverlayWriter` — se preferisci emettere `ProjectionChanged` da qui
  (unico punto che scrive gli overlay) invece che dal QueueService, va bene: **scegli uno
  solo dei due**, non entrambi.
- `Business/Volumes/VolumeSyncService.cs` (o il ciclo condiviso introdotto da 10a) →
  `VolumeStatusChanged`.
- l'implementazione di `IScanStatusTracker` → `ScanProgress` (throttle: la scansione
  aggiorna i contatori molto spesso, non trasformarlo in un firehose).
- `Business/Notifications/NotificationService` → `NotificationRaised`.

### 5. Un hub rotto non deve rompere un job (§9)

La pubblicazione è **best-effort**: un'eccezione del transport non deve mai far fallire una
transizione di stato o una copia. Quindi il publisher cattura, **logga l'eccezione
completa** e prosegue. Non è un catch silenzioso: è resilienza dichiarata, con log. Non
serve una Notification per questo (l'utente vedrebbe rumore per un problema che si risolve
alla riconnessione).

Attenzione all'ordine: pubblica **dopo** il commit, mai dentro la transazione. Un
messaggio che annuncia uno stato che poi rollbacka è peggio di un messaggio in ritardo.

## Test (non negoziabile)

- **RED prima del GREEN**, contro l'implementazione reale.
- **Host, integrazione** (`tests/FileTracert.Tests/Host/`, riusa `FileTracertAppFactory`):
  client SignalR **vero** (`HubConnectionBuilder`) sul `TestServer` →
  - connessione **senza** token → rifiutata (401);
  - connessione **con** token in query string → stabilita;
  - un job che cambia stato via API produce `JobStateChanged` con jobId e stato attesi;
  - una notifica pubblicata produce `NotificationRaised`.
- **Business, unit**: gli emettitori chiamano la port con il payload giusto, e
  un publisher che lancia **non** fa fallire l'operazione (fake che throwa → il job arriva
  comunque a Completed).
- **Layering**: un test o un check che fallisce se `Business`/`Data`/`Platform`
  referenziano SignalR (basta anche un'asserzione sugli assembly referenziati).
- **Harness**: nessuno scenario nuovo obbligatorio (l'harness non monta l'hub), ma la suite
  esistente **deve restare verde**: se un emettitore rompe la coda, lo vedi lì.

## Criteri di accettazione

- [ ] File:riga riverificati su HEAD prima di editare.
- [ ] Port + record in `Contracts`, hub e implementazione **solo** in `Host`; nessun
      riferimento SignalR sotto Host (§3).
- [ ] I 5 messaggi di §7 + `NotificationRaised`, enum come stringhe, date UTC, payload snelli.
- [ ] `/hubs/*` protetto dal token (header **o** query string), 401 senza; confronto fixed-time.
- [ ] `JobProgress` throttlato alla cadenza esistente, `ScanProgress` throttlato.
- [ ] Pubblicazione **dopo** il commit, mai dentro la transazione.
- [ ] Un publisher che lancia non ribalta né blocca alcun job; eccezione loggata per intero.
- [ ] Suite xUnit verde, build backend pulita (warnings-as-errors), harness ancora verde.
- [ ] `CLAUDE.md` §7 aggiornato con `NotificationRaised` e con il path dell'hub.

## Commit suggeriti

1. `feat(contracts): typed realtime messages + IRealtimePublisher port`
2. `feat(host): SignalR hub on /hubs/events, token-guarded`
3. `feat(business): publish job state and progress`
4. `feat(business): publish volume, scan, projection and notification events`
5. `test(host): a real SignalR client over TestServer, auth included`
6. `docs: record the hub contract in CLAUDE.md §7`

## Code review finale (obbligatoria)

Correttezza vs criteri e scenari di fallimento; **layering** (§3) verificato sui csproj;
no silent catch (§9) — il catch del publisher logga tutto; nessuna duplicazione dei punti
di emissione (un evento, un punto); nessuna pubblicazione dentro una transazione; test
reali RED→GREEN. Riportare cosa ha trovato e cosa è stato corretto (o perché un rilievo è
stato lasciato consapevolmente).

## Regole operative

- **Niente parallelo sui file caldi della coda** (`JobExecutionEngine`, `SpaceLedger`,
  `QueueService`, `QueueProcessorWorker`): agente unico, in sequenza.
- **Host chiuso prima di ricompilare**.
- Si committa su `develop`, niente branch nuovi.
- A fine task aggiorna `CLAUDE.md` (sezione «Fatto nello step 10b…» + roadmap) e
  **cancella questo file**.
