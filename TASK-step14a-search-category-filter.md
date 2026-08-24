# TASK — Step 14a: il filtro categoria della Ricerca costa come i match, non come il risultato

> **Sessione dedicata, agente singolo.** Il difetto più grave trovato dal soak dello step 13
> sul catalogo vero (742 033 file). Prerequisito: working tree pulito, suite verde.
> Riferimenti: `CLAUDE.md` §3 (SQLite dietro `IFileSearchIndex`), §6 (FTS5, indici), §7
> (paging server-side), «Fatto nello step 13».
> ⚠️ Il servizio `FileTracert` è **installato e in esecuzione** su questa macchina, sul
> catalogo reale: fermalo prima di ricompilare, e non usarlo come banco di prova distruttivo.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Il difetto, misurato

Sul catalogo reale (742 033 file, `%ProgramData%\FileTracert\filetracert.db`):

| query | risultato | tempo |
|---|---|---|
| `report` (senza filtro) | 431 righe | **517 ms** a freddo, 64 ms a caldo |
| `report` + categoria `Image` | **2 righe** | **12 123 ms** |
| `e` + categoria `Image` | — | **mai tornata** (client arreso a 180 s, poi a 300 s) |

Il costo segue l'**insieme dei match FTS**, non il risultato. Più il filtro è selettivo,
peggio va: è il contrario di ciò che l'utente si aspetta premendo un chip di categoria.

## Perché succede (ipotesi da verificare col piano di esecuzione, non da assumere)

`FileSearchIndex.SearchAsync` (`Data/Search/FileSearchIndex.cs`, ~:230-275) è:

```
FROM FileSearchIndex fts
JOIN Files f ON f.Id = fts.rowid
JOIN Volumes v ON v.Id = f.VolumeId
WHERE FileSearchIndex MATCH $match
  AND f.Category = $category        -- ← il filtro arriva DOPO
LIMIT $take OFFSET $skip
```

L'FTS produce i rowid dei match, poi **ogni riga** viene risolta su `Files` e scartata se la
categoria non combacia. Con `e` che matcha centinaia di migliaia di righe e `Image` che ne
tiene due, si pagano centinaia di migliaia di lookup per restituirne due — e il `LIMIT` non
aiuta, perché prima di riempirlo bisogna trovarle.

**Prima riga di lavoro: `EXPLAIN QUERY PLAN` sullo statement vero**, su una copia del
catalogo reale, con e senza filtro. Il fix si sceglie sul piano osservato, non sul sospetto.

## Direzioni possibili (scegli con la misura in mano)

- **Portare la categoria dentro l'FTS**: una colonna della tabella virtuale (o un token
  sintetico) così il filtro entra nel `MATCH` e l'indice restituisce già solo le righe
  giuste. È la strada che elimina il problema invece di renderlo più veloce; costa spazio
  nell'indice, una migration e l'aggiornamento dei quattro percorsi di popolamento (che dopo
  gli step 11e/11h passano tutti da `IFileSearchIndex`, quindi il punto di modifica è uno).
- **Intersezione guidata dall'altro lato**: quando il filtro è selettivo, partire da `Files`
  per categoria e intersecare con i rowid FTS. Richiede di sapere *quando* conviene, cioè
  una stima di selettività — e le stime che il pianificatore non ha sono esattamente ciò che
  lo step 11e ha imparato a non dare per scontato (in produzione non gira mai `ANALYZE`).
- **Indice covering su `Files`** che copra `(Id, Category, IsIncluded, IsPresent)` per
  evitare la risalita alla riga: attenua, non risolve — il numero di righe visitate resta lo
  stesso.

Vale per **tutti** i filtri della stessa forma, non solo la categoria: dimensione, data,
volume. Se il fix li copre tutti, meglio; se copre solo la categoria, dillo e spiega perché.

## Vincoli

- **`IFileSearchIndex` è il confine** (§3): niente SQL di ricerca fuori da `Data`.
- Il **nome proiettato** di §5 resta ciò che si indicizza (costante condivisa: non
  reimplementarla).
- Nessuna regressione sul percorso senza filtro, che oggi è veloce.
- Se serve una migration dell'FTS: il rebuild su 742 033 righe non è istantaneo — misuralo e
  scrivi quanto costa all'utente **una volta sola** all'avvio dopo l'aggiornamento.

## Test (RED prima del GREEN)

Contro l'implementazione reale (SQLite vero, FTS5 vera):

- **Il test che dimostra il difetto**: un indice con N righe di cui poche della categoria
  cercata; si assertisce il **lavoro**, non i millisecondi — righe visitate (la tecnica di
  `CountingSqliteConnection`, già usata da 11e/11g) o statement. Oggi il numero segue N; dopo
  il fix deve seguire il risultato.
- Stesso risultato prima e dopo, per ogni combinazione di filtri: la ricerca deve tornare
  **le stesse righe nello stesso ordine**.
- Il cap a 10 000 (step 11e/E3) resta e continua a limitare il lavoro.

## Misura di chiusura, sui dati veri

Su una **copia** del catalogo reale (mai sul DB che il servizio sta usando): ripetere le tre
query della tabella sopra e riportare i tempi prima/dopo. `e` + `Image` deve tornare, e il
numero va scritto.

## Definition of done

- xUnit verde, build backend pulita (warnings-as-errors); `ng build` solo se tocchi il frontend.
- I tre numeri della tabella rimisurati e riportati.
- **Code review finale** indipendente: equivalenza dei risultati, layering (§3), nessuna
  regressione sul percorso senza filtro, migration (se c'è) idempotente e misurata.
- `CLAUDE.md`: paragrafo «Fatto nello step 14a»; la voce n° 1 del lavoro successivo dello
  step 13 marcata chiusa.
