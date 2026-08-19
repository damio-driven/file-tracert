# FileTracert — test end-to-end (Playwright)

Il terzo livello della piramide: sotto ci sono xUnit (logica e integrazione backend) e Vitest
(componenti e store), più l'harness che esercita la coda su file veri. Qui si verifica il
**prodotto assemblato** — il Host che serve la SPA compilata, il token che arriva davvero al
browser, SignalR sul socket vero, le schermate che reagiscono.

## Come si esegue

```powershell
cd tests\e2e
npm ci                            # una volta
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
| `npm run typecheck` | `tsc --noEmit` (Playwright transpila senza controllare i tipi) |
| `$env:FT_E2E_SKIP_BUILD='1'; npm test` | salta la build — controlla solo che gli artefatti **esistano**, non che siano aggiornati |

## Cosa serve

- **Windows.** I test parlano con la piattaforma vera: volumi, filesystem, console control event
  per lo spegnimento. Non girano altrove.
- **Nessuna elevazione — obbligatorio, e il `globalSetup` si rifiuta di partire da elevato.**
  Il motore di scansione si sceglie dal **filesystem**, non da un'impostazione: su NTFS la
  scansione *prova sempre* il giornale USN. Non elevati, Windows rifiuta e il prodotto ripiega
  sull'**enumerazione della sola cartella monitorata** — l'unico percorso che resta dentro la
  sandbox. Elevati, `EnsureJournal` **creerebbe** un giornale sul volume di sistema (una modifica
  persistente fuori dalla sandbox, fatta da un test) e la lettura snapshot camminerebbe l'intera
  MFT del disco dello sviluppatore. Perciò: un PASS qui **non** è una prova del percorso USN — è
  la stessa scelta già fatta dall'harness — e da elevato la suite non parte affatto.
- **Il Host chiuso.** La build del backend fallisce su una DLL bloccata se un Host di sviluppo sta
  girando da questa copia di lavoro.
- **Niente CI.** Come l'harness: richiede Windows e un filesystem vero.

## Che cosa tocca sul disco

Solo `tests/e2e/.artifacts/`, che viene svuotata all'inizio di ogni passata e ripulita test per
test. Dentro ci finiscono il database usa-e-getta di ogni Host, il suo log, e la cartella-sandbox
che i test popolano e scansionano.

Le garanzie, in ordine di importanza:

- **Mai il database reale.** Ogni Host parte con `FileTracert:DatabasePath` dentro `.artifacts`;
  `%LOCALAPPDATA%\FileTracert` non viene mai aperto. Non è solo una configurazione: appena il Host
  risponde, il test **verifica che quel file esista davvero**, così un binding rotto diventa un
  errore con un nome invece di una migrazione silenziosa del catalogo dell'utente.
- **Mai la porta 5005.** Gli Host dei test prendono la prima porta libera nell'intervallo
  5180–5279, così una sessione end-to-end non parla con l'istanza che stai usando.
- **Mai un file fuori dalla sandbox.** `Sandbox.dispose()` si rifiuta di cancellare qualcosa che
  non stia sotto `.artifacts`, e dallo step 12b — quando i test hanno cominciato ad **accodare
  operazioni vere** — il recinto qui sotto decide cosa il prodotto ha il permesso di toccare.
- **Il perimetro del catalogo è la sandbox.** Il volume su cui sta viene reso catalogabile e la
  cartella-sandbox diventa la sua unica cartella monitorata. Gli altri volumi della macchina
  restano come li ha classificati il prodotto: senza cartelle monitorate non vengono mai
  scansionati.

### Il recinto (`src/fence.ts`)

Dallo step 12b i test **accodano operazioni** che il vero `JobExecutionEngine` esegue su un volume
che la suite rende catalogabile — su una macchina di sviluppo, il **disco di sistema**. Una
destinazione sbagliata non è più un'asserzione rossa: è un file dell'utente che si sposta. Quindi il
contenimento si **verifica**, non si organizza. Tre strati, uno per ogni modo di uscire:

1. **Perimetro** (`assertPerimeter`, chiamato da `watchSandbox`) — l'indice è confinato alla
   sandbox: è l'unica cartella monitorata della macchina, quindi **nessun id sorgente può nominare
   un file fuori**. Verificato sulla risposta del servizio, non dedotto dal fatto che il test ne ha
   registrata una sola.
2. **Destinazione** (`violationOf`, prima di ogni enqueue) — volume e path di destinazione devono
   stare dentro la sandbox. Vale per le richieste che partono dal test (`Api.enqueue` **pretende** il
   fence come parametro) e per quelle che partono dalla **SPA** quando uno spec guida il picker: il
   contesto del browser le intercetta e le rifiuta prima che raggiungano il Host, cioè prima che
   l'engine possa agire. Una richiesta pulita passa **intatta**: non si finge nulla.
3. **Audit** (`auditRecordedJobs`, in teardown mentre il Host risponde ancora) — ogni job che il
   servizio ha **registrato** viene riletto e controllato: dentro la sandbox, sul volume della
   sandbox, intra-volume. Cross-volume è una violazione di per sé: è l'unico percorso che manda un
   file nel **cestino**, e questa suite non deve metterci mai niente.

I primi due strati sono la promessa («prima dell'esecuzione»), il terzo è la prova. Ognuno fallisce
**rumorosamente**, col path incriminato, e nessuno corregge niente in silenzio.
`specs/sandbox-fence.spec.ts` prova a uscire da tutti e tre.

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
  condizione dell'API. `waitForTimeout` non compare da nessuna parte. Gli stati **transitori** (la
  barra di avanzamento della scansione) non si aspettano affatto: un `MutationObserver` installato
  *prima* dell'azione ne registra la comparsa, così l'asserzione non corre contro la fine del
  lavoro che ha appena avviato.
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

## I flussi coperti (step 12b)

| file | cosa verifica |
| --- | --- |
| `specs/sandbox-fence.spec.ts` | i tre strati del recinto rifiutano un'operazione diretta fuori dalla sandbox — dal test, dal browser, e nell'audit finale |
| `specs/catalog.spec.ts` | l'albero lazy si naviga livello per livello e conta ciò che il test ha messo su disco; una cartella esclusa dal filtro **resta** nell'albero (11h) |
| `specs/search.spec.ts` | l'FTS trova per nome, «solo nome» e «percorso completo» danno risposte diverse, il filtro categoria stringe, e il filtro data ragiona in **giorni locali** |
| `specs/projection.spec.ts` | accodare uno spostamento dal picker muove **subito** il file nel Catalogo e nella Ricerca, col badge che linka la riga della Coda; a operazione eseguita il badge sparisce e il **disco** è dove la proiezione diceva |
