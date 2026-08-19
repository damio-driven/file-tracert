# TASK — Step 11g: l'esclusione è `IsIncluded`, l'assenza è `IsPresent`

> **Sessione dedicata, agente singolo.** Nasce dalla decisione di prodotto presa dopo lo
> step 11f (2026-08-19): *le esclusioni da filtro/perimetro si marcano `IsIncluded=false`;
> `IsPresent` torna a significare soltanto «non c'è più sul disco»*.
> Prerequisito: 11a…11f mergiati, suite verde, working tree pulito, Host chiuso.
> Riferimenti: `CLAUDE.md` §4 (filtri dentro la pipeline, riconciliazione), §6 (convenzioni:
> `IsIncluded` vs `IsPresent`), §3 (SQLite dietro `IBulkIndexWriter`), §9.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

Oggi tre situazioni diverse finiscono nello stesso flag:

1. **il file non c'è più sul disco** → `IsPresent=false` — corretto;
2. **il file è fuori dai watched root attivi** → la scansione non lo vede, la passata degli
   assenti lo marca `IsPresent=false` — **sbagliato**: il file c'è, è fuori dal perimetro;
3. **il file sta sotto una cartella esclusa per attributi** (Hidden/System) e *era stato
   indicizzato prima* che la cartella diventasse tale → 11a lo scarta dallo stream, quindi
   ricade nel caso 2 — **sbagliato allo stesso modo**, e 11a lo aveva pinnato con un test
   dichiarando che era una bugia in attesa di decisione.

L'utente legge «assente» dove il fatto è «fuori dal perimetro che hai scelto». E il costo
non è solo cosmetico: `IsIncluded` è il flag che regge la riconciliazione senza
ri-scansione (§4 — riallargare il filtro non deve costare un re-scan), mentre `IsPresent`
guida ciò che l'app crede esista davvero sul disco.

Meccanismo attuale, verificato su HEAD (riverificare le righe prima di editare):

- `ScanService.PersistAsync` → `DropExcludedSubtrees` (~:304/:314) toglie dallo stream
  directory e file sotto un sottoalbero escluso (`ExcludedSubtrees`, introdotto da 11a);
- ciò che non è nello stream non viene "visto" dal merge;
- `IBulkIndexWriter.MarkAbsentFilesAsync` (`BulkIndexWriter.ScanMerge.cs` ~:89-90) fa
  `SET IsPresent = 0 … WHERE VolumeId = $vol AND IsIncluded = 1 AND IsPresent = 1` per
  tutto ciò che quel lotto di scan non ha toccato → i casi 2 e 3 cadono qui;
- `FilterReconciler` (`Business/Setup/FilterReconciler.cs`) sa già fare la cosa giusta per
  il **cambio di filtro per estensione**: `ExecuteUpdate` su `IsIncluded`, nessun delete.
  È il precedente da seguire, non da duplicare.

## Cosa deve diventare vero

- Un file **escluso dal perimetro o dal filtro** → `IsIncluded = false`, `IsPresent`
  **invariato** (resta `true` se il file sul disco c'è).
- Un file **sparito dal disco** → `IsPresent = false`, `IsIncluded` invariato.
- Riallargare il perimetro (riattivare un watched root, togliere l'esclusione Hidden,
  allargare l'allow-list) → i file tornano `IsIncluded = true` **senza ri-scansione**,
  come già fa `FilterReconciler` per le estensioni.
- Nessun hard-delete, mai (§6).

## Lavoro

### 1. La scansione distingue «non visto» da «escluso»

Il merge deve ricevere, oltre a ciò che ha visto, **il perimetro che ha applicato**: i
sottoalberi esclusi per attributi (già raccolti da `ExcludedSubtrees`) e i root attivi
usati per lo scan. Con quell'informazione:

- ciò che cade dentro un'esclusione → `IsIncluded=false`, `IsPresent` intatto;
- ciò che non cade in nessuna esclusione e non è stato visto → `IsPresent=false`,
  come oggi.

Il SQL sta in `Data` dietro `IBulkIndexWriter` (§3): la firma cambia, non il layering.
Attenzione alla forma della query: passare un insieme di prefissi a SQLite in modo
efficiente è il punto tecnico del task — la strada già usata dal merge (tabella di staging
TEMP per lotto) è probabilmente la risposta, ma scegli tu e motiva.

**Vincolo di prestazione**: la passata degli assenti non deve degradare da set-based a
per-riga. Misurare prima/dopo sul re-scan (baseline in `CLAUDE.md`, harness su
`D:\Collaudo\A`, ~2 000 file) e riportare il numero.

### 2. Le directory hanno lo stesso problema?

`DirectoryNode` ha `IsPresent` (9a) ma **non** ha `IsIncluded`. Decidere — e scrivere la
decisione — se una directory esclusa debba portare un flag suo o se basti che i suoi file
lo portino. Non aggiungere una colonna se non serve a nessun consumatore: `Directories` è
la struttura navigabile, e una cartella che esiste sul disco **esiste**, anche se non se ne
indicizza il contenuto. Se la scelta è «niente colonna», allora la scansione non deve più
marcare `IsPresent=false` sulle directory solo perché sono escluse.

### 3. Il ritorno dentro il perimetro non costa un re-scan

Dove oggi si riattiva un watched root o si cambiano gli attributi esclusi, deve scattare la
riconciliazione su `IsIncluded` (stesso mestiere di `FilterReconciler`, **esteso**, non
riscritto accanto). Verificare i percorsi di `Setup` che toccano root e filtri, e che il
risultato dica se serve comunque uno scan (`NeedsScan`) per i tipi mai indicizzati.

### 4. Ciò che legge quei flag

Passare in rassegna i consumatori: Catalogo (visibilità righe e contatori), Ricerca, FTS,
conteggi per volume, `PendingWorkGuard`/enqueue (un file escluso ma presente **è ancora
spostabile**? decidere e motivare: la risposta coerente con §5 è sì, l'operazione lavora su
ciò che esiste sul disco), Dashboard. Dove la UI diceva «assente» per un file che invece è
solo fuori perimetro, dirlo con le parole giuste (skill `impeccable` se tocchi la UI).

### 5. Migration / dati esistenti

I DB già in uso contengono righe marcate `IsPresent=false` dal comportamento vecchio: non
sono distinguibili con certezza dai file davvero spariti. **Non inventare una migration che
indovina.** Il re-scan successivo rimette a posto ciò che è ancora sul disco: verificarlo
con un test (riga `IsPresent=false` + file presente e fuori perimetro → dopo lo scan
`IsPresent=true, IsIncluded=false`). Se serve una migration, che sia solo per colonne
nuove.

## Split dei commit (indicativo)

1. `feat(data): the absent pass knows what the scan deliberately skipped`.
2. `feat(scan): exclusion marks IsIncluded, absence marks IsPresent`.
3. `feat(business): widening the perimeter reconciles without a rescan`.
4. `fix(frontend): say "fuori perimetro", not "assente"` (se la UI lo mostra).

## Test (RED prima del GREEN)

Contro l'implementazione reale (SQLite vero, `FileFilter` vero, entrambi i motori di scan
dove la logica differisce):

- file indicizzato, poi il suo watched root viene disattivato, poi scan → `IsIncluded=false`,
  **`IsPresent` resta true**. Oggi è il contrario: è il RED.
- file indicizzato, poi la sua cartella diventa Hidden, poi scan → idem. **11a ha lasciato
  un test che pinna il comportamento attuale: quel test va aggiornato, ed è il segnale che
  stai cambiando ciò che volevi cambiare** (non aggiustare altri test per farli passare).
- file davvero cancellato dal disco → `IsPresent=false`, `IsIncluded` intatto (nessuna
  regressione sul caso che già funziona).
- riattivazione del root → `IsIncluded=true` **senza** scan.
- DB che arriva dal comportamento vecchio (`IsPresent=false` su file esistente fuori
  perimetro) → uno scan lo rimette a posto.
- costo: la passata degli assenti resta set-based (contare gli statement, non i ms).

## Harness sul ferro (obbligatorio)

Scenario nuovo: indicizza, restringi il perimetro (root disattivato o cartella resa
Hidden), ri-scansiona, verifica su **file veri** che le righe siano escluse e non assenti,
e che riallargando tornino incluse senza ri-scansione. Coppia in uso: `D:\Collaudo\A` →
`C:\Collaudo\B` (**il drive E: non esiste su questa macchina**). Riportare PASS/FAIL, i
tempi di scan a confronto con la baseline, e rimettere la sezione `HardwareSmoke` di
appsettings **byte-identica** (verifica sha256), non committata.

## Definition of done

- xUnit + Vitest verdi, build backend pulita (warnings-as-errors), `ng build` ok se tocchi
  il frontend (i 4 warning di budget SCSS sono pre-esistenti).
- Harness senza FAIL nuovi, scenario nuovo PASS, numeri riportati.
- **Code review finale** indipendente: correttezza vs scenari di fallimento, no silent
  catch (§9), layering (§3), niente duplicazione con `FilterReconciler`, nessuna
  regressione di prestazione sulla passata degli assenti.
- `CLAUDE.md`: paragrafo «Fatto nello step 11g», la voce di roadmap sull'ambiguità
  `IsPresent` **chiusa**, e §6 aggiornato se la semantica dei flag va enunciata meglio.
