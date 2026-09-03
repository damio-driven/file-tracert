# HANDOFF — dopo lo step 18 (2026-09-03)

> Scritto per essere letto **da una sessione pulita**, insieme a `CLAUDE.md`. Sostituisce
> `HANDOFF-post-step16.md`. Dice **dove siamo**, **cosa resta**, e **cosa richiede l'elevazione**.

---

## Stato

**HEAD su `develop`, albero pulito. Gli step 17 e 18 sono chiusi in-process; nessuno dei due è
distribuito.**

| | |
|---|---|
| xUnit | **959 verdi** (baseline del giorno: 949) |
| Vitest | **268** (256 + 12 dello step 17) |
| Build | pulita, warnings-as-errors; `ng build` ok (i 4 warning di budget SCSS pre-esistenti) |
| E2E | **25/25**, tre passate oggi (la prima dal 21/08), non elevato |
| Harness | **non eseguito oggi**: lo step 17 non tocca file; lo step 18 aggiunge un tick a `usn-hidden-subtree`, che gira solo elevato |
| Servizio installato | gira **step 16**; `develop` è avanti di due step, **una migration** (`AddDirectoryExcludedByScan`) |

**Fatto oggi**, in ordine: la passata E2E (23/25 → un selettore del 12b → 25/25) · **step 17**
paging dove §7 lo prometteva (sottocartelle del Catalogo, picker, albero del Setup; review: 2 MAJOR
presi) · **step 18** l'esclusione che si eredita alle cartelle (`Directories.ExcludedByScan`,
effettiva; review: 1 MAJOR preso). Dettagli, misure e limiti nei paragrafi «Fatto nello step 17/18»
di `CLAUDE.md`.

**Decisioni di prodotto prese oggi** (registrate in cima alla roadmap di `CLAUDE.md`):
- **A4 → motore ibrido**: enumerazione dei root per la prima scansione, cursore USN preso **prima**
  della camminata, delta da lì. **Da fare.**
- **Esclusione ereditata alle cartelle** → fatta (18).

---

## Cosa resta, in ordine di valore

### 1. Sessione elevata (corta)
- **Harness** completo: 59 scenari, con il secondo tick di `usn-hidden-subtree` che oggi fa SKIP
  non elevato. Procedura in memoria (`harness-collaudo-procedure`), `appsettings.json` da rimettere
  byte-identico (sha256 `653f5990…`).
- **Deploy di 17 + 18**: una migration additiva (`Directories.ExcludedByScan`, default 0, nessun
  backfill). Ricetta in memoria (`filetracert-deployment-state`): `GET /api/dashboard` a 0 job,
  publish col servizio su, `sc stop`, backup `filetracert.db.pre18` + sha256, `install-service.ps1`.
  Dopo: le cartelle nascoste già a catalogo hanno la colonna a 0 finché una **scansione completa**
  non le guarda — su `D:` (l'unico volume con l'incrementale) conviene una ri-scansione esplicita.
- Eventuale **P1** (hard link, misura sull'MFT).

### 2. A4 — il motore ibrido (decisione presa, lavoro da fare)
`ScanService` sul motore USN cammina tutta l'MFT anche per tre root piccoli (14d). Il disegno
deciso: prima scansione per **enumerazione** dei root, ma `FSCTL_QUERY_USN_JOURNAL` **prima** della
camminata e cursore (`LastUsn` + `UsnJournalId`) scritto a fine scan, così l'incrementale parte lo
stesso e ciò che cambia durante la camminata lo riporta il primo delta (merge idempotente). Vincoli:
richiede l'elevazione per il giornale (senza, cursore nullo come oggi); in-process si prova con
`ScriptedUsnReader`; sul ferro con l'harness elevato. Attenzione al pre-filtro di `UsnSyncWorker`
(`UsnJournalId != null`).

### 3. Piccoli, indipendenti, non elevati
- **2a — `InSubtree`**: misurare con `EXPLAIN` se è l'`ESCAPE` di EF (non il parametro) a impedire
  la seek su `IX_Directories_MaterializedPath`; se sì, predicato di range su colonna `NOCASE`, con
  equivalenza (P2, ASCII fold) e piano pinnato. Sette call site.
- **2b — C32**: `EnqueueBatchAsync` mappa senza `Include` dei volumi (`QueueService.cs`, il return
  di `created`); due righe + test.
- **2c — stesso-tick** (residuo 3 del 16): `Classify` scarta per path (`RemoveAll`) ciò che
  andrebbe portato avanti per FRN.
- **2d — A5 diagnostica Cloud**: log dei segnali grezzi in `VolumeClassifier`, poi il log vivo.
- **2e — igiene `%TEMP%`**: `StopAsync` sul factory.

### 4. Decisioni di prodotto ferme
- **Filtro dimensione in Ricerca** (esiste nell'API e nello store, non a video).
- **Il verso opposto dell'esclusione** (cartella che smette di essere nascosta): costa una
  scansione; se un giorno si vorrà chiuderlo, serve che il record della cartella faccia rileggere
  i figli, cioè una camminata mirata del sottoalbero.

---

## Il vincolo dell'elevazione (invariato)
Gli E2E si **rifiutano** di partire da elevato (12a); l'harness sul percorso USN e il deploy la
**pretendono**. Un subagent **non** eredita l'elevazione. Quindi: sessione non elevata per E2E e
lavori in-process, sessione elevata per harness/deploy/A4 sul giornale.

## Regole operative apprese oggi (in memoria)
- **Un solo `dotnet` alla volta**; un `dotnet build` di un altro progetto è lavoro dell'utente.
- **Una mutazione si prova sulla solution compilata**: build di un solo progetto + `--no-build`
  prova la DLL vecchia (falso verde, visto).
- **Ripristino per sostituzione inversa, mai `git checkout -- file`** (ha cancellato lavoro non
  committato, una volta).
- **Gli heredoc bash dimezzano i backslash**: script con path Windows o stringhe verbatim C# via
  file (Write tool).
- **E2E da Claude Code**: `Start-Process cmd /c npm test` con finestra nascosta (ha una console);
  attesa con un until-loop in background.
- **Record del secondo tick con `Usn` oltre il cursore** nel reader finto, altrimenti `UpToDate`.
