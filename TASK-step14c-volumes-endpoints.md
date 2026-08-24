# TASK — Step 14c: le due schermate Volumi stanno sotto il secondo

> **Sessione dedicata, agente singolo.** Terzo per gravità fra i lasciti del soak dello step
> 13. Indipendente da 14a/14b sul contenuto; se gira dopo, tanto meglio (il ponte di
> annullamento di 14b vale anche qui).
> Riferimenti: `CLAUDE.md` §6 (indici), §7 (paging server-side, DTO con freschezza del dato),
> «Fatto nello step 13».
> ⚠️ Servizio installato e attivo sul catalogo reale: fermalo prima di ricompilare.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Il difetto, misurato sul catalogo vero (742 033 file, 15 volumi)

| endpoint | tempo |
|---|---|
| `GET /api/dashboard` | 373 ms |
| **`GET /api/volumes`** (lista) | **1 571 ms** |
| **`GET /api/volumes/{id}`** (dettaglio) | **1 768 ms** |
| `GET /api/queue` | 52 ms |
| notifiche | 23 ms |

Entrambi oltre il secondo, su una schermata che si apre di continuo e che dallo step 10c
viene **riletta a ogni riconnessione dell'hub**. La Dashboard, che aggrega la stessa tabella
`Files`, costa un quarto: la differenza non è la mole dei dati, è la forma delle query.

## Da dove viene (verifica, non assumere)

Sospetti, in ordine, tutti da confermare con `EXPLAIN QUERY PLAN` e con un contatore di
statement sul percorso vero:

- **conteggi per volume ripetuti riga per riga**: la lista mostra file e cartelle per ognuno
  dei 15 volumi; se ogni riga costa due aggregati sulla tabella grande, sono 30 passate;
- lo step 11h ha **raddoppiato i contatori** della pagina (file inclusi / cartelle
  nell'albero): due numeri con perimetri diversi, quindi potenzialmente due query ciascuno;
- il dettaglio somma ai contatori le cartelle monitorate, i filtri effettivi e la
  risoluzione dello stato del volume;
- gli indici covering introdotti dallo step 11e coprono il **Catalogo**
  (`DirectoryId, PendingDirectoryId, IsIncluded, IsPresent`), non necessariamente
  l'aggregazione **per volume**.

## Cosa deve diventare vero

- Entrambi gli endpoint **sotto i 300 ms** sul catalogo reale. Se il numero non è
  raggiungibile senza cambiare ciò che la schermata mostra, dillo e proponi il compromesso
  invece di aggirarlo: un contatore approssimato dichiarato è meglio di un secondo e mezzo.
- Il **significato** dei due contatori resta quello che 11h ha stabilito («Indice — N file
  inclusi», «Struttura — M cartelle»): l'efficienza non è una scusa per cambiare cosa
  contano.
- La forma resta quella di `CatalogTotals`/`QueueTotals` (un aggregato per tabella, idioma
  già usato): niente terzo dialetto.

## Vincoli

- **Nessun `ANALYZE`** introdotto di soppiatto: lo step 11e ha documentato che con le
  statistiche popolate un piano del Catalogo **collassa a scansioni**. Se pensi che serva, è
  una decisione a sé, misurata su entrambi i percorsi.
- Un indice nuovo si paga su ogni riga inserita da uno scan: giustificalo con il numero, e
  misura il costo di scansione prima/dopo (baseline harness: 2 002 file).
- `IsCatalogable`, `DataIsLive`/`estimateIsLive` e la freschezza dei dati (§7) non cambiano
  significato.

## Test (RED prima del GREEN)

- **Costo, non millisecondi**: contare statement e passate sulla tabella `Files` per
  entrambi gli endpoint, con più volumi seminati. Oggi il numero cresce con i volumi; dopo
  deve essere costante (o crescere molto meno).
- **Stessi numeri a video**: i contatori restituiti prima e dopo devono coincidere, incluso
  il caso di 11h (root spento → file a zero, cartelle no).
- Un test che semina abbastanza righe da rendere la differenza visibile senza rendere la
  suite lenta.

## Misura di chiusura

Su una **copia** del catalogo reale (mai il DB che il servizio sta usando), rimisurare i due
endpoint e riportare prima/dopo accanto ai 373 ms della Dashboard.

## Definition of done

- xUnit verde, build pulita; `ng build` se tocchi il frontend.
- I due numeri rimisurati, e il costo di scansione confermato invariato.
- **Code review finale** indipendente: equivalenza dei contatori, layering, nessun indice
  non giustificato, nessun cambiamento silenzioso di significato.
- `CLAUDE.md`: paragrafo «Fatto nello step 14c»; voce n° 3 del lavoro successivo dello step
  13 marcata chiusa.
