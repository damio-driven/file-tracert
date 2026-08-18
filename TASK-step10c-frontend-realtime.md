# TASK — Step 10c: Il frontend ascolta invece di pollare

> **Sessione dedicata, agente singolo.** Terzo e ultimo checkpoint dello step 10.
> **Prerequisito: 10b mergiato** (`TASK-step10b-signalr-hub.md`) — l'hub deve esistere e
> pubblicare davvero. Suite verde, working tree pulito.
> Riferimenti: `CLAUDE.md` §7 (messaggi hub), §8 (architettura frontend: standalone,
> zoneless, OnPush + signals, `@ngrx/signals`), §5 (proiezione), §9.
> ⚠️ **Lavoro UI: usare la skill `impeccable`** (§2/§8) per qualunque cosa si veda a video.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

Il frontend oggi tiene viva la UI con i timer:

- `src/frontend/src/app/features/queue/queue.ts` — `setInterval(…, 2500)` in `ngOnInit`,
  ricarica la lista job finché ci sono job attivi;
- `src/frontend/src/app/app.ts` — `setInterval(…, NOTIFICATION_POLL_MS)` per il contatore
  notifiche;
- `src/frontend/src/app/core/realtime/realtime.service.ts` — **placeholder inerte**, con
  scritto in commento che verrà cablato allo step 10.

Il pacchetto `@microsoft/signalr` è già in `package.json` (`^10.0.0`): manca solo il
codice. Dopo 10b il server pubblica; qui il client smette di chiedere.

## Lavoro

### 1. `RealtimeService` vero (`core/realtime/`)

- `HubConnectionBuilder` su `/hubs/events`, token dalla `RuntimeConfigService`
  (`core/config/runtime-config.service.ts`, che lo legge dal `<meta name="ft-token">` in
  produzione o dall'endpoint dev). Il token va **in query string** (`?access_token=…`): è
  il contratto che 10b ha implementato, il WebSocket del browser non manda header custom.
- `withAutomaticReconnect`, avvio **dopo** l'app initializer che risolve il token (oggi
  `RuntimeConfigService.load()` gira lì: non partire prima o la connessione nasce 401).
- Espone lo **stato della connessione** come signal (`connected | reconnecting | offline`).
- Handler tipizzati per i messaggi di §7 + `NotificationRaised`. I tipi TS delle union
  (`JobState`, `JobBlockReason`, `EntityPendingState`) esistono già in
  `core/models/catalog.models.ts`: **riusali**, non ridichiararli (K8 della review dice
  esattamente questo su costanti duplicate).

### 2. Gli store reagiscono, i componenti no

Gli eventi patchano gli stessi SignalStore che le schermate già leggono (§8): nessun
componente deve sapere che esiste SignalR.

- `features/queue/queue.store.ts` — `JobProgress` aggiorna byte/percentuale della riga;
  `JobStateChanged` aggiorna stato/blockReason/errore. **Patch mirata sulla riga**, non
  ricarica dell'intera lista a ogni messaggio.
- store volumi — `VolumeStatusChanged` (online/offline, free bytes, lastSeen).
- store scansioni — `ScanProgress`.
- notifiche — `NotificationRaised` incrementa/aggiorna il contatore.
- Catalogo e Ricerca — `ProjectionChanged` invalida la vista corrente (i badge di
  proiezione di 9b devono cambiare da soli quando un job parte o finisce).

### 3. Spegnere i poll

Rimuovi i due `setInterval` (queue e notifiche). **Non lasciarli come fallback silenzioso**:
o c'è il push, o si vede che non c'è.

Degradazione esplicita, decisione tua ma va presa e commentata:
- alla **riconnessione** fai un refresh completo degli store visibili (i messaggi persi
  durante il buco non tornano indietro: senza questo la UI resta indietro per sempre);
- se la connessione resta giù, mostralo (punto 4) — eventualmente riattivando il polling
  come modalità degradata dichiarata, non nascosta.

### 4. Indicatore di connessione (skill `impeccable`)

Un segnale piccolo e onesto nella shell: connesso (discreto o invisibile), in
riconnessione, disconnesso. Design system esistente (`styles/_tokens.scss`, `.ft-pill`,
famiglia colori di stato), tema dark, niente framework CSS. Non deve gridare quando tutto
va bene, deve essere chiaro quando qualcosa non va.

## Test (non negoziabile)

- **RED prima del GREEN** (Vitest, §2).
- `RealtimeService`: con una fake `HubConnection` — costruisce l'url col token, registra
  gli handler, espone lo stato giusto su `onreconnecting`/`onreconnected`/`onclose`, e sul
  **reconnected** scatena il refresh.
- Store: un `JobProgress` patcha **solo** quella riga; un `JobStateChanged` su un job
  sconosciuto non rompe nulla (arriva prima della lista); `ProjectionChanged` invalida.
- Componenti: la Coda **non** crea più timer (`ngOnInit` non lascia interval attivi) e si
  aggiorna quando lo store cambia.
- E2E Playwright: **non qui** — è lo step 12. Non aprire quel cantiere.

## Criteri di accettazione

- [ ] File:riga riverificati su HEAD prima di editare.
- [ ] Nessun `setInterval` residuo per coda e notifiche (`grep setInterval src/app` lo prova).
- [ ] Connessione autenticata; nessuna chiamata prima che il token sia risolto.
- [ ] Riconnessione → refresh degli store visibili; stato connessione visibile in UI.
- [ ] Patch mirate: un `JobProgress` non ricarica la lista.
- [ ] Union type riusate da `catalog.models.ts`, zero duplicazione di costanti di stato.
- [ ] Vitest verde; `ng build` ok (restano i 4 warning di budget SCSS, pre-esistenti — non
      sono una regressione, non inseguirli).
- [ ] Backend intatto: suite xUnit ancora verde.

## Commit suggeriti

1. `feat(frontend): real SignalR client with token auth and reconnect`
2. `feat(frontend): stores patched by realtime events`
3. `refactor(frontend): drop the queue and notification polling timers`
4. `feat(frontend): connection state indicator in the shell`
5. `test(frontend): realtime service and store patching`

## Code review finale (obbligatoria)

Correttezza vs criteri e scenari di fallimento; nessun poll dimenticato; nessuna
duplicazione di tipi/costanti; gestione onesta della disconnessione (niente stato stantio
mostrato come fresco); test reali RED→GREEN; conformità §8 (OnPush, signals, zoneless,
niente NgModule). Riportare cosa ha trovato e cosa è stato corretto (o perché un rilievo è
stato lasciato consapevolmente).

## Regole operative

- **Usare la skill `impeccable`** per la parte visibile.
- Si committa su `develop`, niente branch nuovi.
- A fine task aggiorna `CLAUDE.md` (sezione «Fatto nello step 10c…», roadmap: **step 10
  chiuso**, prossimo = WP minori rimanenti, poi step 12) e **cancella questo file**.
