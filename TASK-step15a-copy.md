# TASK — Step 15a: l'operazione Copy

> **Sessione dedicata, agente singolo.** Primo lavoro di **fase 2** (§11), scelto perché riusa
> una macchina a stati già provata sul ferro invece di inventarne una.
> Riferimenti: `CLAUDE.md` §4 (coda durevole, space ledger), §5 (proiezione), §6 (schema),
> §11 (fase 2). Niente skill `writing-plans`: **questo documento È il piano**.
> **Files caldi della coda** (`JobExecutionEngine`, `SpaceLedger`, `QueueService`,
> `QueueProcessorWorker`): agente unico, in sequenza, mai in parallelo.

## Perché

§11 elenca **«Operazione Copy (oltre a Move)»** fra le voci di fase 2, e `JobType` porta il
commento `*(Copy → fase 2)*` da sempre. È l'unica voce di fase 2 che non richiede un modello
nuovo: la state machine cross-volume esiste, è idempotente, ha `.fadit-partial`, verify prima
del delete e ripresa dal checkpoint — tutto già esercitato dall'harness sul ferro.

**Ma attenzione a dove sta davvero il lavoro.** La stima «Copy è Move senza `DeletingSource`»
è vera **solo** per l'esecuzione. Le tre cose che costano sono altrove, e questo task esiste
per pagarle in modo esplicito:

1. **Una copia intra-volume sposta byte.** Un move intra-volume è metadati, istantaneo, O(1),
   nessuna prenotazione (§5). Una copia intra-volume **no**: consuma spazio *sullo stesso
   volume*. Ogni predicato che oggi legge `IsIntraVolume` come sinonimo di «gratis» diventa
   sbagliato.
2. **Una copia crea un'entità nuova.** Move/Rename mutano la riga che c'è; la proiezione (§5)
   li esprime con i campi `Pending*` **su quella riga**. Una copia deve far comparire una riga
   **alla destinazione** che sul disco non esiste ancora — cioè per i file quello che
   `IsMaterialized = false` già fa per le cartelle.
3. **Il sorgente sopravvive.** Il guard di enqueue (§5, «una sola operazione pendente per
   entità») serializza per sovrapposizione di path assumendo che chi tocca un path lo *sposti*.
   Una copia lo legge soltanto.

## Decisioni già prese (non re-litigare, il perché è scritto)

- **Due tipi, non uno**: `JobType.CopyFile` e `JobType.CopyFolder`, simmetrici a
  `MoveFile`/`MoveFolder`. §5 elenca le operazioni accodabili per **entità**, e tutti gli
  switch esaustivi del backend sono scritti così: un `Copy` unico costringerebbe ogni ramo a
  ri-derivare «file o cartella?» da campi opzionali della richiesta.
- **Una Copy non è mai «simple»**, nemmeno intra-volume: passa **sempre** dalla state machine
  `Pending → SpaceReserved → Copying → Verifying → Completed`. Salta `DeletingSource` e basta.
  `JobState` **non cambia**: uno stato in meno percorso non è uno stato nuovo.
- **`FreedBytesSource = 0`.** Una copia non libera niente. `BuildReservationEntries` emette
  allora la sola voce `+riserva` sul target, senza `−liberazione`: nessun cambiamento lì.
- **Collisione alla destinazione = `Blocked(NameCollision)`**, come per Move. Niente
  `nome (2)` automatico: inventare un nome che l'utente non ha scelto è una decisione di
  prodotto che nessuno ha preso, e `Blocked` è riattivabile.
- **La proiezione si fa** (§5 è esplicito: accodare muta *immediatamente* la proiezione), e si
  fa **come per le cartelle**: riga di destinazione con `PendingState = PendingCreate`,
  `IsMaterialized = false`, `IsPresent = false`. Serve quindi **`IsMaterialized` su
  `FileEntry`** + migration (default `true`, backfill `true`). L'alternativa «nessuna
  proiezione finché il job non finisce» è stata scartata: accodare cinquanta copie e non
  vedere nulla è esattamente ciò che §5 vieta.

## Lavoro, per checkpoint

### 1. Schema e contratto
- `JobType` += `CopyFile`, `CopyFolder`. Compilare: **tutti gli switch esaustivi diventano
  rossi** ed è il punto — sono l'inventario dei posti da toccare (`IndexUpdater`,
  `JobExecutionEngine` ×2, `JobSnapshotRefresher`, `PendingWorkGuard`, `QueueService` ×2,
  `OverlayWriter`).
- `FileEntry.IsMaterialized` + `IEntityTypeConfiguration` + migration con backfill a `true`.
  Aggiornare §6 di `CLAUDE.md` nello stesso commit dello schema.
- `CreateJobRequest`: i commenti XML nominano le op che richiedono ogni campo — aggiornarli,
  altrimenti mentono (lezione 14e).

### 2. Enqueue
- `BuildCopyFileAsync` / `BuildCopyFolderAsync`. Riusare `ApplyCrossVolumeDemandAsync` con un
  parametro che azzeri `FreedBytesSource`, **non** una seconda copia della stanza (K4 esiste
  per questo).
- **Chiamarla anche quando `intra == true`**: è il punto n° 1 del «Perché».
- `SpaceLedger.ReservationFor` non può più essere gated da `!job.IsIntraVolume`. La domanda
  vera è «questo job consuma byte sul target?», cioè `RequiredBytesTarget > 0`.
  **Test obbligatorio in entrambe le direzioni**: un move intra-volume continua a NON
  prenotare (oggi `RequiredBytesTarget` è 0 per costruzione — il test lo fissa), una copia
  intra-volume prenota. `SpaceCheck.EvaluateHardAsync` specchia la stessa guardia: verificare
  che resti d'accordo, o diventano due definizioni di «ha bisogno di spazio».
- `PendingWorkGuard`: una copia **non rivendica il sorgente**. Due copie dello stesso file
  verso destinazioni diverse devono essere entrambe `Pending`; una copia il cui sorgente è
  target di un move pendente resta un conflitto (il sorgente può sparirle sotto). Concretamente:
  il path sorgente di una Copy non è più «un path SORGENTE» ai fini della regola, i suoi
  **target** sì.

### 3. Proiezione
- `OverlayWriter.ApplyCopyFileAsync` / `ApplyCopyFolderAsync`: creano la riga proiettata alla
  destinazione (file, e le directory intermedie con il `DirectoryResolver` che `CreateFolder`
  già usa), `PendingJobId` = job.
- **Visibilità**: il Catalogo filtra i file con `IsIncluded && IsPresent`
  (`CatalogController.cs:107,121`). Va aggiunto il disgiunto `|| PendingState != None`, come
  già fanno le directory. **Costo da misurare, non da assumere**: gli indici covering di 11e e
  14c portano `IsIncluded, IsPresent` e 11e ha *rifiutato* di metterci `PendingState` (è una
  stringa). Misurare il piano prima e dopo con l'attrezzo di 11e/14a, e scrivere il numero.
- FTS: la riga proiettata è cercabile (§5 — il nome proiettato è ciò che si indicizza).
- `ClearForJobAsync` deve saper **rimuovere** la riga proiettata quando la copia è annullata o
  fallita: qui la pulizia dell'overlay non azzera campi, cancella una riga che non è mai
  esistita sul disco. È l'unico punto in cui il no-hard-delete di §6 non si applica, e va
  scritto accanto al codice: quella riga non descrive niente di reale.

### 4. Esecuzione
- `ExecuteAsync`: `simple` smette di essere `IsIntraVolume || …` e diventa esplicito per tipo.
- Dopo `Verifying`, un job Copy va a `CompleteJobAsync` senza passare da `DeletingSource`.
  Verificare la **ripresa**: un Copy interrotto in `Copying`/`Verifying` deve riprendere, e un
  Copy interrotto *dopo* il finalize non deve rifare nulla.
- `IndexUpdater`: al completamento la riga proiettata diventa reale (`IsMaterialized = true`,
  `IsPresent = true`, `PendingState = None`, dimensioni/date lette dal file atterrato).
  **`UsnFileRef` resta null** — è l'FRN del file nuovo, che solo una scansione conosce, e
  l'indice unico `(VolumeId, UsnFileRef)` è filtrato, quindi più null convivono.
- **Copy cross-volume di cartella**: espansione ricorsiva come `MoveFolder`, meno il delete.

### 5. UI (skill `impeccable`)
- Il picker offre **«Copia in…»** accanto a «Sposta in…». Il preview di §7 vale identico: la
  fattibilità è la stessa domanda, con `FreedBytesSource = 0`.
- Coda: il tipo va mostrato per quello che è; il badge di proiezione dice **«In creazione»**
  sulla riga di destinazione, che è ciò che una copia pendente è.
- `JobType` TS in `catalog.models.ts` e la mappa dei generi di stato (K8) vanno estesi —
  sono esaustivi per costruzione, quindi non compilano finché non li tocchi.

## Vincoli

- **Test RED prima del GREEN**, contro l'implementazione reale (engine + ledger +
  `Win32FileMover` + SQLite veri). Un test che mocka il ledger non testa il ledger.
- **Almeno tre scenari nuovi in `FileTracert.HardwareSmoke`**, che devono passare sul ferro:
  copia intra-volume (il caso che il modello vecchio non prevedeva), copia cross-volume, e
  copia annullata a metà (il partial va via, la riga proiettata anche, il sorgente è intatto).
- **Suite verde + build pulita** (warnings-as-errors) a fine task; `ng build` se si tocca il
  frontend.
- **Nessuna scorciatoia sul ledger**: il rilascio resta nella stessa transazione del cambio di
  stato terminale (WP1 finding #5, E8).

## Split dei commit suggerito

1. `feat(contracts)`: `JobType.CopyFile/CopyFolder` + `FileEntry.IsMaterialized` + migration.
2. `feat(queue)`: enqueue delle due Copy + domanda di spazio anche intra-volume.
3. `fix(ledger)`: `ReservationFor` guarda la domanda, non `IsIntraVolume` (+ i due test).
4. `feat(queue)`: guard — una copia non rivendica il proprio sorgente.
5. `feat(projection)`: riga proiettata alla destinazione + visibilità + FTS + pulizia.
6. `feat(queue)`: esecuzione (salto di `DeletingSource`) + `IndexUpdater` al completamento.
7. `feat(frontend)`: «Copia in…», Coda, tipi TS.
8. `test(harness)`: i tre scenari sul ferro.
9. `docs`: paragrafo «Fatto nello step 15a» in `CLAUDE.md` + §5/§6/§11 allineati.

## Definition of done

- Le due Copy esistono end-to-end: accodo dal Catalogo, la destinazione si vede subito con il
  badge, il job esegue, la riga diventa reale, il sorgente è **intatto**.
- Una copia intra-volume prenota spazio e viene rifiutata (`Blocked`, mai un errore
  all'enqueue) quando il volume non ce la fa.
- Un annullamento a metà non lascia né `.fadit-partial`, né righe proiettate, né file orfani.
- Harness sul ferro: **tutti gli scenari PASS**, i tre nuovi compresi.
- Il costo del disgiunto `PendingState` sul piano del Catalogo è **misurato e scritto**, non
  assunto.
