# TASK — Step 16: le decisioni di perimetro raggiungono le righe già a catalogo

> **Sessione dedicata, agente singolo.** Chiude le voci **A2** e **A3** della roadmap.
> Riferimenti: `CLAUDE.md` §4 (filtri e riconciliazione), §6 (esclusione vs assenza, le cause
> di 11h), §9 (no silent catch). Niente skill `writing-plans`: **questo documento È il piano**.
> **Non è un task sui file caldi della coda**: non si tocca `JobExecutionEngine`, `SpaceLedger`,
> `QueueService`, `QueueProcessorWorker`.
> Due checkpoint. **Fermarsi al primo** e riprendere in una sessione pulita se il contesto è
> lungo: il checkpoint 1 porta la migration, il 2 la usa.

## Perché questi due insieme

Sono lo stesso difetto visto da due lati: **una decisione di perimetro non raggiunge le righe che
il catalogo ha già**. A2 è il lato *impostazioni* (l'utente esclude un segmento di path e non
succede niente), A3 è il lato *disco* (una cartella diventa nascosta e il suo sottoalbero resta
incluso). Condividono la **struttura** (separare le cause di esclusione, 11h) e il **meccanismo**
(un update di sottoalbero set-based). Farli in due giri significherebbe toccare due volte l'enum
delle cause e due volte lo stesso statement.

## A2 è più grave di come la roadmap lo scrive — letto nel codice, non ipotizzato

La roadmap dice: *«togliere un segmento escluso dice onestamente `NeedsScan` e non riammette nulla
senza scansione: corretto, ma non è una riconciliazione»*. Vero, e innocuo. **La metà che morde è
l'altra**, e non è scritta da nessuna parte:

`FilterReconciler` non nomina mai `ExcludedByScan` — di proposito (`FilterReconciler.cs:146`,
`SettingsCauses`, e il commento a `:144` lo dichiara). Ma `ExcludedByScan` è la causa in cui la
scansione scrive **sia** gli attributi **sia** il segmento di path escluso
(`ScanSkipCause.FilteredOut`, e `FileFilter.IsInsidePerimeter` che li mette insieme). Quindi:

> **Aggiungere** un segmento a `ExcludedPaths` non esclude **niente** di ciò che è già a catalogo.
> `FilterWidened` (`FilterReconciler.cs:47`) torna `false` perché nessuna delle due metà si è
> allargata → la schermata Setup annuncia un riallineamento e **non** `NeedsScan`. Le righe sotto
> quel segmento restano `IsIncluded = 1`: navigabili nel Catalogo e **trovabili in Ricerca**,
> finché non passa una scansione completa di quel volume.

Sul catalogo vero significa: l'utente esclude `AppData`, la UI dice ok, e i file di `AppData`
restano indicizzati. È un'esclusione **silenziosamente non applicata**, cioè la classe di guasto
peggiore — l'utente crede di aver deciso qualcosa.

**La causa strutturale.** `ExcludedByScan` somma due fatti di natura diversa:

| fatto | derivabile senza leggere il disco? | chi lo può disfare |
|---|---|---|
| **segmento di path escluso** | **sì** — `MaterializedPath` è a catalogo | le impostazioni |
| attributi (Hidden/System) | no — nessuna impostazione sa se è ancora nascosta | solo una scansione |

11h ha separato le cause **proprio** perché ognuna deve poter essere spenta dal suo proprietario.
Queste due sono ancora nella stessa colonna, quindi la prima è ostaggio della seconda.

## Decisioni già prese (non re-litigare, il perché è scritto)

- **Una quarta causa, non un enum e non un bitmask.** `Files.ExcludedByPath` si aggiunge alle tre
  di 11h; `ExcludedByScan` **si restringe agli attributi**. Stesso argomento di 11h: le cause si
  **sommano** (un file in `AppData` dentro una cartella nascosta è escluso due volte) e ognuna deve
  potersi spegnere da sola. L'invariante di §6 diventa, per ogni writer:
  `IsIncluded == !(ExcludedByType || ExcludedByRoot || ExcludedByScan || ExcludedByPath)`.
  `IsIncluded` resta colonna propria per il motivo di 11h: la leggono Catalogo, FTS e i covering
  index, e un OR di quattro booleani a ogni seek non è ciò che si vuole sul percorso caldo.
- **`ExcludedByScan` non cambia nome.** Rinominarla in `ExcludedByAttributes` sarebbe più onesto e
  costerebbe una migration di **rename di colonna** su 742 675 righe più il tocco di ogni writer,
  per zero comportamento. Il significato nuovo si scrive nel commento XML dell'entità e in §6.
- **`ScanSkipCause` guadagna un valore**, non un flag: `FilteredOut` si divide in
  `ExcludedAttributes` e `ExcludedPath`. `ScanPerimeter.ExcludeSubtree` (`:36`) deve **registrare
  con quale causa** ha escluso la directory, perché l'ereditarietà è diversa nei due casi:
  - escluso per **segmento di path** → ogni discendente ha quel segmento nel proprio path, quindi
    la riconciliazione lo ridecide da sola;
  - escluso per **attributi** → i discendenti non hanno nulla nel proprio path che lo dica, e
    l'esclusione è pura eredità. Solo una scansione la ritratta.
- **Quando entrambe le regole rifiutano lo stesso item si registrano ENTRAMBE le cause.** Non c'è
  una precedenza da scegliere, e cercarla è stato l'errore della prima stesura di questo task
  *(corretto dopo il primo giro di review, che l'ha trovato come BLOCKER)*. Le cause di 11h sono
  flag **proprio perché si sommano**: con una precedenza qualunque, disfare la causa vincente
  riammette la riga anche quando l'altra dovrebbe ancora tenerla fuori — cioè una cartella
  **nascosta** il cui contenuto torna a video perché l'utente ha tolto un segmento di path che non
  c'entra. È lo scenario di 11h con un innesco nuovo, ed è **peggio** di una precedenza sbagliata:
  dipende dalla storia, perché una scansione precedente che avesse visto la cartella nascosta ma
  non ancora path-esclusa lascia la riga protetta. Stesso disco, due esiti.
  Il meccanismo esiste già e **costa zero statement in più**: `SkippedScanArea` è una lista e
  `ExcludeForCauseAsync` gira **una volta per causa distinta**, non una per area. Una directory
  rifiutata da entrambe le regole emette due aree.
  Ne segue che il perimetro non può rispondere con **una** causa: deve dirle tutte, e chi vuole
  solo sapere «dentro o fuori?» chiede `Covers`.
- **Backfill pessimista, come 11h.** Le righe esistenti con `ExcludedByScan = 1` **restano** così,
  con `ExcludedByPath = 0`. Non si prova a indovinare: riammettere in silenzio è l'errore
  invisibile, tenere fuori una riga una scansione in più è l'errore visibile e reversibile. Prezzo
  dichiarato: una riga oggi esclusa **solo** da un segmento di path resta esclusa finché una
  scansione non la riguarda.
- **Il predicato di segmento in SQL ha una forma sola.** Un segmento combacia se il path *o il
  nome* è quel segmento, o lo contiene fra separatori. Si scrive una volta come
  «path e nome incorniciati fra separatori, confrontati con `LIKE` contro il segmento a sua volta
  incorniciato»: incorniciare i due capi rende i quattro casi di confine (inizio, fine, in mezzo,
  path intero) **un** caso. Il separatore non è un carattere jolly di `LIKE`; il segmento va
  comunque passato da `EscapeLike` (esiste da 11c) con la sua `ESCAPE`, perché un `%` in un
  segmento configurato sarebbe altrimenti un jolly.
  **Il nome del file fa parte del confronto** perché `FileFilter.IsPathExcluded` splitta il path
  *relativo del file*, che lo include: replicare la semantica esatta non è una scelta, è il
  requisito.
- **Case folding**: `LIKE` di SQLite piega l'ASCII, come `NOCASE` su `MaterializedPath` e come
  `OrdinalIgnoreCase` in memoria. Stesso limite già documentato da 9a e P2; va **scritto accanto
  al codice**, non scoperto di nuovo.
- **Un solo statement per root**, con l'OR dei segmenti, non uno per segmento: i segmenti esclusi
  sono una manciata (`Windows`, `Program Files`, `$Recycle.Bin`, `AppData`) e la riconciliazione
  gira dentro la transazione di Setup, cioè con l'unico write lock di SQLite in mano.
- **A3 non tocca le directory.** Una cartella che esiste sul disco esiste, anche se non se ne
  indicizza il contenuto: le `Directories` non hanno `IsIncluded` ed è la decisione di 11g. Il
  sottoalbero escluso muove **solo** righe `Files`.
- **A3 non scrive `IsPresent`.** Un'esclusione non è un'assenza (§6). Il pass degli assenti non
  raggiunge comunque le righe appena escluse, perché pretende `IsIncluded = 1`.

---

## Checkpoint 1 — la quarta causa (A2)

### Cosa cambia

1. **Schema.** `Files.ExcludedByPath` (bool, default `0`), migration additiva con backfill a `0`.
   Nessun indice: la colonna non entra in nessun predicato caldo — Catalogo, FTS e i covering di
   11e/14c leggono `IsIncluded`, che resta derivata.
2. **`ScanSkipCause`** (`Contracts/Scanning/ScanSkipCause.cs`): `FilteredOut` → `ExcludedAttributes`
   + `ExcludedPath`, ognuno col proprio commento su chi lo può disfare.
3. **`ScanPerimeter`** (`:36`, `:56`, `:68`): `ExcludeSubtree(path, cause)`; `ExcludedSubtrees`
   ricorda la causa e `SkipCause` la restituisce. Precedenza invariata: **prima i root**
   (un item fuori da ogni root attivo non è mai stato offerto al filtro).
   Fra le due nuove **non c'è precedenza**: si registrano entrambe quando entrambe si applicano
   (vedi la decisione qui sopra).
   Attenzione: `FileFilter.IsInsidePerimeter` oggi risponde sì/no; serve un membro che dica
   **quali** regole hanno rifiutato, senza chiamare due volte `IsPathExcluded` (che fa uno `Split`
   per chiamata — la review di 11g lo aveva già tolto da un ramo caldo).
8. **Il segmento configurato ha UNA normalizzazione**, condivisa dalle due metà. Oggi
   `FilterSettingsService` lo salva verbatim, `FileFilter.IsPathExcluded` lo confronta grezzo
   contro i segmenti splittati, e la metà SQL lo normalizza: `Windows\` — **la grafia che §4 usa**
   — combacia in SQL e non in memoria, quindi la riconciliazione esclude e la scansione successiva
   **riammette tutto**. Un'esclusione che sembra funzionare e poi si disfa da sola è il guasto che
   questo giro esiste per togliere. Normalizzare una volta sola in `EffectiveFilterBuilder.Build`,
   così le due metà consumano lo stesso insieme (e `FilterWidened` con loro).
   Un segmento **multi-parte** (`AppData\Local`) oggi combacia in SQL e mai in memoria: le due metà
   vanno fatte concordare, e la semantica che vince è quella dell'incorniciamento — «il path
   contiene questa sequenza di segmenti» — perché l'altra non è una scelta, è un valore che non
   combacia mai con niente.
4. **`BulkIndexWriter.ScanMerge`** (`ColumnFor`, `:200`): la terza voce dello switch. Il resto
   dello statement `ExcludeForCauseAsync` non cambia forma.
   Il merge azzera **tutte e quattro** le cause sulla riga che ritrova (`:381-383` e l'INSERT a
   `:400`): ha appena visto il file sul disco.
5. **`FilterReconciler`** (`ReconcileRootAsync`, `:87`): guadagna la metà path. Oggi splitta per
   `ExcludedByScan` (`:108`/`:110`); dovrà splittare per «una causa che non posso disfare», cioè
   `ExcludedByScan` — e decidere `ExcludedByPath` da sé. `SettingsCauses` (`:146`) scrive tre
   colonne invece di due.
   **I conteggi devono restare veri**: una riga che il path esclude non va contata «inclusa».
6. **`FilterWidened`** (`:47`): un **restringimento** dei segmenti non è più un no-op. Un
   allargamento continua a dire `NeedsScan` — e resta corretto: le righe mai indicizzate sotto quel
   segmento non le resuscita nessuno. Il testo della nota a video (`ReconcileResultDto` → store
   Angular) dice già l'effetto in numeri e non va toccato, ma **va verificato** che il caso
   «esclusi: N, needsScan: false» si legga bene.
7. **Gli altri writer dell'invariante**, uno per uno, perché 11h ha imparato che sbagliarne uno è
   un guasto a forma di perdita di dati:
   - `IndexUpdater.cs:165-168` (rename/move: ricalcola le cause che può decidere) e `:246-257`
     (che **alza** e non abbassa mai la causa di perimetro);
   - `OverlayWriter.cs:361-363` (la riga proiettata di una Copy copia le cause del sorgente);
   - `UsnDeltaApplier.ExcludeFilesAsync` (`:649`).
   Nessuno di questi può lasciare `IsIncluded` in disaccordo con le quattro colonne.

9. **La riconciliazione deve girare su TUTTI i root, non solo su quelli senza override.**
   `FilterSettingsService` (`:44`) filtra `FilterOverrideJson == null` perché finora cambiava solo
   l'allow-list delle estensioni, che è ciò che un override sostituisce. **`ExcludedPaths` è
   globale** e `EffectiveFilterBuilder.Build` lo applica a ogni root, override compreso: saltarli
   lascia il difetto A2 vivo esattamente dov'era, e per di più con `NeedsScan = false` e conteggi
   parziali, cioè la schermata che promette di aver applicato. Si itera su tutti i root, ognuno col
   **proprio** filtro effettivo (`Build(settings, root.FilterOverrideJson)`); per un root con
   override la metà tipo è un no-op, che è corretto.

### Test (RED prima del GREEN)

Contro l'implementazione reale: `FilterReconciler` vero, SQLite vero, `FileSearchIndex` vero.
Nuovi in `tests/FileTracert.Tests/Business/FilterReconcilerTests.cs` e `ExclusionCauseTests.cs`;
il backfill in `Data/ExclusionCauseBackfillTests.cs`, che ha già il pattern «applica le migration
alla versione precedente, scrivi righe come le scriveva quella versione, migra in avanti».

RED richiesti, **dimostrati rompendo il prodotto**:
- **il difetto**: aggiungi un segmento a `ExcludedPaths` → oggi la riga sotto quel segmento resta
  `IsIncluded = 1` e la Ricerca la trova. Deve diventare `ExcludedByPath = 1`, `IsIncluded = 0`,
  fuori dall'FTS, **con `IsPresent` intatto**;
- **il verso opposto**: togli il segmento → la riga torna inclusa e ricercabile **senza scansione**;
- **le cause si sommano**: un `.tmp` (fuori allow-list) dentro `AppData` è escluso due volte;
  disfarne una non lo riammette;
- **la causa che non si può disfare**: un file dentro una cartella **nascosta** resta escluso
  qualunque cosa si faccia ai segmenti — è la regressione che 11h esiste per impedire;
- **backfill**: una riga legacy `ExcludedByScan = 1` non viene riammessa dalla riconciliazione;
- **conteggi**: il numero «inclusi» riportato a Setup non conta una riga che il path esclude.

**Misura in statement, non in millisecondi** (`CountingSqliteConnection`, come 11g/11h): la
riconciliazione di un root deve costare un numero **fisso** di statement, e non muoversi passando
da 50 a 500 file sotto il root.

---

## Checkpoint 2 — il sottoalbero che diventa nascosto (A3)

### Il difetto, in chiaro

`UsnDeltaApplier.Classify` (`:390`) chiama `perimeter.ExcludeSubtree(item.Path)` quando una
directory del delta non passa il filtro. Da lì l'esclusione raggiunge **solo ciò che sta nello
stesso delta**: `directories.RemoveAll(...)` e il ramo `outside` di `ReconcileAsync`. Le righe
**già a catalogo** sotto quella cartella non sono nominate da nessun record del giornale — perché
non sono cambiate — quindi restano incluse fino alla scansione completa successiva.

Il pass completo non ha questo buco: `ScanService` chiede `perimeter.Covers` per **ogni directory
del catalogo** alla chiusura, quindi ogni discendente produce la propria area saltata. Il delta
non può: vede solo ciò che è cambiato. Il buco è del delta, ed è lì che si chiude.

### Cosa cambia

1. In `ReconcileAsync` (`:522`), per ogni sottoalbero escluso da questo delta: un `ExecuteUpdate`
   set-based sulle righe `Files` la cui `DirectoryId` cade in `Directories.InSubtree(volumeId,
   path)` — `DirectoryQueries.InSubtree` esiste (K5) e va **riusato**, non riscritto.
   Guardia: solo righe con la causa non ancora scritta o `IsIncluded = 1` (stessa forma di
   `ExcludeForCauseAsync`), così un delta ripetuto non riscrive niente — il replay dev'essere
   idempotente, è la proprietà su cui poggia «cursore scritto per ultimo».
2. **L'FTS si pota per directory, non per file.** `ExcludeFilesAsync` (`:649`) chiama oggi
   `_ftsIndex.RemoveAsync(id)` **in ciclo**: forma accettabile per una manciata di file nominati da
   un delta, sbagliata per un sottoalbero che può contenerne migliaia. Il pass nuovo usa
   `SyncDirectoriesAsync` (l'API set-based di 11e/E4). Il ciclo esistente **non si tocca**: è
   un'altra popolazione.
3. **La causa scritta è quella vera**: `ExcludedByScan` se la cartella è caduta per attributi,
   `ExcludedByPath` se per un segmento — cioè il checkpoint 1 usato dal checkpoint 2. Nel secondo
   caso la riconciliazione potrà poi disfarlo da sola, nel primo no.
4. **`ProjectionChanged`** va alzato come per ogni altra cosa che il delta muove (14d): il catalogo
   cambia senza che nessuno l'abbia chiesto, e senza quel messaggio Catalogo e Ricerca restano su
   dati vecchi.

### Test (RED prima del GREEN)

In `Business/UsnDeltaConvergenceTests.cs`, che ha già la forma giusta e va **riusata**: due
database dalla **stessa** scansione completa di un mondo di partenza, uno portato attraverso una
ri-scansione completa del mondo cambiato, l'altro attraverso il delta che descrive lo stesso
cambiamento, righe **identiche**. Caso nuovo: una cartella con dentro dei file già indicizzati
**diventa nascosta**, e nessuno di quei file cambia.

RED: senza il pass di sottoalbero i due database divergono su ogni file già indicizzato — è
esattamente il difetto, e la convergenza è l'unico attrezzo che lo dice senza ambiguità.

Più: **idempotenza** (lo stesso delta applicato due volte non cambia nulla e non scrive statement
in più) e **`IsPresent` intatto** su tutte le righe toccate.

---

## Harness sul ferro (obbligatorio, §«Test»)

Baseline **56/56**. `D:\Collaudo\A` ↔ `C:\Collaudo\B`, elevato (serve per il giornale USN).
`appsettings.json` rimesso byte-identico a fine giro, sha256 `653f5990…` verificato.

- **`ExclusionVsAbsenceScenario`** — esteso: un segmento di path aggiunto a `ExcludedPaths` deve
  escludere le righe già a catalogo **su file veri**, con `IsPresent` intatto, e toglierlo deve
  riammetterle **senza scansione**.
- **`UsnIncrementalSyncScenario`** — esteso, oppure scenario nuovo `usn-hidden-subtree`: cartella
  vera resa nascosta con `File.SetAttributes` dopo una scansione completa, poi **solo** il delta.
  Le due asserzioni che portano il peso: `LastFullScanUtc` **non** si è mosso (altrimenti lo
  scenario misurerebbe una scansione, cioè la strada sbagliata) e i file dentro sono
  `IsIncluded = 0` con `IsPresent = 1`.

## Split dei commit

1. `feat(data)` — colonna `ExcludedByPath` + migration + backfill, entità e commento
   dell'invariante a quattro termini.
2. `feat(scan)` — `ScanSkipCause` a tre valori, `ScanPerimeter` che porta la causa,
   `FileFilter` che dice quale metà ha rifiutato, `ColumnFor` del writer.
3. `fix(setup)` — `FilterReconciler` decide la metà path; `FilterWidened` smette di dire che un
   restringimento è un no-op. **È il commit che chiude A2.**
4. `fix(scan)` — il sottoalbero escluso dal delta raggiunge le righe già a catalogo.
   **È il commit che chiude A3.**
5. `test(harness)` — i due scenari sul ferro.
6. `docs` — §6 (la quarta causa, e `ExcludedByScan` che ora significa solo attributi), la roadmap
   (A2 e A3 chiuse, col residuo dichiarato), il paragrafo «Fatto nello step 16».

Se un file porta più preoccupazioni, staging a livello di hunk (`git add -p`).

## Definizione di finito

- xUnit verde (baseline **882**), Vitest verde (baseline **256**) — il frontend **non si tocca**,
  quindi Vitest deve restare a 256 e `ng build` non va rieseguito se non cambia una riga di TS/SCSS.
- Build backend pulita, **warnings-as-errors**.
- RED dimostrato **rompendo il prodotto e ripristinandolo**, non solo per costruzione, e scritto
  con il numero dei rossi.
- Harness **56 + i nuovi**, 0 FAIL, eseguito **due volte**: prima e dopo il giro di review.
- **Code review finale da un agente indipendente** con contesto pulito (l'utente l'ha autorizzato,
  ed è la forma che 15b ha usato). Riportare cosa ha trovato e cosa è stato corretto, o perché un
  rilievo è stato lasciato consapevolmente.

## Limiti che questo giro NON chiude (dichiararli, non nasconderli)

- **Il verso opposto di A3 resta scoperto**: una cartella che *smette* di essere nascosta non è
  rilevabile dal delta — un record di cartella che passa il filtro e non ha riga è indistinguibile
  da una cartella nuova, e i file che contiene non generano record propri. Serve una scansione,
  come 11g già scrive. Va detto nella roadmap invece di far credere che A3 sia chiusa intera.
- **E il traffico di scrittura normale disfa l'esclusione che il delta ha appena scritto** — lo
  stesso confine visto dall'altro capo, ed è la metà che *toglie* invece di non aggiungere.
  `ScanPerimeter` conosce solo i sottoalberi esclusi da **questo** delta. Un file dentro la cartella
  nascosta, toccato in un tick **successivo** che non nomina la cartella, viene giudicato sui propri
  attributi puliti e sul proprio path pulito, passa `insidePerimeter`, finisce in `indexable`, e il
  merge lo riscrive `IsIncluded = 1` **azzerando tutte e quattro le cause** — perché ha appena visto
  il file sul disco, il che è corretto di per sé. Misurato con una sonda usa-e-getta (non
  committata), due tick su una sola cartella nascosta:

  | tick | esito sulla riga già a catalogo |
  |---|---|
  | 1 — la cartella diventa nascosta | `IsIncluded = 0`, `ExcludedByScan = 1` |
  | 2 — solo il FILE viene scritto | `IsIncluded = 1`, tutte le cause a 0 |

  **Perde solo la metà attributi.** Con l'esclusione per **segmento di path** la stessa sequenza
  lascia la riga fuori (`IsIncluded = 0`, `ExcludedByPath = 1`, verificato con la stessa sonda),
  perché `FileFilter.IsPathExcluded` legge il path relativo **del file** e il segmento è lì dentro.
  È esattamente l'asimmetria su cui `PerimeterVerdict` è costruito: un fatto delle impostazioni si
  ri-deriva dal catalogo, un fatto del disco no.

  **Non è una regressione di questo giro, ma il cambiamento di stato va detto per intero.** Una riga
  che una **scansione completa** aveva escluso per attributi rientrava già così — quindi il difetto
  è anche sul servizio installato. Ciò che cambia è che **prima non contava**: il delta quelle righe
  non le escludeva, quindi non c'era niente da disfare. Da adesso il catalogo può trovarsi in uno
  stato **misto** che *nessuna delle due strade produce da sola* — il sottoalbero escluso, e dentro
  di esso le righe che nel frattempo qualcuno ha scritto, rientrate.

  **Chiuderlo richiede un fatto che il catalogo non ha**: nessuna riga dice «questa cartella è
  nascosta» — le `Directories` non hanno un flag di inclusione, ed è la decisione di prodotto di
  11g — e l'alternativa è leggere gli attributi dal disco per ogni file di ogni delta. Entrambe
  fuori da questo task. Il confine è scritto **anche nel codice**, accanto al «KNOWN HOLE» di
  `UsnDeltaApplier.ReconcileAsync`: i due lati vanno letti insieme.
- **Il backfill pessimista lascia indietro le righe legacy**: una riga oggi esclusa solo da un
  segmento di path porta `ExcludedByScan = 1` e non rientrerà togliendo il segmento, finché una
  scansione non la riguarda. Prezzo scelto, non svista.
- **`IndexUpdater` valuta ancora `IsIncluded` col filtro di default** quando la destinazione di un
  rename/move è fuori da ogni root attivo (11g): fuori perimetro di questo task.
- **I tre handler di Move non scrivono alcuna causa di perimetro** — `MoveFileIndexAsync`,
  `MoveFolderIntra/CrossIndexAsync` e `ReconcileCancelledJobAsync`. Un `MoveFile` completato verso
  una cartella sotto un segmento escluso riscrive `DirectoryId` e **fa l'upsert FTS**, lasciando la
  riga `IsIncluded = 1` con `ExcludedByPath = 0`: il file è trovabile in Ricerca dentro un posto che
  il perimetro esclude. **Non è una regressione di questo giro** — prima il Move non scriveva nemmeno
  `ExcludedByScan`, e il punto 7 nominava «rename/move» ma quelle righe erano l'atterraggio della
  **Copy**; il Move non è mai stato nello sweep. Questo giro **restringe** la finestra invece di
  allargarla: da qui un salvataggio di Setup la ripara in SQL, dove prima serviva una scansione
  completa. È la stessa famiglia della riga qui sopra, che 11g documenta su `IndexUpdater`, e
  chiuderla vuol dire toccare handler che questo task non apre.
- **La riconciliazione continua a non leggere il disco**, quindi non conosce gli attributi. È il
  punto: la metà che questo giro le dà è esattamente quella che può decidere da sola.
- **Il servizio installato non viene aggiornato** da questo giro: c'è una migration, quindi
  distribuirlo è una decisione dell'utente, e il deploy ha una sua procedura.
- **E2E non eseguiti**: il loro `globalSetup` si rifiuta di partire da elevato (12a) e la sessione
  è elevata per l'harness.
