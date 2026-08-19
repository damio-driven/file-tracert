# TASK — Step 11a: correttezza indice / ricerca (WP5)

> **Sessione dedicata, agente singolo.** Primo dei sei task dei WP minori
> (`TASK-step11-overview.md`). Prerequisito: working tree pulito, suite verde, Host chiuso.
> Riferimenti: `CLAUDE.md` §4 (filtri dentro la pipeline), §6 (schema, indici), §5
> (proiezione), §9 (no silent catch); `CODE-REVIEW-HANDOFF.md` → finding 6, C19, C16, P2.
> ⚠️ Tocca `IndexUpdater` e `JobSnapshotRefresher`: **niente parallelo** su questi file.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

WP5 è l'ultimo pezzo di correttezza dell'indice rimasto aperto. I fix di date/UTC
(finding 11 e 12) sono già andati nel giro «date/UTC»; qui restano quattro difetti che
**sopravvivono fino al prossimo full re-scan** e nel frattempo mentono all'utente in
ricerca e in catalogo. Più il FAIL harness pre-esistente su coppia cross-volume, che sta
negli stessi file.

Stato verificato su `88571aa` (riverificare le righe prima di editare):

| # | Dove | Cosa succede oggi |
|---|------|-------------------|
| 6 | `IndexUpdater.MoveFileIndexAsync` (~:95-104), `MoveFolderCrossIndexAsync`; `UsnFileRef` assegnato **solo** in `ScanService.cs:501`; indice unico filtrato in `FileEntryConfiguration.cs:45-47` | Il move cross-volume ri-punta `VolumeId` ma si porta dietro l'FRN del volume **sorgente**. Gli indici MFT bassi si ripetono su ogni volume NTFS: collisione → `DbUpdateException` **dopo** il move fisico → job ribaltato a `Failed`, e il retry salta gli item `Done` e ricade sulla stessa violazione. Senza collisione: FRN stantio che inquina il matching dei delta USN e del merge di scan (9a matcha **per FRN** prima che per path). |
| C19 | `IndexUpdater.RenameFileIndexAsync` (~:72) | Il rename scrive `file.Name` e basta. `Extension` e `Category` restano quelli vecchi → i filtri di Ricerca (`FileSearchIndex`) e `FilterReconciler` lavorano su valori morti. Rinomina `foto.jpg` → `foto.txt`: resta `Image`, resta estensione `jpg`. |
| C16 | `FileFilter.ShouldIncludeFile` / `ShouldIncludeDirectory` (`Filtering/FileFilter.cs`) + `ScanService.cs:262/270` | L'esclusione per attributi guarda **solo gli attributi propri** dell'elemento. NTFS non propaga Hidden/System ai figli: i file dentro una cartella nascosta passano il filtro, e la loro dir viene **resuscitata** come materializzata dalla risalita dei parent. Alberi che l'utente crede esclusi sono interamente indicizzati. |
| P2 | `IndexUpdater.cs:128/306`, `JobSnapshotRefresher.cs:~107`, `Projection/DirectoryResolver.cs:~56`, `Projection/OverlayWriter.cs:~309` | `MaterializedPath ==` in SQL usa la collation **BINARY**, mentre le cache in memoria confrontano `OrdinalIgnoreCase`. Un case-variant (`photos` vs `Photos`) produce un `DirectoryNode` duplicato invece di riusare la riga esistente. |
| — | `JobDependenciesScenario` sulla coppia **cross** | FAIL **pre-esistente** (riprodotto su `7a87fd5`): un dipendente che segue un move *cross-volume* finisce `Failed` invece di `Blocked` con messaggio. Il replay di `JobSnapshotRefresher` copre solo i move/rename di cartella **intra-volume**. |

## Lavoro

### 1. Finding 6 — l'FRN non attraversa i volumi

L'FRN è l'identità di un file **dentro un volume**: cambiato volume, non significa più
niente. Azzerare `UsnFileRef` ovunque un file cambi `VolumeId` (`MoveFileIndexAsync` e il
percorso cross di `MoveFolderCrossIndexAsync`), nella **stessa** `SaveChanges` che sposta
la riga: un azzeramento in una transazione a parte lascia una finestra in cui la
violazione può ancora scattare.

Decidere e **motivare nel commit** cosa fare di `QuickHash`/`Hash`: sono funzione del
contenuto, non del volume, quindi restano — a meno che si trovi un consumatore che li
legge assumendo lo stesso volume.

Il re-scan del target riassegnerà l'FRN (9a: match per FRN, poi per path `COLLATE NOCASE`).

### 2. C19 — il rename ricalcola estensione e categoria

`RenameFileIndexAsync` deve derivare `Extension` e `Category` dal **nome nuovo**, con gli
stessi helper della pipeline di scansione (`FileFilter.GetExtension`,
`FileFilter.ResolveCategory` + la mappa `ExtensionCategories`) — non con una seconda regola
scritta a mano qui (§9, niente duplicazione).

Domanda che il fix deve porsi e risolvere: se il nuovo nome **esce dall'allow-list** del
filtro, `IsIncluded` va cambiato? La risposta coerente con §4 è **sì, riconciliazione**:
`IsIncluded=false`, mai delete. Se scegli diversamente, motivalo nel commit.

### 3. C16 — l'esclusione si eredita

La pipeline deve sapere che un antenato è escluso. Due strade:

- **(a)** propagare durante l'enumerazione: la scansione visita l'albero, quindi può
  portarsi dietro un flag «sono dentro un sottoalbero escluso» e scartare tutto ciò che
  sta sotto senza riesaminare gli attributi;
- **(b)** tenere l'insieme dei prefissi esclusi incontrati e testare ogni path contro di
  esso (riusa `ScanPath.Overlaps`/`IsWithin`, non scrivere un sesto matcher di sottoalbero
  — vedi K5, chiuso allo step 9c).

(a) è preferibile sul motore a enumerazione; sul motore USN la scansione **non** è un
cammino ad albero (è un dump MFT + ricostruzione path), quindi lì serve (b). Vale per
entrambi i motori: scegli l'implementazione ma copri **tutti e due** i percorsi.

Attenzione al secondo mezzo difetto di C16: la risalita find-or-create dei parent
(`DirectoryResolver`) **resuscita** una dir esclusa marcandola materializzata. Un file
escluso non deve poter creare i suoi antenati.

### 4. P2 — una collation sola per `MaterializedPath`

Configurare la collation della colonna in `IEntityTypeConfiguration` (`NOCASE`) + migration,
così i confronti SQL e le cache in memoria dicono la stessa cosa. Ricordare il limite già
documentato allo step 9a: `NOCASE` in SQLite piega **solo l'ASCII**. Scriverlo accanto al
codice, non scoprirlo di nuovo tra sei mesi.

Verificare l'impatto sugli indici esistenti su `MaterializedPath` (una colonna con
collation cambiata vuole l'indice ricostruito) e che le query `StartsWith` del sottoalbero
(`DirectoryQueries.InSubtree`) restino coerenti con `ScanPath.Overlaps`.

### 5. Il FAIL harness cross-volume

**Prima diagnosticare, poi decidere.** Ipotesi da verificare, non da assumere: dopo un move
cross-volume il file è ri-risolto per identità (`FileId`) e il suo path torna corretto, ma
il job dipendente conserva `SourceVolumeId` del volume vecchio → l'engine lo cerca sul
volume sbagliato → eccezione generica → `Failed`.

Due esiti accettabili, in ordine di preferenza:

1. **Il dipendente segue il file**: se l'item è risolto per `FileId`, aggiornare anche il
   volume del job insieme al path. È il comportamento che l'utente si aspetta e chiude lo
   scenario con un `Completed`.
2. Se (1) apre più problemi di quanti ne chiuda (es. un job con item su due volumi), allora
   **almeno** `Blocked` con messaggio esplicito invece di `Failed` terminale — è ciò che
   §4 chiede per una condizione recuperabile. In questo caso lo scenario harness va
   aggiornato ad asserire `Blocked` + motivo, **e** il limite va scritto in `CLAUDE.md`.

Non lasciare il FAIL com'è: o passa, o passa con un'aspettativa diversa e documentata.

## Split dei commit (indicativo)

1. `fix(business): drop the source FRN when a file changes volume` — finding 6 + test.
2. `fix(business): a rename recomputes extension and category` — C19 + test.
3. `fix(scan): exclusion is inherited by the whole subtree` — C16 + test (entrambi i motori).
4. `fix(data): one collation for MaterializedPath` — P2 + migration + test.
5. `fix(business): a dependent job follows its file across volumes` — punto 5 + scenario harness.

Un file che porta più fix → staging a livello di hunk (`git add -p`).

## Test (RED prima del GREEN)

Contro l'implementazione reale (SQLite vero su sandbox temp), mai mock del componente
sotto esame:

- **6**: move cross-volume di un file il cui FRN **esiste già** sul volume target →
  oggi `DbUpdateException`/`Failed`, dopo il fix `Completed` con `UsnFileRef` null.
- **C19**: rename `a.jpg` → `a.txt` → `Extension = "txt"`, `Category = Document`, e il
  filtro categoria in Ricerca lo trova nella categoria nuova (non solo l'assert sull'entità).
- **C16**: albero con dir Hidden contenente file dagli attributi puliti → nessuna riga
  `Files`, nessuna dir resuscitata `IsMaterialized=true`. Un test per motore.
- **P2**: catalogo con `Photos` indicizzata + enqueue di un move verso `photos\x` →
  **una** riga `Directories`, non due.
- **cross**: test di integrazione sul dipendente dopo un move cross-volume, con l'esito
  scelto al punto 5.

## Harness sul ferro (obbligatorio)

Configurare `HardwareSmoke` sulla coppia **cross** (`D:\Collaudo\A` + `E:\Collaudo\B`) e
far girare almeno: `job-dependencies` (deve smettere di essere FAIL), gli scenari di move
cross-volume esistenti, `rescan-preserves-overlay` (il finding 6 tocca il matching per FRN
del merge di scan). Riportare i numeri: scenari applicabili, PASS/FAIL, tempi di scan.
Ricordare di **rimettere `appsettings.json` come stava** a fine collaudo.

## Definition of done

- xUnit verde (nuovi test inclusi), build backend pulita (warnings-as-errors).
- Harness sul ferro: elenco scenari + esito, nessun FAIL nuovo, `job-dependencies` chiuso.
- **Code review finale** indipendente sulle modifiche del giro: correttezza vs scenari di
  fallimento, no silent catch (§9), layering (§3), niente duplicazione, RED→GREEN reale.
  Riportare cosa ha trovato e cosa è stato corretto (o perché un rilievo resta aperto).
- `CLAUDE.md`: paragrafo «Fatto nello step 11a» con deviazioni e limiti noti; in
  `CODE-REVIEW-HANDOFF.md` marcare 6/C19/C16/P2 come chiusi.
