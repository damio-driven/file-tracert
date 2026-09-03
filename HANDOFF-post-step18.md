# HANDOFF — dopo lo step 18 e il suo deploy (2026-09-03)

> Scritto per essere letto **da una sessione pulita**, insieme a `CLAUDE.md`. Sostituisce
> `HANDOFF-post-step16.md`. Dice **dove siamo**, **cosa resta**, e **cosa richiede l'elevazione**.

---

## Stato

**HEAD su `develop`, albero pulito. Gli step 17 e 18 sono chiusi, firmati sul ferro e
distribuiti. Non resta nulla di non distribuito.**

| | |
|---|---|
| xUnit | **959 verdi** |
| Vitest | **268** |
| Build | pulita, warnings-as-errors; `ng build` ok (i 4 warning di budget SCSS pre-esistenti) |
| E2E | **25/25**, non elevato |
| Harness | **59/59 PASS, 0 FAIL, 0 SKIP**, sessione elevata — `usn-hidden-subtree` col secondo tick passa su tutte e tre le coppie |
| Servizio installato | gira **step 18**; migration `AddDirectoryExcludedByScan` applicata; backup `filetracert.db.pre18` |

**Fatto nella sessione elevata**: harness completo · deploy di 17+18 (publish 10,9 s, stop 0,3 s,
install 4,1 s, nessun rebuild FTS, zero Error, invariante §6 vera su 742 675 righe) · ri-scansione
di `D:` con il meccanismo dello step 18 provato sul servizio installato nel ciclo
nascondi → ri-mostra → cancella · la ricognizione di A4 (sotto). Dettagli nei paragrafi
«Fatto nello step 17/18» e «Deploy di 17 e 18» di `CLAUDE.md`.

---

## Cosa resta, in ordine di valore

### 1. A4 — il motore ibrido (decisione presa; il disegno ha un prerequisito misurato)
La decisione: prima scansione per **enumerazione** dei root, cursore USN preso **prima** della
camminata, delta da lì.

**Ma il disegno preso alla lettera produce un incrementale cieco**, e la sonda del 03/09 lo mostra:
il delta colloca ogni record risalendo dal **FRN del padre** alle righe `Directories.UsnFileRef`, e
quella colonna la scrive **solo** il ramo USN. Con una prima scansione a enumerazione le righe
nascono senza FRN e il delta risponde `status=Applied indexed=0 unresolved=1` — cursore avanzato,
niente indicizzato, nessun errore. Oggi il prodotto è protetto dal gate di
`UsnDeltaApplier.Ineligible` («the last full scan used enumeration, so the directory rows carry no
file references»), e l'ibrido è esattamente ciò che quella riga toglie.

**Quindi l'ordine di lavoro è**:
1. `Platform` — `ScanEntry` guadagna il FRN e `ManagedDirectoryEnumerator` lo legge.
   `GetFileInformationByHandleEx` / `FileIdBothDirectoryInfo` dà nome, attributi, dimensione, date
   **e** FileId per handle di directory: si porta via anche la `FileInfo` per voce di oggi.
   Equivalenza col vecchio enumeratore da provare (attributi, reparse point, cartelle negate).
2. `Business` — separare il motore del **cursore** da quello della **camminata**: oggi
   `Volume.ScanEngine` fa entrambi i mestieri, in `ScanService.EnumerateRaw` e nel gate di
   eligibilità del delta.
3. Convergenza in-process (`ScriptedUsnReader`) + scenario harness elevato.

### 2. Piccoli, indipendenti, non elevati
- **2a — `InSubtree`**: misurare con `EXPLAIN` se è l'`ESCAPE` di EF a impedire la seek su
  `IX_Directories_MaterializedPath`; se sì, predicato di range su colonna `NOCASE`, con equivalenza
  (P2, ASCII fold) e piano pinnato. Sette call site.
- **2b — C32**: `EnqueueBatchAsync` mappa senza `Include` dei volumi; due righe + test.
- **2c — stesso-tick** (residuo 3 del 16): `Classify` scarta per path ciò che andrebbe portato
  avanti per FRN.
- **2d — A5 diagnostica Cloud**: log dei segnali grezzi in `VolumeClassifier`, poi il log vivo.
- **2e — igiene `%TEMP%`**: `StopAsync` sul factory.
- Eventuale **P1** (hard link, misura sull'MFT).

### 3. Decisioni di prodotto ferme
- **Filtro dimensione in Ricerca** (esiste nell'API e nello store, non a video).
- **Il verso opposto dell'esclusione** (cartella che smette di essere nascosta): costa una
  scansione; chiuderlo richiede che il record della cartella faccia rileggere i figli, cioè una
  camminata mirata del sottoalbero.
- **`C:` senza incrementale**: deciso il 27/08, e resta così.

---

## Il vincolo dell'elevazione (invariato)
Gli E2E si **rifiutano** di partire da elevato (12a); l'harness sul percorso USN e il deploy la
**pretendono**. Un subagent **non** eredita l'elevazione. Quindi: sessione non elevata per E2E e
lavori in-process, sessione elevata per harness/deploy/A4 sul giornale.

## Regole operative apprese (in memoria)
- **Un solo `dotnet` alla volta**; un `dotnet build` di un altro progetto è lavoro dell'utente.
- **La solution è `src/backend/FileTracert.slnx`**, non nella root.
- **Una mutazione si prova sulla solution compilata**: build di un solo progetto + `--no-build`
  prova la DLL vecchia (falso verde, visto).
- **Ripristino per sostituzione inversa, mai `git checkout -- file`** (ha cancellato lavoro non
  committato, una volta). L'eccezione è `appsettings.json` dell'harness, che è la procedura.
- **Gli heredoc bash dimezzano i backslash**: script con path Windows o stringhe verbatim C# via
  file (Write tool).
- **E2E da Claude Code**: `Start-Process cmd /c npm test` con finestra nascosta (ha una console).
- **Record del secondo tick con `Usn` oltre il cursore** nel reader finto, altrimenti `UpToDate`.
- **Il token del servizio** si legge dall'HTML **servito** (`http://127.0.0.1:5005/`), non dal file
  su disco: lì c'è ancora il segnaposto `__FT_TOKEN__`.
