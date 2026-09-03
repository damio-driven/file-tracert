# HANDOFF — dopo lo step 19 e la sua firma sul ferro (2026-09-03)

> Scritto per essere letto **da una sessione pulita**, insieme a `CLAUDE.md`. Sostituisce
> `HANDOFF-post-step18.md`. Dice **dove siamo**, **cosa resta**, e **cosa richiede l'elevazione**.

---

## Stato

**HEAD su `develop`, albero pulito. Lo step 19 (motore ibrido, voce A4) è chiuso e firmato sul
ferro. L'unica cosa non distribuita è lo step 19 stesso — e non ha migration.**

| | |
|---|---|
| xUnit | **971 verdi**, passata completa **da elevato** (1 m 37 s) — quindi anche i `NtfsUsnReaderTests`, che senza elevazione ritornano a vuoto |
| Vitest | 268 (invariato: 19 non tocca il frontend) |
| Build | pulita, warnings-as-errors, Debug e Release |
| E2E | 25/25, ultima passata non elevata; **non rieseguiti** dopo 19 (non lo toccano, e la sessione era elevata) |
| Harness | **59/59 PASS, 0 FAIL, 0 SKIP**, due passate identiche, sessione elevata |
| Servizio installato | gira **step 18**; **19 non distribuito** |

### La firma di A4, e come si legge
I due scenari USN escono **PASS su tutte e tre le coppie**, dove il tentativo del giorno prima li
aveva visti **SKIP**. La riga che vale il giro, sei volte nel log (2 scenari × 3 coppie):

```
walked by the Enumeration engine; journal cursor after the scan: usn=126002016 id=134275375767659020
delta: Applied (7 journal record(s)) — indexed=2 absent=1 excluded=0 dirs=0 unplaced=0
```

L'ibrido cammina il **perimetro** per enumerazione **e prende il cursore**; il delta poi **colloca**
i record contro le righe che quella camminata ha scritto (`indexed=2`, `unplaced=0`). È il punto in
cui la sonda pre-19 rispondeva `indexed=0 unresolved=1`.

**Il contrasto va letto con la grafia nuova**: `falling back to enumeration` compare **0 volte**, e
qui significa «il giornale è stato aperto davvero» — non più «il motore scelto è l'USN». Dopo A4 la
camminata per enumerazione **è** la strada giusta quando il root non è la radice del volume. La prova
che l'USN c'è sono il cursore letto e il delta applicato.

`appsettings.json` dell'harness rimesso byte-identico (sha256 `653f5990…`, verificato).

---

## Cosa resta, in ordine di valore

### 1. Distribuire lo step 19 — decisione dell'utente
Nessuna migration: è una copia di file. Procedura nel paragrafo «Deploy di 17 e 18» di `CLAUDE.md`
(publish col servizio attivo → `sc stop` → backup del DB con sha256 → `install-service.ps1`).
Richiede **elevazione**. Da controllare dopo: nessun rebuild FTS, zero Error nel log, invariante §6.

**Cosa cambierebbe davvero sul catalogo dell'utente**: oggi solo `D:` ha il cursore acceso. Con
l'ibrido, una ri-scansione esplicita di un volume i cui root sono **sottoalberi** (cioè `C:`, dove i
root sono tre cartelle) camminerebbe **solo il perimetro** invece di tutta l'MFT — che è il caso che
14d misurava come perdente — **e** lascerebbe il cursore acceso. Cioè la decisione «`C:` senza
incrementale» (27/08) è stata presa contro un costo che lo step 19 ha tolto: vale la pena
riproporla all'utente, non darla per rinnovata.

### 2. Piccoli, indipendenti, non elevati
- **2a — `InSubtree`**: misurare con `EXPLAIN` se è l'`ESCAPE` di EF a impedire la seek su
  `IX_Directories_MaterializedPath`; se sì, predicato di range su colonna `NOCASE`, con equivalenza
  (P2, ASCII fold) e piano pinnato. Sette call site. **31 ms per directory esclusa** sul volume di
  sistema, misurati al deploy di 16.
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
- **`C:` senza incrementale**: deciso il 27/08 — ma vedi il punto 1, la premessa è cambiata.

---

## Il vincolo dell'elevazione (invariato)
Gli E2E si **rifiutano** di partire da elevato (12a); l'harness sul percorso USN e il deploy la
**pretendono**. Un subagent **non** eredita l'elevazione. Quindi: sessione non elevata per E2E e
lavori in-process, sessione elevata per harness e deploy.

## Regole operative apprese (in memoria)
- **Prima di misurare, controllare la macchina**: `cmd /c exit` deve costare ~25 ms (66 ms
  attraverso bash è sano). Se costa secondi, guardare i processi `dotnet` orfani da build
  interrotte — 13 vivi, una volta, e lo spawn era a 3 000 ms.
- **Un solo `dotnet` alla volta**; un `dotnet build` di un altro progetto è lavoro dell'utente.
- **La solution è `src/backend/FileTracert.slnx`**, non nella root.
- **L'harness legge la sua `appsettings.json` dalla `bin`**: dopo averla modificata serve un
  `dotnet build` del progetto, altrimenti parte con la config vecchia e dice «Nothing to do».
- **Ripristino della config dell'harness: attenzione agli EOL.** Il file è **CRLF**; il tool Write
  produce LF e il sha256 non torna pur essendo il contenuto identico. `sed -i 's/$/\r/'` lo
  riporta a `653f5990…`.
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
