# TASK — Step 12a: infrastruttura E2E Playwright + primi flussi

> **Sessione dedicata, agente singolo.** Primo dei due checkpoint dello step 12, l'ultimo
> dell'MVP (`CLAUDE.md` §10.12). Prerequisito: 11h mergiato, suite verde, working tree
> pulito, Host chiuso.
> Riferimenti: `CLAUDE.md` §2 (E2E: Playwright sui flussi critici), §3 (hosting: Kestrel su
> loopback, token locale), §8 (schermate), sezione «Test (non negoziabile)».
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

Oggi la piramide ha due livelli su tre: **xUnit 786** (logica + integrazione backend) e
**Vitest 244** (componenti e store), più l'harness sul ferro che esercita la coda su file
veri. Manca il livello che verifica **il prodotto assemblato**: il Host che serve la SPA,
il token che arriva davvero al browser, SignalR che passa dal socket vero, le schermate che
reagiscono.

È anche l'unico modo di chiudere una lacuna dichiarata due volte:
- lo step 10b ha scritto che **l'hub non è montato nell'harness**;
- lo step 10c ha scritto che la copertura del push è Vitest con una `HubConnection` **finta**,
  e che *«la prova end-to-end vera arriva con Playwright»*.

Questo checkpoint costruisce l'infrastruttura e copre i flussi di **avvio, volumi e
scansione**. I flussi di catalogo, ricerca, coda e realtime sono lo step 12b.

## Stato di partenza (verificato su HEAD, riverificare prima di editare)

- `tests/e2e/` **non esiste**; `CLAUDE.md` §3 lo prevede come progetto node accanto a
  `tests/FileTracert.Tests`.
- Il frontend non ha Playwright (`src/frontend/package.json`: solo `ng`, `build`, `test`).
- `src/frontend/proxy.conf.json` inoltra `/api`, `/health` e `/hubs` (con `ws: true`) a
  `http://localhost:5005` — è la strada di `ng serve`.
- `core/config/runtime-config.service.ts` risolve il token in due modi: in **produzione**
  legge `<meta name="ft-token">` che il Host timbra servendo `index.html`; in **sviluppo**
  cade su `GET /api/dev/token` attraverso il proxy.

## La decisione che struttura tutto: contro cosa gira il browser

Due configurazioni possibili, e la differenza non è di comodità:

- **`ng serve` + proxy**: veloce da montare, ma prova il percorso **dev** del token e mette
  un proxy in mezzo al WebSocket — cioè non prova ciò che l'utente esegue.
- **Host che serve la SPA buildata**: stessa origine, token dal `<meta>`, WebSocket diretto.
  È il prodotto.

**Vai sulla seconda.** Se per qualche ragione tecnica non è praticabile, motiva nel report e
usa la prima, ma dichiarando cosa resta non provato. In entrambi i casi il Host va avviato
dai test (webServer di Playwright o gestione esplicita), con:
- un **database temporaneo** (`FileTracert:DatabasePath` su una cartella usa-e-getta) —
  mai il DB reale dell'utente in `%LOCALAPPDATA%\FileTracert`;
- una **porta dedicata**, diversa dalla 5005 di sviluppo, così una sessione E2E non
  colonizza l'istanza che l'utente sta usando;
- i worker che servono e nient'altro (esistono già gli interruttori usati dai test di
  integrazione: guardali prima di inventarne altri).

## Dati: da dove vengono i volumi

Il Host in E2E parla con la piattaforma **vera** (`IVolumeProbe`, enumerazione): i volumi
sono quelli della macchina. Per rendere i test deterministici serve un perimetro nostro:
una cartella scratch (es. sotto `%TEMP%`) popolata dal test con una struttura nota e
registrata come **watched root**, con gli altri volumi lasciati non catalogabili.

Attenzione: **non elevato** significa niente USN → motore a enumerazione. Va bene, ma va
scritto: è la stessa scelta già fatta dall'harness, e il commento evita che qualcuno legga
un PASS come prova del percorso USN.

**Vincolo assoluto**: nessun test E2E tocca file dell'utente fuori dalla propria sandbox, e
la sandbox si crea e si distrugge dentro il test. Vale anche per il cestino.

## Lavoro

### 1. Il progetto `tests/e2e`

Playwright + TypeScript, `npm` proprio (non dentro `src/frontend`: è un progetto di test,
non una dipendenza dell'app). Config con un solo browser per l'MVP (Chromium), reporter
leggibile, **retry a zero in locale**: un retry nasconde esattamente la classe di difetti
che questo livello dovrebbe trovare. Traccia/screenshot **on-failure**.

Script di lancio documentati nel README del progetto: come si esegue, cosa serve
(niente elevazione), quanto ci mette, e che **non gira in CI** (richiede Windows e un
filesystem vero) — stessa politica dell'harness.

### 2. Il ciclo di vita: Host + SPA

Build del frontend, avvio del Host sulla porta dedicata con il DB temporaneo, attesa che
`/health` risponda, esecuzione, spegnimento pulito. Lo spegnimento è parte del test: il
processo deve chiudersi (11c ha lavorato proprio su questo) e i file temporanei vanno via.

### 3. Aiutanti, non copia-incolla

Un piccolo strato di helper: creazione della sandbox e della sua struttura, registrazione
del watched root via API (più veloce e più stabile che pilotare il wizard a ogni test),
attesa di condizioni **osservabili** (una scansione finita si vede dalla UI, non da un
`sleep`). Page-object o funzioni: scegli, ma **niente `waitForTimeout` come strumento di
sincronizzazione** — se serve, è il segnale che manca un'asserzione web-first.

### 4. I flussi coperti in questo checkpoint

1. **Avvio e autenticazione**: l'app si carica, il token arriva dal `<meta>`, le chiamate
   API rispondono 200. Contro-prova che vale doppio: **senza** token il Host risponde 401 —
   verifica che il meccanismo protegga davvero, non che sia semplicemente assente.
2. **Dashboard**: le card mostrano numeri coerenti con il DB appena seminato (volumi,
   file, byte, contatori coda a zero su un DB pulito — che dopo 11d sono reali).
3. **Volumi**: la lista mostra i volumi della macchina; il dettaglio del volume di test
   mostra GUID, filesystem, root sorvegliati; si può **aggiungere un watched root** e
   cambiare i filtri dalla UI.
4. **Scansione**: si lancia dalla UI, l'avanzamento si vede (`ScanProgress` arriva davvero
   dall'hub, non da un poll: 10c ha tolto i timer), e al termine i contatori del volume
   riflettono i file della sandbox.

Per ciascuno: asserzioni su ciò che **l'utente vede**, non su risposte HTTP intercettate.
La risposta HTTP è già coperta da xUnit; qui il punto è che la schermata la mostri.

## Split dei commit (indicativo)

1. `test(e2e): the Playwright project and its README`.
2. `test(e2e): boot the real Host over a throwaway database`.
3. `test(e2e): sandbox helpers and web-first waiting`.
4. `test(e2e): startup, auth and dashboard`.
5. `test(e2e): volumes, watched roots and a real scan`.

## Verifica

- I test E2E passano **tre volte di fila** (un E2E che passa una volta non è verde, è
  fortunato). Riportare la durata di una passata.
- Almeno un **RED dimostrato**: rompi qualcosa nel prodotto (togli il timbro del token nel
  `<meta>`, o il refresh dei contatori dopo lo scan) e mostra che il test cade. Un E2E che
  non è mai stato rosso non prova nulla.
- xUnit e Vitest restano verdi; build backend pulita; `ng build` ok (4 warning SCSS
  pre-esistenti).
- Nessun file dell'utente toccato: dichiaralo, e dì come lo hai verificato.

## Definition of done

- `tests/e2e` esiste, si esegue con un comando, e il README dice come.
- I quattro flussi sopra sono coperti e verdi ×3.
- **Code review finale** indipendente: nessuna sincronizzazione a tempo, nessuna asserzione
  tautologica (un test che verifica il proprio mock non è E2E), sandbox davvero isolata,
  spegnimento pulito, niente segreti o path della macchina committati.
- `CLAUDE.md`: paragrafo «Fatto nello step 12a» + §2/§3 aggiornati se la posizione o il
  comando di lancio differiscono da quanto scritto lì.
