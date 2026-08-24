# TASK — Step 14b: una query lunga si annulla, e non tiene in ostaggio lo spegnimento

> **Sessione dedicata, agente singolo.** Nasce dalla conseguenza del difetto 14a, osservata
> sul ferro nello step 13. **Prerequisito: 14a mergiato** — sistemata la causa, questo giro
> chiude la classe di guasto che la causa ha rivelato.
> Riferimenti: `CLAUDE.md` §3 (CancellationToken / shutdown pulito, `ShutdownTimeout`), §9.
> ⚠️ Il servizio `FileTracert` è installato e attivo sul catalogo reale: fermalo prima di
> ricompilare.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Il difetto, misurato

Dopo che una query di ricerca è andata lunga (12 s nel caso mite, mai tornata nel caso
grave), sul servizio installato è successo questo:

- il client si è **sganciato**, e il servizio ha continuato a lavorare: **+147 s di CPU in
  119 s di orologio**, oltre un core, per più di venti minuti;
- un `sc stop` è rimasto **`StopPending` per oltre 270 secondi**, contro i **30 s** che
  `ShutdownTimeout` promette in §3.

Causa: `RequestAborted` è onorato tra un `await` e l'altro, ma **non entra dentro uno
statement SQLite già in esecuzione**. Il token viene passato correttamente ovunque (lo step
11c ha chiuso il buco su `ScanService`); semplicemente non ha presa su `sqlite3_step`.

Il risultato è che **un difetto di prestazioni diventa un difetto di spegnimento**: qualunque
query lenta, oggi o domani, può tenere il servizio in piedi oltre il suo timeout.

## Cosa deve diventare vero

1. Una richiesta HTTP annullata (client sganciato, tab chiusa, timeout) **smette di
   consumare CPU** entro un tempo limitato e osservabile.
2. Lo **stop del servizio** rispetta `ShutdownTimeout` anche con una query in corso.
3. Chi viene interrotto lo dice: log completo (§9), e la richiesta risponde in modo onesto
   invece di restare appesa.

## Come si ottiene (la parte tecnica interessante)

SQLite espone due meccanismi, e vanno capiti prima di scegliere:

- **`sqlite3_interrupt`** — interrompe lo statement in corso su una connessione, facendolo
  fallire con `SQLITE_INTERRUPT`. In `Microsoft.Data.Sqlite` la strada è chiamarlo sulla
  connessione giusta al momento giusto, il che richiede di sapere **quale** connessione sta
  servendo quella richiesta.
- **`sqlite3_progress_handler`** — callback ogni N istruzioni della VM, con la possibilità di
  abortire. Non richiede di raggiungere la connessione da fuori, ma va installato dove la
  connessione nasce.

Punti da decidere e documentare:

- dove vive il ponte token → interrupt (probabile: `Data`, dietro le interfacce, perché è
  SQLite-specifico — §3);
- come si evita di interrompere **la connessione sbagliata**: le connessioni sono in pool
  (lo step 11i ha chiuso una brutta storia proprio su questo — leggilo prima di scrivere
  codice che tocca la vita delle connessioni);
- cosa succede alle **scritture**: interrompere una query di lettura è innocuo, interrompere
  una transazione di scrittura no. Il perimetro minimo sensato è la **lettura** (Ricerca,
  Catalogo, liste), e il resto va lasciato fuori con una ragione scritta.

## Vincoli

- **Non toccare la crash-safety della coda**: il queue processor ha già la sua disciplina di
  checkpoint e i suoi `CancellationToken.None` deliberati sui commit. Se il tuo meccanismo
  può raggiungere quelle connessioni, escludile esplicitamente.
- **Nessun catch muto** (§9): un'interruzione è un evento, va loggata e distinta da un errore.
- Il comportamento normale non cambia: una query che finisce in tempo non deve accorgersi di
  niente (misura l'overhead se usi il progress handler).

## Test (RED prima del GREEN)

- **Query lunga + token annullato** → il metodo ritorna entro un budget asserito, e la CPU
  smette. Oggi non ritorna: è il RED. Costruisci la lentezza con dati veri (un match set
  grande su FTS5 vera), non con uno `sleep`.
- **Shutdown con query in corso** → l'host si ferma entro `ShutdownTimeout`. È il test che
  descrive il guasto vero visto sul ferro.
- **Nessuna interruzione spuria**: una query normale sotto carico non viene abortita.
- Se scegli il progress handler: un test che misura l'**overhead** su una query breve.

## Misura di chiusura, sul servizio installato

Riprodurre la sequenza dello step 13 su una **copia** del catalogo reale: query pesante,
client sganciato, poi `sc stop`. Riportare CPU dopo lo sgancio e tempo di stop. Il criterio
è quello del §3: **30 secondi**.

## Definition of done

- xUnit verde, build pulita (warnings-as-errors).
- I due numeri del ferro rimisurati (CPU dopo lo sgancio, durata dello stop).
- **Code review finale** indipendente: nessuna connessione interrotta per sbaglio, nessuna
  regressione sulla vita delle connessioni (step 11i), scritture fuori dal perimetro,
  overhead misurato.
- `CLAUDE.md`: paragrafo «Fatto nello step 14b»; voce n° 2 del lavoro successivo dello step
  13 marcata chiusa.
