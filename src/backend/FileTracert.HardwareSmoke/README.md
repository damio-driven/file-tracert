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
| `index-update-fail-once` | cross | Upsert FTS fallisce una volta durante il completamento: il commit atomico fa retry, il job chiude `Completed`, spazio decrementato una sola volta. |
| `phantom-reservation-rebuild` | cross | Riserva ledger orfana su job terminale (crash footprint): riconciliata al rebuild di startup, la feasibility torna corretta. |
| `insufficient-space` | cross | Job che non ci sta: `Blocked(InsufficientSpace)`, **non** `Failed`, niente copiato. |
| `fifo-auto-recovery` | cross | Il job A libera lo spazio che serve a B: B completa **da solo**, senza retry manuale. |
| `offline-simulated` | cross | Volume di destinazione marcato offline nel catalogo: il job **attende** (mai `Failed`) e parte da solo quando torna online. |
| `offline-enqueue-blocked` | cross | Enqueue con la destinazione offline: job **nato** `Blocked(TargetVolumeOffline)`, stima non live, **riserva mantenuta**, worker che lo ignora, niente sul target. |
| `offline-remount-space-recheck` | cross | Il volume torna più pieno della stima: il ricontrollo **hard** lo tiene `Blocked(InsufficientSpace)` invece di copiare; quando lo spazio c'è davvero completa da solo. |
| `offline-unplug` | cross, `SemiAutomatic` | L'operatore stacca fisicamente il drive esterno: il job sopravvive e completa al ricollegamento, anche con lettera diversa (la coda segue il Volume GUID). |

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

- La verifica "il file è nel cestino" si ferma a *"non è più al suo path"* più il fatto che
  il volume abbia un cestino funzionante (`IFileMover.CanRecycle`): enumerare
  `$Recycle.Bin` richiederebbe nuova interop in Platform e non vale il rischio.
- L'arrangement non usa `ScanService`: su un volume NTFS quel servizio enumera l'intera MFT
  (giusto per il prodotto, minuti per volume qui) e il suo persist **tronca** l'indice del
  volume. L'harness indicizza solo il proprio sottoalbero, passando comunque dal port di
  enumerazione e dal filtro reali.
- Lo scenario `offline-simulated` marca il volume offline **nel catalogo** mentre resta
  fisicamente montato: verifica il *gate logico* della coda. Il comportamento hardware vero
  è coperto da `offline-unplug`.
