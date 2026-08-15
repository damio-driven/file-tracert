# TASK — Step 9a: re-scan che preserva l'overlay, in transazioni corte

> **Branch:** `develop` (nessun branch nuovo) · **Base:** `cec1cd1`
> **Prerequisito di:** 9b (overlay `Pending*`) e 9c (dipendenze tra job).
> **Chiude:** i due debiti §11 «Re-scan idempotente vs proiezione» e «contesa di lock».
> **Questo file È il piano.** Implementare per commit, nell'ordine dato.
> **Sessione dedicata:** è il punto di codice più caldo del progetto (indicizzazione).
> Fermarsi ai checkpoint indicati invece di tirare dritto.

---

## 1. Il problema, in una schermata

`ScanService.PersistAsync` (`src/backend/FileTracert.Business/Scanning/ScanService.cs:355-412`,
chiamato da `:159`) fa, dentro **una sola transazione**:

```
BeginTransaction
PRAGMA defer_foreign_keys=ON                       (:366)
_ftsIndex.ClearVolumeAsync(volume.Id)              (:370)
Files.Where(VolumeId == v).ExecuteDeleteAsync()    (:373)   ← truncate per volume
Directories.Where(VolumeId == v).ExecuteDeleteAsync()(:374)  ← truncate per volume
Directories.AddRange(...) + SaveChanges            (:380-381) ← riga per riga (E2)
_bulkWriter.BulkInsertFilesAsync(...)              (:401)
_ftsIndex.SyncVolumeFromDbAsync(volume.Id)         (:404)
volume.LastFullScanUtc / LastUsn + SaveChanges     (:406-410)
Commit
```

Due conseguenze, **stesso punto di codice**:

1. **L'overlay muore a ogni re-scan.** Il truncate cancella le righe `Files`/`Directories`
   con i campi `Pending*`. Finché non è risolto, scrivere l'overlay (9b) è inutile.
   Cancella anche le **identità**: `Files.Id` cambia a ogni scan, mentre
   `OperationJobItems.FileId` le referenzia.
2. **Il write-lock unico di SQLite resta preso per minuti** su un volume grosso (C:),
   quindi `VolumeSyncWorker`, la coda e le API prendono `SQLITE_BUSY`
   («database is locked»). Oggi è solo **tamponato** da `WalCheckpointWorker`
   (`FileTracert.Host/Workers/WalCheckpointWorker.cs`) + `busy_timeout`: cerotto, non cura.

---

## 2. Cosa deve fare il rework

**Merge, non replace.** Per ogni volume, riconciliare ciò che lo scan ha visto con
ciò che è già in catalogo:

| Caso | Azione |
|---|---|
| Riga esistente ritrovata | **UPDATE** dei soli campi fisici (size, timestamp, attributi, `Extension`, `Category`, `LastIndexedUtc`, `IsPresent=true`). **Mai** toccare `Pending*`, `Id`, `IsIncluded` deciso dal filtro, `Hash`/`QuickHash`. |
| Riga nuova | INSERT (bulk). |
| Riga in catalogo non più vista sul disco | `IsPresent = false` (soft). **Mai** delete: §6 e la policy no-hard-delete. |
| Riga con overlay (`PendingState != None`) non vista | `IsPresent=false` ma **riga e overlay intatti**: il job pendente la referenzia ancora. |

**Chiave di matching** (in quest'ordine):
1. `UsnFileRef` quando il motore è USN e il valore è presente — è l'identità vera del file;
2. altrimenti path relativo proiettato *fisico* (`MaterializedPath` + `Name`), confronto
   **case-insensitive** (Windows non distingue; la collation SQLite di default è BINARY,
   vedi P2 in `CODE-REVIEW-HANDOFF.md`).

**Transazioni corte.** Commit **per batch** (dimensione configurabile, default suggerito
5 000 file), così il write-lock si rilascia tra un blocco e l'altro. Conseguenze da gestire:

- `volume.LastFullScanUtc` / `LastUsn` si scrivono **solo alla fine**, in una transazione
  propria: un crash a metà non deve far credere che lo scan sia completo (altrimenti
  l'incrementale USN riparte da un checkpoint che copre righe mai scritte).
- Il merge deve essere **idempotente e ripartibile**: un re-scan dopo un crash converge.
  È il vantaggio del merge — non c'è nessuno stato «mezzo truncato» da riparare.
- Il passaggio `IsPresent=false` per gli assenti va fatto **alla fine**, sulle righe
  `LastIndexedUtc < inizio_scan` di quel volume (marcatore di generazione): non si può
  decidere "assente" finché tutti i batch non sono passati.

---

## 3. Dove vive cosa (§3, layering)

- La **strategia** di merge (chi è nuovo, chi va aggiornato, chi è sparito) sta in
  `Business/Scanning`.
- Il **come** SQLite-specifico (tabella di staging, `INSERT … ON CONFLICT`, join set-based)
  sta dietro `IBulkIndexWriter` in `FileTracert.Data/Indexing`.
  ⚠️ **Discrepanza nota**: `IBulkIndexWriter` e `IFileSearchIndex` oggi stanno in
  `FileTracert.Data/Indexing` e `FileTracert.Contracts/Search`; CLAUDE.md §3 li dà entrambi
  in `Contracts`. **Non spostarli qui** (scope creep): segnalarlo e basta.
- `BulkInsertDirectoriesAsync` **esiste ed è mai chiamato** (E2): usarlo, invece di
  `AddRange` + `SaveChanges` riga per riga.

**Approccio deciso: staging table.** Una tabella temporanea per volume, riempita in bulk
con ciò che lo scan ha visto, poi UPDATE/INSERT set-based per join. Tiene la memoria
limitata (niente dizionario da milioni di righe in RAM su C:) e resta interamente dentro
`BulkIndexWriter`, dove le SQLite-specifics devono stare (§3). Il dizionario in memoria è
**scartato**: non ripresentarlo in implementazione.

Dettagli da risolvere scrivendo il codice, non prima:
- tabella `TEMP` (per-connessione) contro tabella reale con colonna `VolumeId` e cleanup:
  con i commit per batch la connessione resta la stessa, ma verificarlo invece di assumerlo;
- indice sulla staging per le due chiavi di join (`UsnFileRef`, path normalizzato), altrimenti
  il merge diventa un nested-loop su milioni di righe;
- il confronto case-insensitive sul path va reso **traducibile in SQL** (colonna di join già
  normalizzata a lower in staging e in confronto, oppure `COLLATE NOCASE`): non fare il
  matching in memoria, è esattamente lo scivolone di K5.

---

## 4. Schema: `IsPresent` su `DirectoryNode` (deciso, approvato 2026-08-11)

`FileEntry` ha `IsPresent`; **`DirectoryNode` no** (campi verificati:
`Id`, `VolumeId`, `ParentId`, `Name`, `MaterializedPath`, `UsnFileRef`, `IsMaterialized`,
`Pending*`, audit). Senza un flag equivalente, una cartella sparita dal disco può solo
essere cancellata — e cancellarla porta via l'overlay dei figli.

**Deciso:** aggiungere `IsPresent` a `DirectoryNode` (+ migration + configuration), stessa
semantica di `FileEntry` (default `true`, backfill a `true` per le righe esistenti). Serve
anche a chiudere il residuo del finding 15 (sottoalbero fantasma dopo un MoveFolder
cross-volume) in modo coerente con la policy no-hard-delete.

Da propagare dove oggi si guarda solo l'esistenza della riga: navigazione del Catalogo
(`CatalogController`), risoluzione dei target all'enqueue, e il conteggio delle subdir.
Una cartella `IsPresent=false` **senza** overlay non va mostrata come navigabile; una con
overlay sì (è il caso «cartella accodata»). Aggiornare anche §6 di CLAUDE.md con il campo
nuovo, altrimenti lo schema documentato e quello reale divergono.

---

## 5. Commit previsti

1. **`feat(data)`** — `IsPresent` su `DirectoryNode` + migration + configuration
   (solo se la decisione §4 è confermata).
2. **`feat(data)`** — API di merge su `IBulkIndexWriter` (staging + upsert per batch +
   marcatura degli assenti), con i suoi test contro SQLite vero.
3. **`refactor(scan)`** — `PersistAsync` → merge per batch con commit corti; checkpoint
   `LastFullScanUtc`/`LastUsn` solo a fine scan; directory via
   `BulkInsertDirectoriesAsync`.
4. **`fix(scan)`** — FTS aggiornata per batch invece di `ClearVolume` + `SyncVolumeFromDb`
   finale (si collega a E4: oggi l'upsert per-file sono 2 statement in autocommit).
5. **`test(harness)`** — scenario di re-scan sul ferro (vedi §7).
6. **`docs`** — chiudere le due voci §11 in `CLAUDE.md` con quello che è stato fatto.

Se un commit diventa troppo grosso, spezzarlo: la regola è **un commit per preoccupazione**.

---

## 6. Test (RED prima del GREEN, contro SQLite vero)

Nuovi, tutti in `tests/FileTracert.Tests`:

1. **L'overlay sopravvive al re-scan** — file con `PendingName`/`PendingState=PendingRename`/
   `PendingJobId`, poi re-scan dello stesso volume: i campi `Pending*` sono ancora lì.
   *(Oggi RED: il truncate li cancella.)*
2. **L'identità sopravvive al re-scan** — `Files.Id` e `Directories.Id` invariati per le
   righe ritrovate. *(Oggi RED.)* È ciò che protegge `OperationJobItems.FileId`.
3. **File sparito → `IsPresent=false`**, riga presente, `Pending*` intatti.
4. **File riapparso → `IsPresent=true`** senza cambiare `Id`.
5. **Matching per `UsnFileRef`** quando il path è cambiato (file rinominato fuori
   dall'app): stessa riga aggiornata, non una riga nuova + una fantasma.
6. **Matching case-insensitive** sul path quando l'FRN non c'è (motore enumerazione):
   `Foto\a.jpg` e `foto\A.JPG` sono la stessa riga (chiude P2 per questo percorso).
7. **Transazioni corte** — con batch size 1-2, mentre lo scan gira un secondo writer
   (nuovo `DbContext`) scrive una riga e **non** prende `SQLITE_BUSY`. *(Oggi RED: la
   transazione monolitica lo blocca.)*
8. **Crash/cancel a metà** — token cancellato tra due batch: `LastFullScanUtc` **non**
   aggiornato, `LastUsn` invariato, e un secondo scan converge allo stato giusto.
9. **Non regressione filtro** — file esclusi dal filtro restano `IsIncluded=false` e non
   vengono resuscitati dal merge.

Attenzione ai test esistenti che assumono il truncate: `ScanWorkerTests`,
`BulkIndexWriterTests`, gli scenari harness che ri-indicizzano (`CatalogArranger`).
Vanno **adeguati**, non aggirati; se un test verde diventa rosso, capire *perché* prima di
toccarlo.

---

## 7. Harness (obbligatorio, CLAUDE.md «Test»)

Nuovo scenario in `FileTracert.HardwareSmoke`, es. `rescan-preserves-overlay`:
1. arrange su volume reale, indicizza;
2. accoda un'operazione (che oggi non scrive ancora l'overlay: fino a 9b lo scenario
   scrive i campi `Pending*` a mano sulla riga, e lo dichiara nel commento);
3. ri-scansiona lo stesso volume;
4. assert: overlay presente, `Files.Id` invariato, job ancora eseguibile e completabile.

Il PASS va verificato sul ferro configurato. **Nota operativa:** su questa macchina
`E:\Collaudo\B` non esiste più; `D:\Collaudo\A` sì. Con un solo volume si ottiene solo la
coppia *intra*, sufficiente per questo scenario. Ricordarsi di rimettere
`appsettings.json` dell'harness a `Enabled: false` a fine sessione.

---

## 8. Criteri di accettazione

1. Un re-scan **non** cancella righe: aggiorna, inserisce, marca assenti.
2. `Pending*` e le identità (`Id`) sopravvivono a qualunque numero di re-scan.
3. Durante uno scan lungo, gli altri writer scrivono senza `SQLITE_BUSY` (test 7 verde).
4. Un crash a metà scan non lascia il catalogo in uno stato che si dichiara completo.
5. Nessuna regressione di performance percepibile sul primo scan (che resta il caso
   «tabella vuota» → bulk insert puro). Misurare prima/dopo su un volume vero e
   **riportare i numeri**, non «sembra uguale».
6. Suite verde (xUnit + Vitest), build backend pulita, scenario harness PASS.

## 9. Code review finale (obbligatoria)

Come da CLAUDE.md: review indipendente delle modifiche — correttezza vs criteri, no silent
catch (§9), layering (§3: SQLite-specifics dietro `IBulkIndexWriter`), no duplicazione,
test reali RED→GREEN, idempotenza/crash-safety (qui è il cuore del task).

## 10. Fuori scope

Scrittura dell'overlay all'enqueue, `CreateFolder` che crea la riga `Directories`, FTS sul
**nome proiettato**, path proiettato, badge in UI → **9b**. Dipendenze tra job,
`Blocked(DependencyPending)`, guard di enqueue unificato → **9c**. Spostamento di
`ScanPath`/`IBulkIndexWriter` in `Contracts` → cleanup, non qui.
