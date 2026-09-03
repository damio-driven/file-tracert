# FileTracert — Hardware harness

Un runner **opt-in** che esercita la coda operazioni di FileTracert **su drive veri**.
Non è `dotnet test`, **non gira mai in CI**: tocca file reali, li sposta e li manda nel
cestino. Serve per il collaudo di livello 2 (ferro), accanto ai test logici (`dotnet test`,
livello 1) e ai futuri e2e Playwright (livello 3, step 12).

Ogni scenario è **arrange → act → assert**:

- **arrange** — l'harness genera lui stesso una struttura nota di file e cartelle
  (inclusi file che il filtro *esclude*, sottoalberi profondi, file grandi) e la indicizza
  in un database usa-e-getta, passando dal port `IDirectoryEnumerator` e dal `FileFilter`
  reali: "file escluso" significa escluso dal filtro del prodotto, non da un flag inventato;
- **act** — l'operazione passa dal vero `IQueueService` (enqueue) e dal vero
  `QueueProcessorWorker`, che a sua volta usa il vero `JobExecutionEngine`, il vero
  `SpaceLedger` e il vero `IFileMover`. L'harness **non** riscrive nessuna di queste parti:
  osserva soltanto;
- **assert** — verifica il **filesystem** (cosa esiste dove, cosa è sparito, nessun
  `.fadit-partial` residuo, contenuto confrontato via SHA-256) **e il database**
  (righe `Files` / `Directories` / `OperationJob` coerenti, indice FTS aggiornato).

---

## Configurazione

`appsettings.json`, sezione `HardwareSmoke`:

```json
{
  "HardwareSmoke": {
    "Enabled": false,
    "TestVolumes": [
      { "Name": "internal-a",   "Path": "D:\\Collaudo\\A", "Kind": "Internal" },
      { "Name": "internal-b",   "Path": "E:\\Collaudo\\B", "Kind": "Internal" },
      { "Name": "external-usb", "Path": "K:\\Collaudo",    "Kind": "External" }
    ],
    "ScratchSubfolder": "FileTracertHarness",
    "Scenarios": [ "*" ],
    "SemiAutomatic": false,
    "MainDatabasePath": "",
    "LargeFileMegabytes": 96,
    "ScenarioTimeoutSeconds": 180
  }
}
```

| Campo | Significato |
|---|---|
| `Enabled` | Interruttore generale. `false` (default) → il runner non fa nulla. |
| `TestVolumes` | Le cartelle sacrificabili, **una per drive fisico** se possibile. `Path` deve **esistere già**. |
| `Kind` | `Internal` / `External`. Serve solo per il report e per abilitare lo scenario di scollegamento fisico. |
| `ScratchSubfolder` | Nome di **una sola cartella** creata dentro ogni `Path`. È l'unica cosa che l'harness crea e l'unica che il cleanup rimuove. |
| `Scenarios` | `["*"]` o lista vuota = tutti; altrimenti i nomi degli scenari da eseguire. Un nome sconosciuto viene segnalato nel report. |
| `SemiAutomatic` | `true` abilita lo scenario che chiede all'operatore di staccare e ricollegare fisicamente il drive. |
| `MainDatabasePath` | Override del DB di produzione da cui il guard legge le `WatchedRoot`. Vuoto = `%LOCALAPPDATA%\FileTracert\filetracert.db`. |
| `LargeFileMegabytes` | Dimensione del file usato dagli scenari a tempo (cancel, crash/resume): deve essere abbastanza grande da poter interrompere la copia. |
| `ScenarioTimeoutSeconds` | Quanto attendere un job prima di dichiarare lo scenario bloccato. |

### Coppie di volumi

Dai `TestVolumes` l'harness genera:

- una coppia **intra-volume** per ogni area (sorgente e destinazione sullo stesso volume);
- una coppia **cross-volume** per ogni combinazione di aree su **GUID di volume diversi**
  (non ordinata: `A→B` e `B→A` percorrono lo stesso codice, eseguirle entrambe
  raddoppierebbe solo i tempi). Tutte le combinazioni di `Kind` richieste
  (internal→internal, internal→external, external→external) restano coperte.

Due aree che finiscono sullo **stesso volume fisico** non producono una coppia
cross-volume — l'identità è il Volume GUID, non la lettera né il nome in configurazione —
e il report lo dice esplicitamente.

---

## Guard-rail

Il runner si rifiuta di partire (exit code `2`) se:

- `Enabled` è `false` o `TestVolumes` è vuoto → *"Nothing to do"*, exit `0`;
- il DB di produzione **esiste ma non è leggibile** → non si può dimostrare che le aree
  configurate siano lontane dai dati catalogati, quindi REFUSING (l'eccezione completa
  viene stampata);
- una `Path` **coincide, contiene o è contenuta** in una `WatchedRoot` di produzione;
- una `Path` è la **radice di un drive** o una cartella di **sistema** (Windows, Program Files…);
- una `Path` **non esiste** (un refuso non deve creare un'area di lavoro da qualche parte);
- due `TestVolumes` si **sovrappongono** (le loro scratch area collidono);
- `ScratchSubfolder` non è un singolo nome di cartella sicuro (niente separatori, `..`, drive).

Il containment riusa `Platform.PathBoundary`, lo stesso usato dal `Win32FileMover`.

### Opera solo su duplicati

L'harness **non adotta mai** file preesistenti. Ogni fixture è generata da lui dentro
`{Path}\{ScratchSubfolder}\{scenario}__p{coppia}\{source|target}\…`. Puntare una
`TestVolume` a una cartella che contiene roba vera è sicuro: quella roba non viene letta,
spostata né cancellata.

### Database

Ogni scenario riceve un **database SQLite usa-e-getta** creato in `%TEMP%`, mai dentro le
aree di test. Il DB di produzione viene aperto **in sola lettura** dal guard e mai toccato.

### Cleanup

A fine run vengono rimosse le sole `ScratchSubfolder` e i database temporanei.
I file che gli scenari hanno cancellato sono nel **cestino del rispettivo volume** e
**non vengono svuotati**: se un run va storto sono recuperabili (§6 no-hard-delete).
Il report lo ricorda esplicitamente.

---

## Esecuzione

```bash
dotnet run --project src/backend/FileTracert.HardwareSmoke
```

Va eseguito **elevato** se i volumi di test lo richiedono (stesso requisito del servizio).

Codici di uscita:

| Exit | Significato |
|---|---|
| `0` | Niente da fare, oppure tutti gli scenari PASS/SKIP. |
| `1` | Almeno uno scenario **FAIL**. |
| `2` | Rifiutato: configurazione non sicura o inutilizzabile, o run interrotto. |

Il report finale è una tabella `scenario · coppia · esito · durata`, seguita dal dettaglio
di ogni assert fallito (con i path concreti) e dalle note del run.

---

## Scenari

| Nome | Coppia | Cosa verifica |
|---|---|---|
| `move-file-intra` | intra | Move istantaneo: sorgente sparito, destinazione presente e integra, catalogo ri-puntato. |
| `move-file-cross` | cross | copy → verify → finalize: contenuto identico, sorgente rimosso, nessun `.partial`, catalogo ri-puntato. |
| `move-folder-excluded-files` | cross | I file esclusi dal filtro **restano sul sorgente**; solo i file copiati+verificati vengono rimossi; le cartelle non vuote sopravvivono. |
| `move-folder-nothing-to-copy` | cross | Cartella tutta esclusa: esito onesto, mai `Completed` senza che la destinazione esista. |
| `move-folder-rejected-at-enqueue` | intra | Move dentro sé stessa e move no-op: rifiutati **all'enqueue** (400), nessun job creato. |
| `rename-folder` | intra | Rename applicato, sottoalbero `Directories` e path FTS aggiornati a cascata. |
| `create-folder` | qualsiasi | Cartella creata su disco e presente nel catalogo. |
| `cancel-mid-copy` | cross | Cancel durante la copia: sorgente intatto, nessun `.partial`, nessun file finale sulla destinazione, job `Cancelled`. |
| `cancel-before-delete` | cross | Cancel nella finestra pre-delete (copia finalizzata, sorgente ancora sul disco): il worker riavviato onora `Cancelled`, sorgente mai cestinato, copia atterrata riconciliata nel catalogo. |
| `crash-resume-mid-copy` | cross | Worker ucciso a metà copia e riavviato: il job riprende e chiude al 100% netto, nessun orfano. |
| `crash-resume-verifying` | cross | Crash tra finalize e checkpoint `Verified`: il resume verifica il file finale in place e completa, senza fallire sul partial mancante. |
| `crash-resume-deleting-source` | cross | Crash a metà del recycle dei sorgenti: il resume tollera il sorgente già cestinato e completa. |
| `crash-resume-simple-op` | qualsiasi | Crash dopo la `File.Move` intra-volume non checkpointata: il re-run riconosce l'op già applicata, completa e aggiorna l'indice. |
| `intra-collision-blocked` | qualsiasi | Move intra-volume su destinazione occupata: `Blocked(NameCollision)` riattivabile, mai `Failed`, nessun file toccato. |
| `search-date-filter` | qualsiasi | Bound data della ricerca: `from` a mezzanotte tiene il giorno, `to` a mezzanotte lo esclude; i timestamp tornano UTC. |
| `rescan-preserves-overlay` | qualsiasi | Seconda scansione completa: `Files.Id`/`Directories.Id` invariati, overlay `Pending*` intatto, file sparito marcato `IsPresent=false` (mai cancellato), FTS aggiornata, job ancora eseguibile sulla riga ri-scansionata. **È l'unico scenario che esegue una scansione vera del volume: il più lento.** |
| `exclusion-vs-absence` | qualsiasi | Perimetro ristretto fra due scansioni: la riga sotto la cartella resa Hidden esce `IsIncluded=false` con `IsPresent` **intatto**, il file davvero cancellato esce `IsPresent=false`, la cartella nascosta resta presente; poi il ritorno dentro il perimetro — un solo scan dopo aver tolto Hidden, e lo switch del watched root **senza alcuna scansione**. In coda, la metà **A2** (step 16): un segmento aggiunto a `ExcludedPaths` esclude le righe **già a catalogo** con `IsPresent` intatto, `ExcludedByPath` e non `ExcludedByScan`, fuori dall'FTS e `needsScan=false`; toglierlo le riammette, sempre **senza scansione**. Esegue scansioni vere del proprio sottoalbero. |
| `usn-incremental-sync` | qualsiasi | Il **giornale vero**: dopo una scansione completa un file viene creato, uno rinominato e uno cancellato **fuori dall'app**, e converge il **solo** delta. `LastFullScanUtc` non si muove e il rinominato conserva l'identità della riga (il FRN). **SKIP** se il volume non ha preso il motore USN (harness non elevato o filesystem senza giornale). |
| `usn-hidden-subtree` | qualsiasi | Il **giornale vero**, metà **A3** (step 16): una cartella diventa Hidden e **nient'altro viene toccato**, poi il solo delta. I file già indicizzati sotto di lei escono `IsIncluded=false` con `IsPresent=true` e causa `ExcludedByScan` (mai `ExcludedByPath`), il fratello fuori resta intatto, l'FTS è potata per cartella, `LastFullScanUtc` non si muove e un replay non cambia nulla. **SKIP** alle stesse condizioni. |
| `index-update-fail-once` | cross | Upsert FTS fallisce una volta durante il completamento: il commit atomico fa retry, il job chiude `Completed`, spazio decrementato una sola volta. |
| `phantom-reservation-rebuild` | cross | Riserva ledger orfana su job terminale (crash footprint): riconciliata al rebuild di startup, la feasibility torna corretta. |
| `insufficient-space` | cross | Job più grande di quanto il drive abbia **davvero**: `Blocked(InsufficientSpace)`, **non** `Failed`, niente copiato. |
| `live-space-recheck` | cross | Lo spazio sparisce **dopo** l'enqueue: il ricontrollo di esecuzione parcheggia il job invece di copiare a metà, poi il job riparte da solo. |
| `space-margin` | cross | `AppSettings.SpaceMarginPercent` muove qualcosa: un job che ci starebbe esatto viene parcheggiato dal margine e parte con margine 0. |
| `fifo-auto-recovery` | cross | Il job B, bloccato dietro lo spazio che il job A ha già prenotato, completa **da solo** dopo A, senza retry manuale. |
| `offline-simulated` | cross | Volume di destinazione marcato offline nel catalogo: il job **attende** (mai `Failed`) e parte da solo quando torna online. |
| `offline-enqueue-blocked` | cross | Enqueue con la destinazione offline: job **nato** `Blocked(TargetVolumeOffline)`, stima non live, **riserva mantenuta**, worker che lo ignora, niente sul target. |
| `offline-remount-space-recheck` | cross | Il volume torna senza lo spazio che la stima prometteva: il ricontrollo **hard** lo tiene `Blocked(InsufficientSpace)` invece di copiare; quando lo spazio c'è davvero completa da solo. |
| `offline-unplug` | cross, `SemiAutomatic` | L'operatore stacca fisicamente il drive esterno: il job sopravvive e completa al ricollegamento, anche con lettera diversa (la coda segue il Volume GUID). Dallo step 10a il ricollegamento è **rilevato dal device watcher reale** (nessuna sincronizzazione manuale nella seconda metà) e il tempo *ricollegamento → job terminale* è **asserito**: oltre 25 s è FAIL con il numero in chiaro. |

### Scenari attesi RED

Alcuni scenari descrivono il comportamento **corretto**, non quello attuale: restano rossi
finché il relativo pacchetto di lavoro non atterra. Un FAIL su questi è la specifica che
parla, non l'harness rotto.

Stato all'ultimo run di collaudo (2026-08-10, 2 volumi interni, `SemiAutomatic=false`) —
**30 PASS / 0 FAIL / 0 SKIP**:

- gli scenari **WP2** (`offline-simulated`, `offline-enqueue-blocked`,
  `offline-remount-space-recheck`) → **verdi** da quando il gate offline è atterrato:
  il job viene parcheggiato, non eseguito, e riparte da solo al ritorno del volume.
  `offline-unplug` resta fuori dal conteggio: gira solo con `SemiAutomatic=true`;
- `move-folder-excluded-files`, `move-folder-nothing-to-copy`,
  `move-folder-rejected-at-enqueue` → **WP3** (MoveFolder sicuro): **verdi**;
- tutti gli scenari **WP1** (crash/resume ai tre step, cancel-before-delete,
  collisione intra, index-update-fail-once, phantom-reservation) → **verdi** da quando
  WP1 è atterrato. Con il fix #7 (indice dentro il commit di Completed) le assert di
  catalogo leggono una volta sola: il vecchio settle-poll da 15 s è stato rimosso.

### Scenari SKIP

`cancel-mid-copy` e `crash-resume-mid-copy` devono catturare la copia **mentre** avviene.
Su un NVMe veloce il file di default può essere copiato prima che l'harness riesca a
interromperlo: lo scenario riporta **SKIP** con la spiegazione e il rimedio (alzare
`LargeFileMegabytes`). Uno SKIP **non** è un PASS e non fa fallire il run.

---

## Limiti noti

- **Gli scenari di spazio non riempiono il drive.** Dallo step 11b la fattibilità legge i byte
  liberi **dal dispositivo**, quindi abbassare `FreeBytesLastKnown` non mette più sotto pressione
  niente: la scarsità si arrangia sul lato **domanda** (dimensione indicizzata del file, oppure
  `RequiredBytesTarget` del job già accodato, che è il modo dell'harness di dire «un altro
  processo ha riempito il disco dopo l'enqueue»). È lo stesso confronto — domanda contro spazio
  libero reale — visto dall'altro capo, e non richiede di portare a zero un volume da centinaia
  di GB per qualche secondo: una zavorra del genere non è un test, è un disservizio, e se il
  processo muore in mezzo il drive resta pieno. La stima memorizzata viene lasciata
  **deliberatamente ottimista** in questi scenari: con il vecchio codice sarebbe bastata a far
  partire la copia, quindi il PASS dice anche che quel numero non viene più creduto.

- La verifica "il file è nel cestino" si ferma a *"non è più al suo path"* più il fatto che
  il volume abbia un cestino funzionante (`IFileMover.CanRecycle`): enumerare
  `$Recycle.Bin` richiederebbe nuova interop in Platform e non vale il rischio.
- L'arrangement non usa `ScanService`: su un volume NTFS quel servizio enumera l'intera MFT
  (giusto per il prodotto, minuti per volume qui). L'harness indicizza solo il proprio
  sottoalbero, passando comunque dal port di enumerazione e dal filtro reali. Dallo step 9a
  la scansione **fa merge** invece di troncare, quindi ri-indicizzare non è più distruttivo:
  gli scenari che devono provare la scansione vera (`rescan-preserves-overlay`, `exclusion-vs-absence`) la eseguono
  esplicitamente, su un watched root limitato alla propria area di fixture.
- Lo scenario `offline-simulated` marca il volume offline **nel catalogo** mentre resta
  fisicamente montato: verifica il *gate logico* della coda. Il comportamento hardware vero
  è coperto da `offline-unplug`.
- **Il device watcher (step 10a) è verificabile solo da `offline-unplug`.** Un arrivo vero non
  è simulabile: la notifica la emette Windows quando un dispositivo compare davvero, e nessuno
  scenario non interattivo può provocarla. Gli scenari `offline-*` non interattivi continuano a
  sincronizzare a mano (è il *gate* della coda che stanno testando, non il trigger): non
  dichiarano PASS su un push che non è mai avvenuto. Il resto della catena — raffica
  deduplicata, un solo ciclo di sync, nessuna sovrapposizione con il poll, fallback rumoroso se
  la registrazione nativa fallisce — è coperto da xUnit (`Host/DeviceWatcherWorkerTests`,
  `Platform/Win32DeviceWatcherTests`).
