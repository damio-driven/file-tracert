# TASK — Step 11e: efficienza (WP9)

> **Sessione dedicata, agente singolo.** Quinto dei sei task dei WP minori
> (`TASK-step11-overview.md`). **Prerequisito rigido: 11a mergiato** — E4 riscrive gli
> stessi metodi di `IndexUpdater` che 11a corregge (finding 6, C19).
> Riferimenti: `CLAUDE.md` §3 (persistenza ibrida, SQLite dietro `IFileSearchIndex` /
> `IBulkIndexWriter`), §6 (indici), §7 (paging server-side); `CODE-REVIEW-HANDOFF.md` →
> E1, E3, E4, E5, E6, E7, E8.
> ⚠️ Tocca `QueueService` e `IndexUpdater`: **niente parallelo** su quei file.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

Radice comune: **SQLite è single-writer**. Ogni statement in più su un percorso caldo non
è "un po' più lento": è tempo in cui nessun altro scrive, incluso lo scan e la coda. Dopo
lo step 10c la UI **non polla più** (E1 non è più moltiplicato per un poll da 2,5 s), ma i
costi restano su ogni load e su ogni rilettura dopo riconnessione.

Stato verificato su `88571aa` (riverificare le righe):

| # | Dove | Spreco | Direzione |
|---|------|--------|-----------|
| E1 | `QueueService.ListAsync:425-433` — `.Include(j => j.Items)` su una pagina di job, per usare poi `items.First()?.SourceRelativePath` in `MapToDto` | Un `MoveFolder` cross-volume da 100k file materializza 100k entità per mostrare **un** path | Proiettare in SQL il primo path (e i contatori item se servono al DTO), niente `Include` degli item nella lista |
| E3 | `Data/Search/FileSearchIndex.cs:~178` — `SELECT MIN(COUNT(*), 10000)` | Il `MIN` non limita **il lavoro**: il COUNT visita ogni match FTS più i join per riga | `SELECT COUNT(*) FROM (SELECT 1 … LIMIT 10000)` |
| E4 | `IndexUpdater` ~:205 e ~:278/330 — upsert FTS **per file** (DELETE+INSERT, due statement in autocommit ciascuno) | Rename/move di cartella da 50k file = 100k commit WAL | Set-based `INSERT…SELECT`: il pattern esiste già in `FileSearchIndex.SyncVolumeFromDbAsync`. Dietro `IFileSearchIndex` (§3), non con SQL sparso in `Business` |
| E5 | `Host/Controllers/CatalogController.cs:~82` e `~124` | Due subquery COUNT correlate per sottocartella, lista sottocartelle **non paginata**, l'indice `IX_Files_DirectoryId` non copre i flag usati nei predicati | Covering index `(DirectoryId, IsIncluded, IsPresent)` o un count raggruppato unico; valutare il paging delle sottocartelle (§7 dice paging **ovunque**) |
| E6 | `Host/Controllers/DashboardController.cs:27-31` — `LongCountAsync` + `SumAsync` | Due full scan sequenziali della tabella `Files`, più due count sui volumi | Aggregato single-pass. **Attenzione**: 11d aggiunge qui i contatori di coda — se 11d è già passato, integra invece di duplicare |
| E7 | `ScanService.GatherAndFilter:~231` + il matching del root per item | Catena LINQ e ordinamento delle chiavi ricostruiti **per ogni item enumerato** (milioni di allocazioni su un volume grosso) | Pre-ordinare i root **una volta** fuori dal loop, poi `foreach` con first-match |
| E8 | `BlockedJobRevaluator` ~:220-226 — `SaveChanges` + `ReleaseAsync` + `ReserveAsync`, ciascuno con il proprio scope/transazione | Tre transazioni di scrittura per ogni job sbloccato | Una sola (attenzione: il rilascio ledger nella **stessa transazione** del cambio di stato è una regola di crash-safety già acquisita in WP1 — muoverlo non deve romperla) |

## Vincoli che nessuna ottimizzazione può violare

- **Correttezza prima**: se un'ottimizzazione cambia un risultato, non è un'ottimizzazione.
  Ogni fix va accompagnato da un test che dimostra **stesso output**, meno lavoro.
- **SQLite-specifics dietro le interfacce** (§3): il SQL set-based di E4 sta in
  `Data`/`FileSearchIndex`, non in `Business`.
- **Crash-safety** (E8): il ledger e lo stato terminale restano nella stessa transazione.
  Se accorpare le tre transazioni obbliga a spostare il rilascio **fuori**, non farlo:
  documenta perché E8 resta aperto invece di introdurre riserve fantasma (finding 5).
- **Niente misure a memoria**: ogni claim di miglioramento va con un numero misurato
  (harness sul ferro o cronometro nel test), non con un «dovrebbe essere più veloce».

## Lavoro

Ordine consigliato, dal più isolato al più intrecciato: **E3 → E7 → E5 → E6 → E1 → E4 → E8**.

E4 ed E8 sono i due che toccano percorsi caldi con semantica delicata (FTS e ledger):
affrontali per ultimi, quando il resto è verde, così un eventuale rollback è chirurgico.

Per E5: prima **misurare** quale dei due difetti pesa (le subquery o l'indice mancante).
Aggiungere un indice è una migration e un costo di scrittura su ogni insert dello scan:
va giustificato con il numero, non con l'intuizione.

## Split dei commit (indicativo)

Un commit per finding, con il numero misurato nel messaggio:

1. `perf(data): the search count stops at the cap`
2. `perf(scan): sort the watched roots once, not per item`
3. `perf(host): one grouped count for catalog children`
4. `perf(host): single-pass dashboard aggregate`
5. `perf(business): the queue list stops materializing every item`
6. `perf(data): set-based FTS upsert for folder-wide renames`
7. `perf(business): one transaction to unblock a job`

## Test (RED prima del GREEN)

Qui il RED non è «fallisce», è «costa». Due livelli:

- **Correttezza (obbligatorio)**: per ogni fix un test che confronta il risultato prima/dopo
  su dati veri (SQLite vero). Per E4 in particolare: rename di cartella con N file → FTS
  contiene esattamente le stesse righe, con il **nome proiettato** (§5 — 9b: la colonna
  `name` è `COALESCE(NULLIF(PendingName,''), Name)`, una costante condivisa: non
  reimplementarla nel nuovo SQL, riusala).
- **Costo (dove misurabile in modo stabile)**: contare gli **statement** o le entità
  materializzate, non i millisecondi (i tempi in test sono rumorosi). Es. E1: un job con
  1 000 item → la lista non deve caricare 1 000 entità.

## Harness sul ferro (obbligatorio)

Far girare la suite già configurata e riportare i **tempi di scan** (primo scan e re-scan
su `D:\Collaudo\A`, ~2 000 file) confrontandoli con l'ultima misura registrata in
`CLAUDE.md` (step 10a: primo scan 1,08 s, re-scan 0,59 s). E7 dovrebbe muovere questo
numero; se non lo muove, dirlo. Rimettere `appsettings.json` come stava.

## Definition of done

- xUnit verde, build backend pulita (warnings-as-errors); `ng build` solo se hai toccato
  il frontend (non dovresti).
- Numeri misurati riportati per ciascun fix; i finding che restano aperti (es. E8 se la
  crash-safety lo vieta) **elencati con il motivo**.
- **Code review finale** indipendente: correttezza invariata, layering (§3), nessuna
  regressione di crash-safety, niente SQL duplicato fuori dalle interfacce.
- `CLAUDE.md`: paragrafo «Fatto nello step 11e» con le misure; in `CODE-REVIEW-HANDOFF.md`
  marcare E1/E3/E4/E5/E6/E7/E8 (chiusi o motivatamente aperti). E2 è già chiuso dallo step 9a.
