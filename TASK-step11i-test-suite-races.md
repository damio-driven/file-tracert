# TASK — Step 11i: la suite dice la verità anche sotto carico

> **Sessione dedicata, agente singolo.** Chiude la flakiness documentata negli step 11e/11f/11g.
> Prerequisito: 11g mergiato, working tree pulito, Host chiuso.
> Riferimenti: `CLAUDE.md` → sezione «Test (non negoziabile)», §3 (shutdown, concorrenza), §9.
> Va eseguito **prima** del prossimo work package: un test che fallisce a caso costringe ogni
> agente successivo a dimostrare che non è colpa sua, e prima o poi qualcuno smetterà di
> dimostrarlo.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Il sintomo

Sulla suite completa, **sotto carico concorrente**, 1-2 test di integrazione `Host`
falliscono. Un test **diverso** a ogni esecuzione (osservati almeno: `CatalogApiTests`,
`AuthEndpointTests`, `DomainApiTests`, `SetupApiTests`, `LogsApiTests`, `SqliteLoggingTests`,
`DatabaseInitializerTests`, `RootsBySpecificityTests`, `Win32FileMoverTests`), sempre verde
in isolamento, con firma ricorrente:

```
ObjectDisposedException: 'SQLitePCL.sqlite3'
```

talvolta dentro `sqlite3_create_collation`.

## La causa quasi certa (verificarla, non fidarsi)

`Microsoft.Data.Sqlite` mette le connessioni **in pool per connection string**, e il pool è
**per processo**. Nel repo ci sono almeno sei chiamate a `SqliteConnection.ClearAllPools()`:

- `tests/FileTracert.Tests/Host/FileTracertAppFactory.cs` → `Dispose` (~:120)
- `tests/FileTracert.Tests/Data/SqliteFileContext.cs` (~:50)
- `tests/FileTracert.Tests/Data/SqliteLogStoreTests.cs` (~:263)
- `tests/FileTracert.Tests/Host/DatabaseInitializerTests.cs` (~:105)
- `src/backend/FileTracert.HardwareSmoke/Harness/ScenarioEnvironment.cs` (~:152)

`ClearAllPools()` non chiude «le mie» connessioni: chiude **tutte quelle del processo**,
comprese quelle che un altro test, in un'altra classe, sta usando in quel momento. xUnit
esegue le classi in parallelo → un teardown qualsiasi può disporre l'handle nativo sotto i
piedi di chiunque altro. Questo spiega ogni pezzo del sintomo: la casualità di quale test
cade, il verde in isolamento, e il tipo dell'eccezione (l'handle nativo, non la connessione
gestita).

**Primo lavoro: dimostrarlo.** Un test o una prova riproducibile che lega la caduta alla
chiamata, non un «ho cambiato e non si è più visto». Se la causa fosse un'altra (o ce ne
fosse una seconda), il resto del task va riscritto attorno a ciò che hai misurato — dillo
nel report.

## Direzione del fix

Ogni teardown deve chiudere **il proprio** pool, non quello di tutti: `ClearPool` accetta
una connessione e agisce sulla sola connection string corrispondente. Ogni chiamante qui
conosce il proprio path (`_dbPath`, il file di log, la sandbox dello scenario), quindi la
versione mirata è a portata di mano ovunque.

Decisioni tue, da motivare:

- se il pooling nei test serva davvero, o se `Pooling=False` sulle connection string di
  test sia più onesto (attenzione: cambia anche il comportamento che si sta testando —
  `DatabaseInitializerTests` verifica il **checkpoint del WAL**, che con il pooling ha
  proprietà diverse; non spegnerlo lì senza guardare);
- se serva comunque una `[Collection]` xUnit per serializzare i test che condividono
  davvero una risorsa di processo. Usala solo dove la condivisione è reale: serializzare
  tutto nasconde il difetto invece di chiuderlo, e allunga la suite per sempre.
- l'harness (`ScenarioEnvironment`) è un processo suo e single-threaded: lì
  `ClearAllPools()` può restare, ma se la versione mirata è altrettanto semplice,
  uniformare riduce il numero di regole da ricordare.

Verificare anche gli altri usi di stato **statico di processo** nei test (le varie
`SQLitePCL.Batteries.Init()` sono idempotenti e innocue, ma guarda se c'è altro: cartelle
`%TEMP%` con nome fisso, variabili d'ambiente, provider di logging globali, `EventLog`).
Il difetto di classe è «un test tocca uno stato che appartiene al processo»: chiudilo dove
lo trovi, non solo dove è esploso.

## Criterio di riuscita (misurato, non dichiarato)

Il criterio **non** è «l'ho eseguita e era verde»: è precisamente la frase che questa
flakiness ha già smentito tre volte. Serve una prova sotto carico:

- la suite completa eseguita **N volte di fila** (N ≥ 10) **con carico concorrente reale**
  sulla macchina — ad esempio un `ng build` o una seconda build in parallelo, che è la
  condizione in cui il difetto si è manifestato — con **zero** fallimenti;
- e la prova che il carico c'era davvero (dillo: cosa girava, quanto durava una passata).

Se dopo il fix resta un fallimento residuo di natura diversa, **non insabbiarlo**:
riportalo con l'output, e se non lo chiudi in questo giro documentalo con la firma esatta.

## Split dei commit (indicativo)

1. `test: prove the pool is cleared process-wide` — la riproduzione.
2. `test: each teardown clears only its own pool` — il fix.
3. `test: serialize only what really shares a process resource` — se serve.

## Definition of done

- Prova di riproduzione prima del fix (RED), e la sua sparizione dopo (GREEN).
- N ≥ 10 passate consecutive della suite completa sotto carico, zero fallimenti, con i
  numeri riportati.
- xUnit verde, build backend pulita (warnings-as-errors). Il frontend non c'entra.
- L'harness sul ferro **non** è richiesto (nessuna modifica al prodotto); se tocchi
  `ScenarioEnvironment`, allora sì: una passata completa sulla coppia `D:\Collaudo\A` →
  `C:\Collaudo\B`, baseline 47/47 PASS.
- **Code review finale** indipendente: nessun test indebolito o serializzato per comodità,
  nessuna asserzione tolta, il difetto chiuso alla radice e non mascherato.
- `CLAUDE.md`: paragrafo «Fatto nello step 11i» e **rimozione della voce sulla flakiness**
  dai limiti noti (o sua riscrittura, se resta un residuo).
