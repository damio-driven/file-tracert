# TASK — Step 9c: dipendenze tra job, `Blocked(DependencyPending)`, guard di enqueue unificato

> **Branch:** `develop` (nessun branch nuovo) · **Base:** il commit finale dello **step 9b**.
> **Prerequisito obbligatorio:** 9b (overlay `Pending*` scritto all'enqueue). Senza proiezione,
> «dipende da un'entità pendente» non è nemmeno esprimibile. **Non iniziare prima che 9b sia
> chiuso e verde.**
> **Chiude:** la seconda metà del finding 9 e l'intero **WP4** del `CODE-REVIEW-HANDOFF.md`
> (finding 8 + C26 + K5).
> **Questo file È il piano.** Implementare per commit, nell'ordine dato.
> **Sessione dedicata.** `QueueService`, `JobExecutionEngine`, `BlockedJobRevaluator`,
> `SpaceLedger` sono file caldi: agente unico, in sequenza, niente parallelo.
> **Le decisioni tecniche qui sotto sono già prese** (regola CLAUDE.md del 2026-08-11):
> implementarle, non richiederne conferma; se una si rivela sbagliata, cambiarla e
> **documentare il perché** nel commit.

---

## 1. Il problema, in una schermata

Tre buchi, stesso punto di codice (`FileTracert.Business/Operations/QueueService.cs`):

1. **`DependsOnJobId` non viene mai assegnato.** La colonna esiste (`OperationJob`,
   `OperationJobConfiguration` la mappa con FK Restrict) ed è morta. `JobBlockReason` ha
   `DependencyPending` e `DependencyCancelled`: **irraggiungibili**.
2. **La seconda operazione su un'entità pendente viene respinta con 409**
   (`GuardFileAsync`/`GuardDirectoryAsync` → `EntityAlreadyPendingException` →
   `OperationsController` → `Conflict`). §5 di CLAUDE.md dice l'opposto: *«Una sola operazione
   pendente per entità (MVP): la seconda è `Blocked` finché la prima non si risolve»*, e §4 dice
   *«Non rifiutare mai un job all'enqueue»*.
3. **Il guard ha tre punti ciechi** (finding 8 del handoff, verificato):
   - `GuardFileAsync` matcha solo `i.FileId == fileId` → un'op su cartella (item con
     `FileId = null`) è invisibile: accodo un rename di cartella e poi un move di un file che
     ci sta dentro; il folder-job esegue prima (FIFO), lo snapshot `SourceRelativePath` del
     file-job punta a un path che non esiste più → `FileNotFoundException` → **Failed permanente**
     (il retry non ricalcola lo snapshot).
   - I **target** dei job pendenti non sono mai controllati: un `RenameFolder` sulla cartella
     destinazione di un move pendente fa resuscitare il path vecchio da
     `EnsureTargetDirectory`.
   - I job `CreateFolder` non hanno item → `GuardDirectoryAsync` non li vede affatto.
4. **Corollario C26:** `SequenceOrder = MaxAsync() + 1` è letto **fuori** dalla transazione di
   insert, senza vincolo di unicità → due enqueue concorrenti ottengono lo stesso ordine, e la
   feasibility FIFO (che salta solo `e.SequenceOrder > mine`) si doppia-conta a vicenda.

E il matching di sottoalbero è reimplementato ≥5 volte con semantiche divergenti (K5:
`StartsWith` case-sensitive tradotto in SQL contro `OrdinalIgnoreCase` in memoria;
`ScanPath.IsWithin` esiste e in quei punti non è usato).

---

## 2. Cosa deve fare il rework

### 2.1 Un solo predicato di sovrapposizione

Nuovo componente in `FileTracert.Business/Operations/` (es. `PendingWorkGuard`), unico posto in cui
si risponde a: *«questa nuova operazione tocca qualcosa che un job non terminale sta già toccando?»*.

- **Cosa si confronta:** l'insieme dei path **sorgente e target** del nuovo job contro l'insieme dei
  path **sorgente e target** di ogni job non terminale, per volume. Include:
  - gli `OperationJobItems` (source + target);
  - `OperationJob.TargetRelativePath` — è l'unico path che un `CreateFolder` possiede;
  - il caso file: il *file* è coperto anche quando il conflitto è su una **cartella antenata**.
- **Semantica di sovrapposizione:** due path collidono se sono uguali o se uno è antenato
  dell'altro, **case-insensitive** e **consapevole dei confini di segmento** (`Docs` non collide
  con `Documents`). Esiste già `ScanPath.IsWithin` + `AncestorPaths` in `QueueService`: unificare
  lì, non aggiungere una sesta variante. Il predicato serve in **due forme** — in memoria e
  traducibile in SQL — ed è la sede naturale del fix K5.
- **Volumi diversi = nessuna collisione** su path uguali: lo stesso path relativo su un altro
  volume è un altro posto.

### 2.2 Dalla 409 al `Blocked(DependencyPending)`

Quando il guard trova una sovrapposizione, l'enqueue **non lancia**: crea il job

```
State        = Blocked
BlockReason  = DependencyPending
DependsOnJobId = <id del job in conflitto, il più recente in ordine di coda>
ErrorMessage = testo italiano che nomina l'entità e il job che la tiene
```

**Decisioni prese:**

- **Un solo `DependsOnJobId`**, non una lista: è il vincolo MVP «una op pendente per entità».
  Se i job in conflitto sono più d'uno (può succedere con i sottoalberi), si dipende **dall'ultimo
  in `SequenceOrder`** — quando quello si risolve, gli altri davanti a lui si sono già risolti,
  e comunque la rivalutazione ricontrolla il guard da capo prima di sbloccare (§2.4).
- **Un job `Blocked(DependencyPending)` NON scrive l'overlay** (aggancio previsto in 9b §9): non
  possiede l'entità. Lo scriverà quando si sblocca. `ApplyOverlayAsync` diventa condizionale in
  **un punto solo**.
- **Nessuna riserva di spazio** per un dipendente bloccato? **No — la riserva si fa lo stesso.**
  È lo stesso principio del gate offline (`ApplyOfflineGateAsync`): un job parcheggiato che
  eseguirà comunque deve tenere impegnati i suoi byte, o gli altri job sovra-committano il target.
  Coerenza col codice esistente: `shouldReserve` resta guidato dalla valutazione dello spazio.
- **`EntityAlreadyPendingException`** e il ramo `Conflict` in `OperationsController` diventano
  codice morto: **rimuoverli** (niente eccezioni tenute in vita «per sicurezza»). Lato frontend,
  `shared/.../operation-error.ts` ha due rami dedicati al 409 con `entityType`: vanno tolti, e la
  UI deve invece **mostrare bene** un job accodato-ma-bloccato (§2.6).

### 2.3 Esecuzione: l'ordine delle dipendenze si rispetta

- `Blocked` non è in `JobStates.Runnable`, quindi il processor non lo pesca: la barriera di base
  c'è già gratis.
- Aggiungere comunque una **guardia in esecuzione** in `JobExecutionEngine.ExecuteJobAsync`: se
  `DependsOnJobId` esiste e quel job non è `Completed`, il job torna
  `Blocked(DependencyPending)` invece di partire. È la rete contro un `Retry` manuale o una
  rivalutazione andata storta — un dipendente eseguito fuori ordine corrompe file veri.
- **Ricalcolo degli snapshot allo sblocco** (è il fix vero del finding 8a): quando un dipendente
  passa da `Blocked` a `Pending`, i suoi `OperationJobItems.SourceRelativePath` /
  `TargetRelativePath` sono snapshot presi **prima** che il prerequisito girasse e possono essere
  morti. Allo sblocco vanno **ricalcolati** dallo stato corrente (posizione fisica + overlay) delle
  entità referenziate — `FileId` sopravvive ai re-scan dallo step 9a, quindi la riga si ritrova.
  Un item il cui `FileId` non è più risolvibile fa finire il job `Blocked` con messaggio esplicito,
  **mai** `Failed` silenzioso.

### 2.4 Rivalutazione (`BlockedJobRevaluator`)

Oggi `RevaluableReasons` copre `InsufficientSpace`, `TargetVolumeOffline`, `SourceVolumeOffline`.
Aggiungere `DependencyPending` e `DependencyCancelled`, con questa logica:

| Stato del job prerequisito | Effetto sul dipendente |
|---|---|
| `Completed` | ri-esegue il guard: se non c'è più sovrapposizione → `Pending` + **scrittura dell'overlay** + ricalcolo degli snapshot (§2.3). Se c'è ancora un altro job in mezzo → resta `Blocked(DependencyPending)` con `DependsOnJobId` **ripuntato** su quello. |
| `Cancelled` o `Failed` | → `Blocked(DependencyCancelled)`. **Mai** cancellazione a cascata: §5 dice che i dipendenti restano in coda, riattivabili. |
| non terminale | nessun cambiamento. |

- Un `DependencyCancelled` è **riattivabile**: la rivalutazione lo riporta a `Pending` se il guard
  è pulito (es. l'utente ha ricreato la cartella prerequisito, o ha cancellato l'altro job). È il
  motivo per cui è `Blocked` e non `Failed`.
- Le due gate esistenti (offline, poi spazio hard) restano **prima**: un dipendente sbloccato ma
  con volume staccato deve restare bloccato sul motivo giusto.
- **Trigger:** `CancelAsync` deve rivalutare (oggi rilascia il ledger ma non segnala né rivaluta —
  finding 13, già parzialmente chiuso al WP2: verificare sull'HEAD com'è messo e agganciarsi lì,
  non aggiungere un secondo percorso).
- **Ordine FIFO**: la rivalutazione cicla già in `SequenceOrder`; un dipendente sbloccato rientra
  nella domanda del ledger che la feasibility del successivo deve vedere. Non rompere quella
  proprietà.

### 2.5 `SequenceOrder` transazionale (C26)

`MaxAsync() + 1` va **dentro** la transazione dell'insert, e va aggiunto un **indice unico** su
`SequenceOrder` (+ migration) così che due enqueue concorrenti non possano condividere l'ordine:
in caso di violazione, ritentare l'assegnazione (retry corto e limitato, con log — non un ciclo
infinito). La feasibility FIFO dipende dall'unicità di quel numero: senza vincolo è una
convenzione, non un invariante.

### 2.6 Frontend

- Coda (`features/queue/`): la colonna motivo mostra `DependencyPending` /`DependencyCancelled`
  in italiano (*«in attesa dell'operazione #N»*, *«l'operazione da cui dipendeva è stata
  annullata»*), con **link alla riga del job prerequisito**. Il `blockReason` è già tipizzato in
  `core/models/catalog.models.ts` — i due valori esistono già nel tipo TS.
- Picker (`shared/components/operation-picker/`): l'enqueue non torna più 409. La conferma deve
  dire chiaramente che l'operazione **è stata accodata ma è in attesa**, non far credere a un
  fallimento. Rimuovere i rami 409 di `operation-error.ts`.
- ⚠️ **Usare la skill `impeccable`** per tutto il lavoro UI (CLAUDE.md §2/§8).

---

## 3. Commit previsti

1. **`refactor(business)`** — `PendingWorkGuard`: predicato di sovrapposizione unico
   (case-insensitive, segment-aware, source+target, `CreateFolder` incluso), in memoria e in SQL;
   `QueueService`/`JobExecutionEngine` smettono di reimplementarlo (K5). Solo unificazione, nessun
   cambio di comportamento visibile: i test esistenti restano verdi.
2. **`feat(queue)`** — `SequenceOrder` assegnato in transazione + indice unico + migration (C26).
3. **`feat(queue)`** — enqueue: sovrapposizione → `Blocked(DependencyPending)` con
   `DependsOnJobId`, overlay **non** scritto; rimozione di `EntityAlreadyPendingException` e del
   ramo 409.
4. **`feat(queue)`** — rivalutazione delle dipendenze: `DependencyPending`/`DependencyCancelled`
   nel `BlockedJobRevaluator`, ripuntamento di `DependsOnJobId`, scrittura dell'overlay allo
   sblocco, ricalcolo degli snapshot degli item.
5. **`feat(queue)`** — cancel di un prerequisito → dipendenti `Blocked(DependencyCancelled)` nella
   **stessa transazione** del cancel; guardia di dipendenza in `ExecuteJobAsync`.
6. **`feat(frontend)`** — motivi di blocco e link al prerequisito in Coda; picker senza 409; test
   Vitest.
7. **`test(harness)`** — scenario sul ferro (§5).
8. **`docs`** — CLAUDE.md: chiudere la voce di roadmap dello step 9 e il WP4 nel
   `CODE-REVIEW-HANDOFF.md`; annotare cosa resta (chaining → fase 2).

---

## 4. Test (RED prima del GREEN, contro SQLite vero)

In `tests/FileTracert.Tests`, engine + ledger + SQLite veri:

1. **Seconda op sulla stessa entità → accodata `Blocked(DependencyPending)`**, non 409, con
   `DependsOnJobId` corretto. *(Oggi RED: 409.)*
2. **Overlay non rubato**: l'entità conserva l'overlay del **primo** job; il secondo non lo tocca.
3. **Sblocco a catena**: il primo job completa → il secondo passa a `Pending`, scrive il proprio
   overlay ed esegue correttamente.
4. **Snapshot ricalcolato**: rename di cartella + move di un file che ci sta dentro; il folder-job
   esegue per primo → il file-job **non** fallisce con `FileNotFoundException` (è il finding 8a,
   oggi RED).
5. **Op su cartella vs op su file discendente**: la sovrapposizione è rilevata in **entrambe** le
   direzioni (antenato e discendente).
6. **Conflitto sul target**: `RenameFolder` sulla cartella destinazione di un move pendente →
   `Blocked`, non un albero resuscitato.
7. **`CreateFolder` visibile al guard**: un `RenameFolder` sul path di un `CreateFolder` pendente
   viene bloccato.
8. **Case-insensitive**: `Foto\a.jpg` e `foto\A.JPG` sono la stessa entità per il guard.
9. **Cancel del prerequisito** → dipendenti `Blocked(DependencyCancelled)`, **non** cancellati,
   e riattivabili quando la condizione si risolve.
10. **Prerequisito `Failed`** → stesso trattamento del cancel.
11. **Dipendente non eseguibile fuori ordine**: forzare `Pending` su un dipendente con
    prerequisito non completato → l'engine lo riporta `Blocked` senza toccare file.
12. **`SequenceOrder` unico** sotto due enqueue concorrenti.
13. **Ledger**: un dipendente bloccato mantiene la sua riserva; il rilascio avviene solo su stato
    terminale (non regressione dei fix WP1).

Adeguare — **non aggirare** — i test che oggi asseriscono il 409
(`operation-picker.spec.ts`, i test di `QueueService`, `operationErrorMessage`).

---

## 5. Harness (obbligatorio, CLAUDE.md «Test»)

Nuovo scenario in `FileTracert.HardwareSmoke`, es. `job-dependencies`:

1. arrange su `D:\Collaudo\A`, indicizza;
2. accoda `CreateFolder X`, poi `MoveFile` dentro `X`, poi una seconda op sulla **stessa** entità;
3. assert prima dell'esecuzione: la terza è `Blocked(DependencyPending)` con `DependsOnJobId`
   giusto, e l'overlay appartiene al secondo job;
4. lascia girare la coda: assert che l'ordine di esecuzione è quello delle dipendenze e che a fine
   corsa **tutti** i job sono `Completed`, i file sono fisicamente dove devono, gli overlay azzerati;
5. secondo giro: accoda una coppia prerequisito+dipendente, **cancella il prerequisito**, assert
   `DependencyCancelled` sul dipendente e nessuna cascata di cancellazioni.

PASS obbligatorio sul ferro configurato. `E:\Collaudo\B` **non esiste più**: coppia *intra*,
sufficiente. Rimettere `appsettings.json` dell'harness a `Enabled: false` a fine sessione.

---

## 6. Criteri di accettazione

1. Nessun enqueue viene mai **rifiutato** per un conflitto con un altro job: viene accodato
   `Blocked` con il motivo e il prerequisito espliciti (§4 di CLAUDE.md).
2. Il guard vede source **e** target di tutti i job non terminali, `CreateFolder` compresi, con
   matching di sottoalbero case-insensitive e segment-aware, scritto **una volta sola**.
3. Un dipendente sbloccato esegue con snapshot **freschi** e non fallisce per path morti.
4. Cancellare un prerequisito lascia i dipendenti in coda come `DependencyCancelled`, riattivabili.
5. Un job non può eseguire prima del proprio prerequisito, nemmeno forzandolo.
6. `SequenceOrder` è unico e assegnato in transazione.
7. Suite verde (xUnit + Vitest), build backend pulita (warnings-as-errors), scenario harness PASS
   sul ferro.

## 7. Code review finale (obbligatoria)

Review indipendente: correttezza vs criteri e scenari di fallimento; no silent catch (§9);
layering (§3); **no duplicazione** — è metà del senso di questo task, quindi verificare
esplicitamente che il predicato di sovrapposizione non sia rimasto in più copie; test reali
RED→GREEN; idempotenza e crash-safety di ogni transizione toccata (dipendenza + overlay + ledger
devono commitare insieme). Riportare cosa è stato trovato e corretto, o perché un rilievo è stato
lasciato consapevolmente.

## 8. Fuori scope

**Chaining di più operazioni sulla stessa entità** (la proiezione che riflette il netto di più op
concatenate) → **fase 2**, esplicitamente. Qui la seconda op resta bloccata, non si compone.
Scheduling intelligente/riordino della coda → fase 2. Device watcher e SignalR real-time →
**step 10**. Probe live dello spazio + margine (finding 10) → WP6. Se un fix ne richiede un pezzo,
fare il minimo indispensabile e **segnalarlo**.
