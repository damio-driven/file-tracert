# TASK — Step 11c: logging e shutdown puliti (WP8)

> **Sessione dedicata, agente singolo.** Terzo dei sei task dei WP minori
> (`TASK-step11-overview.md`). Prerequisito: working tree pulito, suite verde, Host chiuso.
> Riferimenti: `CLAUDE.md` §9 (**no silent catch**), §3 (CancellationToken / shutdown
> pulito), §6 (diagnostica: DB log dedicato); `CODE-REVIEW-HANDOFF.md` → C18, C23, C24, C28.
> Task **indipendente** dagli altri sul contenuto; l'unico file condiviso è `ScanService`
> (che 11a tocca altrove): non eseguirlo in parallelo con 11a.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

Quattro difetti che si notano solo quando serve la diagnostica — cioè quando qualcosa è
già andato storto. Stato verificato su `88571aa` (riverificare le righe):

| # | Dove | Cosa succede oggi |
|---|------|-------------------|
| C18 | `Business/Scanning/ScanService.cs:~286` e `~304`: `_usnReader.ReadFullSnapshot(volume.VolumeGuid, CancellationToken.None)` e `_enumerator.Enumerate(mountRoot, root.RelativePath, CancellationToken.None)` | Il token vero **esiste ed è scartato**. La fase di enumerazione dura minuti su un volume grosso: uno stop del servizio a metà scansione supera lo `ShutdownTimeout` e finisce in kill sporco. Contraddice §3 («`ApplicationStopping` linkato nei BackgroundService», shutdown pulito). |
| C23 | `Host/Program.cs:~39`: `var logProcessor = new SqliteLogProcessor(logStore);` registrato come **istanza pre-costruita** (`AddSingleton(logProcessor)`); `SqliteLoggerProvider.Dispose` (~:26) è di fatto un no-op che si affida al DI | Il container **non dispone le istanze che non ha creato lui**: `DisposeAsync` del processor non viene mai chiamato → la coda in memoria (bounded, fino a ~10k record) viene persa a ogni stop. Esattamente i log dello shutdown, cioè quelli che servono. |
| C24 | `Host/Logging/SqliteLogProcessor.cs:60, 66, 79` | Tre `catch` **nudi**, senza uno straccio di traccia. Violazione diretta di §9. Attenuante reale: il sink non può loggare su sé stesso — ma un breadcrumb su `Console`/`Debug` (o un contatore di record persi esposto altrove) sì. |
| C28 | `Data/Logging/SqliteLogStore.cs:~186-187`: `Message LIKE $search OR Exception LIKE $search` con `$search = $"%{query.Search}%"` | Il filtro `Category` una riga sopra è **escapato** (`EscapeLike` + `ESCAPE '\'`), il search no: cercare `100%` o `file_name` fa match a caso. Il metodo `EscapeLike` esiste già a ~:213. |

## Lavoro

### 1. C18 — il token arriva fino all'enumerazione

Propagare il `CancellationToken` reale a `ReadFullSnapshot` e `Enumerate`. Attenzione:
entrambe sono `IEnumerable` **pigre** consumate in un `foreach` — il token va passato alla
chiamata *e* il consumo deve poter uscire (le implementazioni `yield return` devono già
onorare il token: verificarlo in `Platform`, non darlo per buono).

Verificare gli altri `CancellationToken.None` di `ScanService` (~:435, ~:446, sui
`CommitAsync`): quelli sono **deliberati** — un commit non si annulla a metà — e vanno
lasciati con il commento che spiega perché. Il difetto è solo dove il token c'è e viene
buttato.

Scenario che il fix deve rendere vero: stop del servizio durante una scansione grossa →
la scansione esce entro il timeout, senza checkpoint bugiardo (§9a: `LastFullScanUtc` /
`LastUsn` si scrivono solo a scan completo, non toccarlo).

### 2. C23 — la coda dei log viene svuotata allo stop

Far sì che il processor venga **disposto davvero**. Opzioni (scegli e motiva):

- registrare il tipo e lasciare che il container lo costruisca (ma serve prima che il
  logging parta: verificare l'ordine di bootstrap, è il motivo per cui oggi è pre-costruito);
- registrarlo comunque come istanza **e** agganciare `IHostApplicationLifetime.ApplicationStopped`
  / `IAsyncDisposable` esplicito nel percorso di shutdown;
- far sì che `SqliteLoggerProvider.Dispose` disponga davvero il processor che possiede.

Requisito, qualunque strada: al termine dello stop la coda è **drenata** (i record ancora
in canale finiscono nel DB log) entro un tempo limitato — un drain senza cap trasforma un
servizio che si ferma in un servizio che non si ferma. Cap esplicito e loggato.

### 3. C24 — nessun catch muto

I tre catch nudi diventano catch che **lasciano una traccia**: `Console.Error` /
`Debug.WriteLine` con eccezione completa (messaggio + stack + inner), e/o un contatore di
record persi che qualcuno possa leggere. Il vincolo «il sink non può loggare su sé stesso»
si rispetta scrivendo **fuori** dal sink, non tacendo.

Distinguere `OperationCanceledException` in shutdown (rumore atteso, livello basso) dal
resto (§9, stessa regola applicata in 10b a `RealtimeEvents`).

### 4. C28 — il search dei log è letterale

Usare `EscapeLike` + `ESCAPE '\'` anche sul parametro `$search`, come già fa `Category`.
Un test con `100%` e uno con `file_name` bastano a fissarlo.

## Split dei commit (indicativo)

1. `fix(scan): the real token reaches the enumeration phase` — C18 + test.
2. `fix(host): the log queue is drained on shutdown` — C23 + test.
3. `fix(host): the log sink leaves a trace when it fails` — C24.
4. `fix(data): log search escapes LIKE wildcards` — C28 + test.

## Test (RED prima del GREEN)

- **C18**: scansione su enumeratore fake **lento** + token cancellato → la scansione esce
  entro un budget di tempo asserito (oggi non esce: è il RED). Il fake sta in `Platform`
  (port), non sostituisce il componente sotto esame.
- **C23**: processor con N record in coda + shutdown dell'host di test → gli N record sono
  **nel DB log**. Oggi si perdono.
- **C28**: due righe di log, una con `100%` nel messaggio e una senza → il search `100%`
  ne trova **una**.
- **C24**: difficile da testare in modo pulito; almeno un test che uno store che lancia non
  fa cadere il processo **e** incrementa il contatore di record persi (se scegli quella
  strada). Se non testabile, dirlo esplicitamente nel report finale invece di lasciarlo
  implicito.

## Harness sul ferro

Non è richiesto uno scenario nuovo: nessuno di questi fix cambia il comportamento su file
veri. Far girare comunque la suite harness già configurata per verificare che nulla sia
regredito (in particolare gli scenari di scansione, toccati da C18), e riportare i numeri.

## Definition of done

- xUnit verde, build backend pulita (warnings-as-errors).
- Harness: nessun FAIL nuovo rispetto alla baseline del task precedente.
- **Code review finale** indipendente: no silent catch (§9) verificato **riga per riga** su
  ciò che è stato toccato, shutdown senza deadlock né attese illimitate, layering (§3).
- `CLAUDE.md`: paragrafo «Fatto nello step 11c»; in `CODE-REVIEW-HANDOFF.md` marcare
  C18/C23/C24/C28 come chiusi.
