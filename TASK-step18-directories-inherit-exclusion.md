# TASK — step 18: l'esclusione si eredita alle cartelle

> Decisione di prodotto dell'utente (2026-09-03): «sì, falla ereditare alle cartelle figlie».
> Chiude i residui 1 e 2 dello step 16 (il traffico di scrittura che disfa l'esclusione per
> attributi; il delta che fa crescere il catalogo dentro un sottoalbero escluso). Il residuo 3
> (stesso-tick) resta fuori: si chiude dentro `Classify`, per identità, ed è un giro a sé.

## Il fatto che manca, e dove lo si scrive e legge

Il delta risolve il padre di ogni record dalle righe `Directories` (`LoadDirectoriesByFrnAsync`)
e giudica ogni item sui **propri** attributi. Una cartella diventata nascosta **dopo** essere
stata indicizzata ha una riga, e quella riga non dice niente. Due colonne su `Directories`:

| colonna | significato (**effettivo**: lei o un antenato) | chi la scrive | chi la disfa |
|---|---|---|---|
| `ExcludedByScan` | attributi Hidden/System, come li ha visti l'ultima scansione o il delta | chiusura dello scan (`ScanSkipAreas`, aree senza nome) · pass di sottoalbero del delta | solo una scansione che **cammina** la cartella (`DirectoryMerger`, righe viste) |
| `ExcludedByPath` | un segmento del path è in `ExcludedPaths` | chiusura dello scan · pass del delta · **`FilterReconciler`** (derivabile dal `MaterializedPath`) | gli stessi, nei due versi |

Niente `ExcludedByRoot` sulle cartelle: i root sono impostazioni, il delta chiede già
`GoverningRoot` per ogni path. Niente `IsIncluded`: «una cartella che esiste esiste» (11g), la
visibilità non cambia. Le cartelle nascoste fin dalla prima scansione continuano a **non avere
riga**: lì l'eredità funziona già per assenza del padre.

## Lettura nel delta
`LoadDirectoriesByFrnAsync` carica le due colonne. Ogni padre di catalogo che porta una causa
viene registrato nel `ScanPerimeter` come sottoalbero escluso **ereditato** (`inherited: true`):
`IsExcluded`/`SkipVerdict` lo vedono — quindi un file toccato lì dentro va in `outside` con la
causa, una sottocartella nuova cade nel `RemoveAll` del C16 e non nasce — ma
`ExcludedSubtreeRoots` **non** lo restituisce, così `ExcludeSubtreesAsync` non ripaga il pass di
sottoalbero (31 ms per cartella sul volume di sistema) a ogni tick che nomina un file lì dentro:
le righe sotto sono già timbrate dal tick che ha visto la cartella diventare nascosta.

## Commit
1. `Data`: colonne + migration additiva `AddDirectoryExclusionCauses`, default 0, **nessun
   backfill** (pessimista come 11h/16: la prima scansione completa le scrive; fino ad allora il
   delta si comporta come oggi su quelle righe).
2. Scan: `BulkIndexWriter.ExcludeSkippedAsync` timbra anche `Directories` (una UPDATE per causa
   sulle aree senza nome); `DirectoryMerger` azzera le due colonne sulle righe **viste** che le
   portavano (lista in memoria, normalmente vuota → zero statement). RED su `ScanMergeTests` /
   `ScanServiceTests`; i conteggi di statement pinnati si muovono di +1 per causa e lo si scrive.
3. Delta: lettura + eredità nel perimetro + `ExcludeSubtreesAsync` timbra le righe `Directories`
   del sottoalbero. `SnapshotAsync` della convergenza confronta anche le due colonne (tutti i
   24 casi devono restare verdi); casi nuovi: file modificato dentro la cartella nascosta al tick
   dopo (oggi riammesso → RED), sottocartella + file creati lì dentro al tick dopo (oggi indicizzati
   → RED), cartella che smette di essere nascosta e viene ricamminata (le colonne si azzerano).
4. `FilterReconciler`: `ExcludedByPath` sulle cartelle nei due versi, stesso predicato incorniciato.
5. Harness: `usn-hidden-subtree` guadagna il secondo tick (modifica + creazione dentro la cartella
   nascosta). Gira solo elevato: PASS da prendere nella sessione elevata.
6. Brief.

## Limiti dichiarati in partenza
- Gli handler di Move/Rename (`CascadeDirMoveAsync`) riscrivono `MaterializedPath` e non le cause:
  stessa famiglia della nota di 11g/16; la riconciliazione di Setup e la scansione riparano.
- Il caso stesso-tick (residuo 3) resta aperto.
- Il Catalogo non mostra le cause sulle cartelle (nessuna UI in questo giro).
