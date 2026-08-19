# TASK — Step 11h: perché una riga è esclusa

> **Sessione dedicata, agente singolo.** Nasce dal rilievo che lo step 11g ha trovato e
> **non** ha chiuso, insieme alle due lacune collegate che ha documentato.
> Prerequisito: **11i mergiato** (suite stabile sotto carico), working tree pulito, Host chiuso.
> Riferimenti: `CLAUDE.md` §4 (filtri e riconciliazione senza ri-scansione), §6 (schema,
> convenzioni sui flag), §5 (FTS e nome proiettato), §3 (SQLite dietro `IFileSearchIndex`).
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.
> ⚠️ Se tocchi la UI, usa la skill `impeccable`.

## Perché

Lo step 11g ha reso vero che *escluso* è `IsIncluded=false` e *assente* è `IsPresent=false`.
Ma `IsIncluded` è **un bit senza memoria**: dice *che* una riga è fuori, non *perché*.

Conseguenza, con lo scenario esatto:

1. la scansione esclude un file perché sta sotto una cartella `Hidden` → `IsIncluded=false`;
2. l'utente cambia il filtro per estensione (o riattiva un altro watched root);
3. `FilterReconciler` ricalcola l'inclusione **per estensione** e non sa nulla della
   cartella nascosta → **rimette dentro** un file che lo scan aveva escluso per attributi;
4. resta dentro fino alla scansione successiva, che lo riesclude.

Prima di 11g la stessa sequenza era innocua **solo** perché quelle righe portavano la bugia
`IsPresent=0` (e la passata degli assenti le lasciava stare). Chiuso quel difetto, questo
è rimasto scoperto: è il debito che 11g ha creato consapevolmente e documentato.

Le tre cause di esclusione oggi indistinguibili:

| Causa | Chi la produce | Chi deve poterla disfare |
|---|---|---|
| **tipo** — l'estensione è fuori dall'allow-list | filtro (Setup) e scan | la riconciliazione, quando il filtro si allarga |
| **root spento** — il file sta sotto un watched root non attivo | scan (perimetro) e Setup | la riconciliazione, quando il root torna attivo |
| **saltato dallo scan** — attributi (Hidden/System) o segmento di path escluso | scan | **solo una scansione**: la riconciliazione non sa se la cartella è ancora nascosta |

La terza riga è tutto il punto: la riconciliazione **non deve** disfarla.

## Lavoro

### 1. Persistere il perché

Serve uno stato che distingua almeno le tre cause sopra. Scelte tue, da motivare nel commit:

- un enum persistito (`ExclusionReason`) su `FileEntry`, `None` quando `IsIncluded=true`;
- oppure un piccolo insieme di flag, se una riga può essere esclusa da **più** cause
  insieme (probabile: un `.tmp` dentro una cartella nascosta) — e qui sta la decisione
  vera: con un solo valore devi definire una precedenza e *disfare* diventa ambiguo,
  con dei flag ogni causa si spegne per conto suo. Guarda quale delle due rende
  `FilterReconciler` semplice, perché è lì che si paga.

Vincoli: migration con backfill (le righe esistenti `IsIncluded=false` non sanno perché lo
sono — la scelta più onesta è timbrarle con la causa che la riconciliazione **non** disfa,
così nulla rientra in silenzio, e lasciare che il primo scan le corregga); nessun
hard-delete; il campo si scrive negli stessi percorsi set-based di 11g
(`ReconcileUnseenFilesAsync`, il merge, `FilterReconciler`), senza degradare a per-riga —
**misura gli statement prima/dopo**, come ha fatto 11g.

### 2. `FilterReconciler` disfa solo ciò che ha diritto di disfare

Riscrivere la riconciliazione in termini di causa: riammette le righe escluse per **tipo**
(quando il filtro si allarga) e per **root spento** (quando il root torna attivo), lascia
stare quelle **saltate dallo scan**. Il risultato deve continuare a dire se serve comunque
uno scan (`NeedsScan`) per ciò che non può essere risolto senza guardare il disco — ed è
esattamente il caso della terza riga.

### 3. La riconciliazione risincronizza l'FTS

Lacuna documentata da 11g: la riconciliazione muove `IsIncluded` ma **non** tocca l'indice
FTS, quindi Catalogo e Ricerca si contraddicono finché non passa una scansione. Sistemarlo
usando l'API set-based già esistente (`IFileSearchIndex`, gli stessi metodi di sync/prune
usati dal merge), con il **nome proiettato** di §5 — la costante condivisa, non una copia.

Attenzione al costo: riallargare un filtro su un volume grosso può toccare centinaia di
migliaia di righe. Set-based o niente.

### 4. I contatori della pagina Volumi descrivono lo stesso perimetro

Seconda lacuna di 11g: dopo il cambio di semantica, i contatori di **file** e di **cartelle**
della schermata Volumi contano perimetri diversi (i file rispettano `IsIncluded`, le cartelle
no — 11g ha deciso, motivandolo, di non dare un `IsIncluded` alle directory). Decidere cosa
deve leggere l'utente e renderlo coerente: o i due numeri parlano dello stesso insieme, o
dicono a parole cosa contano. Un numero che non dichiara il proprio perimetro è un numero
che mente (skill `impeccable`).

## Split dei commit (indicativo)

1. `feat(data): a row remembers why it is excluded` — schema + migration + backfill.
2. `feat(scan): the scan stamps the reason it excluded a row`.
3. `feat(business): reconciliation undoes only what it can know about`.
4. `feat(business): reconciliation keeps the search index in step`.
5. `fix(frontend): the volume counters say what they count`.

## Test (RED prima del GREEN)

Contro l'implementazione reale (SQLite vero, `FileFilter` vero, entrambi i motori dove la
logica differisce):

- **lo scenario del difetto**: file sotto cartella `Hidden`, scan, poi allargamento del
  filtro per estensione → il file **resta escluso**. Oggi rientra: è il RED.
- root spento → file esclusi; root riacceso → rientrano **senza scan**.
- filtro ristretto e poi riallargato → i file per-tipo rientrano, quelli saltati dallo scan
  no.
- FTS: dopo una riconciliazione, Ricerca e Catalogo concordano **senza** una scansione di
  mezzo.
- DB che arriva dal comportamento vecchio (righe `IsIncluded=false` senza causa) → il
  backfill non le fa rientrare da sole, e uno scan le mette a posto.
- costo: contare gli statement della riconciliazione e della passata di chiusura dello scan,
  prima e dopo.

## Harness sul ferro (obbligatorio)

Estendere lo scenario `exclusion-vs-absence` (creato da 11g) o affiancarne uno: su file
veri, cartella nascosta + allargamento del filtro → la riga resta esclusa; root spento e
riacceso → rientra senza scan. Coppia in uso: `D:\Collaudo\A` → `C:\Collaudo\B` (**il drive
E: non esiste su questa macchina**). Baseline: **47 scenari, 47 PASS**. Riportare PASS/FAIL
e i tempi di scan. Rimettere la sezione `HardwareSmoke` di appsettings **byte-identica**
(sha256 di baseline `653f5990…`), non committata.

## Definition of done

- xUnit + Vitest verdi, build backend pulita (warnings-as-errors), `ng build` ok se tocchi
  il frontend (i 4 warning di budget SCSS sono pre-esistenti).
- Harness senza FAIL nuovi, scenario nuovo/esteso PASS, numeri riportati.
- Misure degli statement prima/dopo su riconciliazione e chiusura dello scan.
- **Code review finale** indipendente: correttezza vs scenari di fallimento, no silent catch
  (§9), layering (§3), niente duplicazione con ciò che 11g ha costruito, nessuna regressione
  di prestazione.
- `CLAUDE.md`: paragrafo «Fatto nello step 11h», §6 aggiornato con il nuovo campo, e la voce
  di roadmap lasciata aperta da 11g **chiusa** (insieme alle due lacune collegate).
