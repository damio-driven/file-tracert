# FileTracert — test end-to-end (Playwright)

Il terzo livello della piramide: sotto ci sono xUnit (logica e integrazione backend) e Vitest
(componenti e store), più l'harness che esercita la coda su file veri. Qui si verifica il
**prodotto assemblato** — il Host che serve la SPA compilata, il token che arriva davvero al
browser, SignalR sul socket vero, le schermate che reagiscono.

## Come si esegue

```powershell
cd tests\e2e
npm install                 # una volta
npx playwright install chromium   # una volta: scarica il browser
npm test
```

`npm test` fa tutto: compila il backend e il frontend, poi avvia un Host per ogni test.
Una passata completa dura **circa 2 minuti**.

Varianti utili:

| comando | cosa fa |
| --- | --- |
| `npm test -- specs/scan.spec.ts` | un solo file |
| `npm run test:headed` | col browser a schermo |
| `npm run report` | apre il report HTML dell'ultima passata |
| `$env:FT_E2E_SKIP_BUILD='1'; npm test` | salta la build (se hai appena compilato a mano) |

## Cosa serve

- **Windows.** I test parlano con la piattaforma vera: volumi, filesystem, console control event
  per lo spegnimento. Non girano altrove.
- **Nessuna elevazione.** Senza privilegi di amministratore il giornale USN non è accessibile e la
  scansione ripiega sull'**enumerazione** (il prodotto lo fa apposta, con una notifica). Un PASS
  qui non è quindi una prova del percorso USN — è la stessa scelta già fatta dall'harness.
- **Il Host chiuso.** La build del backend fallisce su una DLL bloccata se un Host di sviluppo sta
  girando da questa copia di lavoro.
- **Niente CI.** Come l'harness: richiede Windows e un filesystem vero.

## Che cosa tocca sul disco

Solo `tests/e2e/.artifacts/`, che viene svuotata all'inizio di ogni passata e ripulita test per
test. Dentro ci finiscono il database usa-e-getta di ogni Host, il suo log, e la cartella-sandbox
che i test popolano e scansionano.

Le garanzie, in ordine di importanza:

- **Mai il database reale.** Ogni Host parte con `FileTracert:DatabasePath` dentro `.artifacts`;
  `%LOCALAPPDATA%\FileTracert` non viene mai aperto.
- **Mai la porta 5005.** Gli Host dei test prendono la prima porta libera nell'intervallo
  5180–5279, così una sessione end-to-end non parla con l'istanza che stai usando.
- **Mai un file fuori dalla sandbox.** I flussi coperti da questo checkpoint *leggono* il disco
  (una scansione) e scrivono solo dentro `.artifacts`; `Sandbox.dispose()` si rifiuta di cancellare
  qualcosa che non stia lì sotto. Nessun file dell'utente, e nessun passaggio dal cestino.
- **Il perimetro del catalogo è la sandbox.** Il volume su cui sta viene reso catalogabile e la
  cartella-sandbox diventa la sua unica cartella monitorata. Gli altri volumi della macchina
  restano come li ha classificati il prodotto: senza cartelle monitorate non vengono mai
  scansionati.

## Come è montato

- **Il browser parla con il Host, non con `ng serve`.** Stessa origine, token letto dal
  `<meta name="ft-token">` che il Host timbra su `index.html`, WebSocket diretto verso
  `/hubs/events`. È il prodotto: la strada `ng serve` + proxy proverebbe il percorso *dev* del
  token e metterebbe un proxy in mezzo al socket.
- **Un Host per test**, su un database vuoto. Costa ~4 s a test e in cambio nessun test dipende da
  quello prima: i contatori della Dashboard sono globali, e condividere un database vorrebbe dire
  scrivere asserzioni che valgono solo in un certo ordine.
- **Zero retry** (`playwright.config.ts`). Un retry nasconde esattamente la classe di difetti che
  questo livello esiste per trovare.
- **Nessuna attesa a tempo.** Le attese sono asserzioni web-first sulla UI, o `expect.poll` su una
  condizione dell'API. `waitForTimeout` non compare da nessuna parte.
- **Niente rete.** Ogni richiesta che non sia diretta al proprio Host viene rifiutata dal
  contesto del browser (`index.html` linka i font di Google): un prodotto loopback non deve
  dipendere dalla rete nemmeno per essere provato.

### Avvio e spegnimento del Host

`scripts/start-host.ps1` e `scripts/stop-host.ps1` esistono per due ragioni concrete, entrambe
scritte accanto al codice:

- Node non può avviarlo direttamente: `spawn({ detached: true })` su Windows lascia il processo
  **senza console**, e un figlio non-detached **condivide** la console del runner — un Ctrl+C
  indirizzato al Host colpirebbe anche Playwright. `Start-Process` gli dà una console propria.
- Lo spegnimento è **CTRL_C_EVENT**, non `taskkill`: `taskkill` senza `/F` non ferma un'app
  console senza finestra, e con `/F` è `TerminateProcess` — i worker non eseguono la sequenza di
  stop e il test non proverebbe nulla del lavoro fatto allo step 11c. Il test **fallisce** se il
  Host non si spegne nel budget.

## I flussi coperti (step 12a)

| file | cosa verifica |
| --- | --- |
| `specs/startup-auth.spec.ts` | la shell si carica e mostra il servizio attivo; il token è nel `<meta>` timbrato dal Host; senza token (o con token sbagliato) API e hub rispondono 401 |
| `specs/dashboard.spec.ts` | su catalogo vuoto le card dicono zero; dopo una scansione contano esattamente i file della sandbox, byte compresi |
| `specs/volumes.spec.ts` | il dettaglio mostra GUID, filesystem e cartelle monitorate; si aggiunge una cartella monitorata dall'albero reale del Setup; si cambia il filtro e l'indice si riallinea |
| `specs/scan.spec.ts` | la scansione parte dalla UI, l'avanzamento compare e sparisce perché l'hub lo spinge, e i contatori finiscono allineati alla sandbox |

Catalogo, ricerca, coda e realtime sono lo **step 12b**.
