# TASK — step 17: il paging arriva dove §7 lo prometteva

> Decisione di prodotto dell'utente (2026-09-03): «procedi a paginare, anzi, verifica altri punti
> dove manca, perché potrebbe essere vitale».

## Ricognizione (fatta prima di toccare)

Tre liste senza tetto, misurate sul catalogo vivo in sola lettura (max **845** sottocartelle in
una cartella, 24 cartelle sopra 200, 0 sopra 1 000 — tutte `node_modules`/`.nuget`, ma un archivio
foto con cartelle per data ci arriva). Ogni riga elencata paga **due sottoquery di conteggio**.

| lista | endpoint | consumatori |
|---|---|---|
| sottocartelle del Catalogo (E5) | `GET /api/catalog/{v}/children` → `Directories` | Catalogo, picker Sposta/Copia, E2E `api.ts` |
| cartelle del disco (Setup) | `GET /api/volumes/{v}/folders` | albero del Setup |

Bounded per costruzione e lasciati stare: volumi, scansioni, root, batch (500); log, notifiche,
coda, ricerca e file sono già `PagedResult` con `MaxTake` 200.

## Decisioni (tecniche, prese qui)

1. **Contratto**: `CatalogChildrenDto.Directories` diventa `PagedResult<CatalogDirDto>`. Query
   `dirSkip`/`dirTake` (default 50, cap `MaxTake`); `skip`/`take` restano dei file, così le due
   liste si paginano **indipendentemente** — una cartella con 800 sottocartelle e 3 file non deve
   pagare la seconda per la prima.
2. **UI**: le cartelle si **appendono** («Mostra altre N cartelle»), non si sfogliano. In un
   browser ad albero, perdere le cartelle già viste per vederne altre è un errore; i file restano
   con il pager precedente/successiva che hanno. Stesso gesto nel picker.
3. **Setup**: `FolderBrowseService.ListAsync` restituisce `PagedResult<FolderNodeDto>` ordinato
   per nome (l'enumerazione del disco non ha ordine); l'albero appende per nodo.
4. `invalidate` (push `ProjectionChanged`) ricarica le cartelle con `take = min(caricate, 200)`:
   non collassa la vista dell'utente a ogni messaggio.

## Commit
1. Contracts + Host + xUnit (RED: il test chiede `Directories.Items`/`TotalCount` su 5 cartelle con `dirTake=2`).
2. Frontend: modello, api, store, Catalogo, picker (skill `impeccable`), Vitest. E2E `api.ts` cammina tutte le pagine.
3. Setup: servizio, controller, store, albero.
4. Brief: §7 (l'eccezione E5 si chiude), roadmap B, nota E2E del 2026-09-03.

## Verifica
xUnit + Vitest + `ng build` + E2E completa (non elevato). Harness non richiesto: nessun file
viene toccato diversamente.
