# HANDOFF — dopo lo step 20 e il deploy di 19+20 (2026-09-04)

> Scritto per essere letto **da una sessione pulita**, insieme a `CLAUDE.md`. Sostituisce
> `HANDOFF-post-step18.md` e `HANDOFF-post-step19.md`. Dice **dove siamo**, **cosa resta**, e
> **cosa richiede l'elevazione**.

---

## Stato

**HEAD su `develop`, albero pulito. Gli step 19 (motore ibrido) e 20 (un file con due nomi) sono
chiusi, firmati sul ferro e distribuiti. L'incrementale USN è acceso su `C:` e su `D:`.**

| | |
|---|---|
| xUnit | **975 verdi**, passata completa da elevato |
| Vitest | 268 (invariato: né 19 né 20 toccano il frontend) |
| Build | pulita, warnings-as-errors, Debug e Release |
| E2E | 25/25 all'ultima passata (2026-09-03); **non rieseguiti** dopo 19/20 — non li toccano, e la sessione era elevata |
| Harness | **62/62 PASS, 0 FAIL, 0 SKIP**, elevato (59 + le tre coppie di `hard-link-identity`) |
| Servizio installato | **step 20**; nessuna migration da 18; backup `filetracert.db.pre19` e `.pre20` |
| Catalogo | 993 780 righe `Files` · 151 341 directory · 870 136 righe FTS · 31 volumi · invariante §6 vera |

**Cursori USN**: `Windows-SSD` (`Enumeration`, in avanzamento) **e** `Dati` (`UsnJournal`). Il primo
è A4 sul catalogo vero: perimetro camminato per enumerazione, cursore preso lo stesso.

---

## Come è andata, in breve (i dettagli sono in `CLAUDE.md`)
1. Harness elevato a macchina sana → **59/59**, firma di A4 presa.
2. Deploy di 19 → verificato pulito → **la ri-scansione di `C:` è fallita** su `UNIQUE constraint
   failed: Files.VolumeId, Files.UsnFileRef`. Hard link: 153 FRN rivendicati da più di un path sul
   perimetro vero. Rollback a 18.
3. Step 20: il riferimento diventa una rivendicazione che **al più un path per volume** detiene.
   RED in-process e sul ferro, harness 62/62, code review indipendente.
4. Deploy di 20 → la ri-scansione di `C:` arriva in fondo e scrive il cursore → **e non succede
   niente**, perché `UsnSyncWorker` chiedeva ancora «quale motore», la domanda che A4 aveva
   sostituito ovunque tranne lì. Fix + test riscritto, ri-distribuito.
5. Incrementale su `C:` provato sul ferro: 101 343 record di arretrato in un tick, poi un file vero
   creato e cancellato — indicizzato e marcato assente entro 15 s, **senza** che
   `LastFullScanUtc` si muovesse.

---

## Cosa resta, in ordine di valore

### 1. Il buco degli hard link, dichiarato e distribuito *(voce A5b della roadmap)*
**Un'identità sopravvive al path a cui è stata concessa.** Cancellato il path che ha vinto il FRN
mentre il gemello hard-linked resta, la scansione successiva lascia **due righe per un path**, una
fantasma e assente per sempre; il delta USN fa la stessa ri-puntatura e lì la riga vacante non viene
nemmeno marcata assente. È una **regressione rispetto al 18** in quello scenario, ed è distribuita.

**Il prezzo pratico è basso e va saputo**: un fantasma ha `IsPresent = 0`, quindi non lo mostra il
Catalogo (`IsMaterialized AND IsPresent`), non lo trova la Ricerca e non lo contano i contatori —
è una riga nel database che nessuna schermata mostra.

**La fix ovvia riapre il crash del 20** (preferire il match per path consegna il riferimento a una
riga mentre l'altra lo porta ancora). Chiuderlo è una decisione su **identità contro path dentro il
merge** (§6). Fissato da due test — uno riproduce il fantasma, l'altro fa la **stessa** cancellazione
con una camminata senza identità e mostra il merge risolvere correttamente — e da un `KNOWN HOLE`
accanto a `EnumerateRaw`.

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
- **P1, metà MFT**: quando è il dump dell'MFT a camminare (solo se un root è la radice del volume),
  `nodes[frn]` è last-write-wins e i path in più non arrivano al catalogo. La metà enumerazione è
  chiusa dal 20.

### 3. Decisioni di prodotto ferme
- **Filtro dimensione in Ricerca** (esiste nell'API e nello store, non a video).
- **Il verso opposto dell'esclusione** (cartella che smette di essere nascosta): costa una scansione.

### 4. Da tenere d'occhio sul servizio, ora che `C:` ha il cursore
- Il worker fa **un volume alla volta** e `ReadChanges` è **sincrona** (limite dichiarato in 14d):
  un tick lungo su `C:` ritarda quello di `D:`.
- Sotto scansione pesante il `VolumeSyncWorker` può prendere `SQLITE_BUSY`, loggare l'eccezione
  intera e riprendersi al ciclo dopo — visto in questo deploy, è la resilienza di §9, non un difetto.

---

## Il vincolo dell'elevazione (invariato)
Gli E2E si **rifiutano** di partire da elevato (12a); l'harness sul percorso USN e il deploy la
**pretendono**. Un subagent **non** eredita l'elevazione.

## Regole operative apprese (in memoria)
- **Prima di misurare, controllare la macchina**: `cmd /c exit` ~25 ms (66 ms via bash è sano). Se
  costa secondi, cercare i processi `dotnet` orfani da build interrotte.
- **Un deploy non è verificato finché non si è esercitata la funzione che cambia.** Qui il servizio
  è partito pulito **due volte** con un difetto grave dentro: la prima volta il crash è arrivato
  alla prima scansione, la seconda il guasto era il **silenzio**. Le checklist di deploy (migration,
  FTS, invariante, zero Error) sono tutte passate in entrambi i casi.
- **Un cursore che avanza non prova niente da solo**; un cursore che **non** avanza sì. E `+0 s di
  CPU` distingue «lento» da «non sta facendo nulla», che era la biforcazione diagnostica del giro.
- **Un solo `dotnet` alla volta**; la solution è `src/backend/FileTracert.slnx`.
- **L'harness legge `appsettings.json` dalla `bin`**: dopo averla modificata serve un `dotnet build`
  del progetto, altrimenti parte con la config vecchia e dice «Nothing to do».
- **Quella config è CRLF**: il tool Write produce LF e il sha256 non torna pur essendo il contenuto
  identico. Serve una passata CRLF.
- **Gli heredoc bash dimezzano i backslash** — mi ha morso due volte in questa sessione, una delle
  quali mentre scrivevo la nota che lo dice. Script con path Windows o stringhe verbatim C# via
  Write tool.
- **Una mutazione si prova sulla solution compilata**; ripristino per **sostituzione inversa**, mai
  `git checkout -- file`.
- **E2E da Claude Code**: `Start-Process cmd /c npm test` con finestra nascosta (ha una console).
- **Il token del servizio** si legge dall'HTML **servito** (`http://127.0.0.1:5005/`), non dal file
  su disco: lì c'è ancora il segnaposto `__FT_TOKEN__`.
