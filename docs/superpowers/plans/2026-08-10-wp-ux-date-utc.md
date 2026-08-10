# TASK — WP-UX «date e UTC» (finding #12, #11, C31)

> **Branch:** `develop` (nessun branch nuovo — vedi CLAUDE.md «Lavoro in parallelo»)
> **Base:** `bb07a06` · **Riferimenti:** `CODE-REVIEW-HANDOFF.md` finding 11, 12, C31
> **Posizione in roadmap:** punto 1 («Fix UX brucianti»), prerequisito di niente,
> ma sblocca la verificabilità del filtro data prima dello step 9.
> **Questo file È il piano.** Implementare per commit, nell'ordine dato.

---

## 1. Contesto verificato su HEAD (2026-08-10)

Righe riverificate oggi, valide su `bb07a06`:

| Finding | Dove (verificato) | Stato |
|---|---|---|
| **#12** | `FileTracert.Data/FileTracertDbContext.cs:24-27` — nessun `ConfigureConventions`, solo `ApplyConfigurationsFromAssembly`. `HasConversion` presente solo sugli enum. | aperto |
| **#11** | `FileTracert.Data/Search/FileSearchIndex.cs:240,245` — `p.Add(("$modFrom", q.ModifiedFrom.Value.ToString("o")))`, confrontato con `f.ModifiedUtc` TEXT scritto dal provider come `2026-07-03 14:20:29.912`. | aperto |
| **C31** | `search.store.ts:19-20` ha `modifiedFrom/modifiedTo`, ma `search.html:42-84` espone solo categoria / solo-online / volume: **nessun input data**. `SearchController.cs:42-44` risponde 400 se `Kind == Unspecified`. | aperto (latente) |

Fatti utili accertati:
- I parametri del filtro FTS sono già `List<(string Name, object Value)>` e vengono
  bindati con `AddWithValue` (`FileSearchIndex.cs:127,161`): si può passare un
  `DateTime` **come oggetto**, lasciando a `Microsoft.Data.Sqlite` la serializzazione
  nel formato di storage. Nessuna stringa da comporre a mano.
- La pagina risultati FTS seleziona **solo i rowid** (`FileSearchIndex.cs:143-153`) e
  idrata i DTO via EF ⇒ il converter del commit 1 copre anche la ricerca.
- `SqliteLogStore` è **fuori scope**: scrive e filtra entrambi con `"o"`
  (`SqliteLogStore.cs:20,75,139,193,199`) e rilegge con `DateTimeStyles.RoundtripKind`
  (`:117`) ⇒ già coerente, Kind già `Utc`. **Non toccare.**
- `relative-time.pipe.ts:18` usa `Date.parse` sulla stringa: con il converter del
  commit 1 il JSON esce con `Z` e il pipe diventa corretto **senza modifiche**.

---

## 2. Vincoli

- **RED prima del GREEN** su ogni commit (CLAUDE.md «Test»): il test deve fallire
  sull'HEAD attuale, poi passare.
- **Niente mock del componente sotto esame**: DbContext + SQLite veri per #12,
  `FileSearchIndex` reale per #11.
- **UI con la skill `impeccable`** (CLAUDE.md §2/§8) — commit 3.
- **Niente scope creep**: gli altri finding di WP5 (#6 `UsnFileRef`, C19
  `Extension/Category` al rename, C16 esclusione ereditata, P2 collation) **restano
  fuori**. Se emergono, segnalarli, non fixarli qui.
- **Host chiuso prima di ricompilare** il backend.

---

## 3. Commit 1 — `fix(data): DateTime UTC globale via ConfigureConventions` (#12)

**Obiettivo:** ogni `DateTime` letto dal DB principale ha `Kind = Utc`, quindi la
serializzazione JSON emette la `Z` e ogni timestamp in UI smette di slittare
dell'offset locale.

**Modifica**
- `FileTracert.Data/FileTracertDbContext.cs`: override di `ConfigureConventions`
  con un `ValueConverter<DateTime, DateTime>` applicato a `Properties<DateTime>()`
  (EF applica la variante nullable anche a `DateTime?` — verificare in test, non
  darlo per scontato).
- Semantica del converter — **importante, non invertirla**:
  - *write*: `v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v`
    (i valori del dominio sono già UTC per convenzione §6; un
    `ToUniversalTime()` incondizionato reinterpreterebbe gli `Unspecified` come
    ora locale e sposterebbe i dati già scritti).
  - *read*: `DateTime.SpecifyKind(v, DateTimeKind.Utc)`.
- Il converter va in un tipo dedicato in `FileTracert.Data` (es.
  `Conversions/UtcDateTimeConverter.cs`), con commento sul perché la write è
  condizionale.

**Attenzione (da verificare, non assumere)**
- `EFCore.BulkExtensions` legge la configurazione del modello: rieseguire
  `BulkIndexWriterTests` e controllare che il formato scritto sul disco non cambi
  rispetto a prima (se cambiasse, i confronti del commit 2 vanno ritarati).
- `AuditingInterceptorTests` — l'interceptor scrive `DateTime.UtcNow` (Kind=Utc):
  nessun effetto atteso, ma il test è il canarino.

**Test RED→GREEN**
- `tests/FileTracert.Tests/Data/DbContextIntegrationTests.cs` (o nuovo
  `UtcDateTimeConversionTests.cs`): salva un `Volume` con `LastSeenUtc` UTC e un
  `FileEntry` con `ModifiedUtc`/`LastIndexedUtc`, ricarica da un context nuovo,
  assert `Kind == Utc` su almeno un `DateTime` **e** un `DateTime?` valorizzato.
- `tests/FileTracert.Tests/Host/` (usando `FileTracertAppFactory`): assert che la
  stringa JSON di un endpoint con timestamp DB-sourced (es. lista volumi) termini
  con `Z` sul campo temporale. È questo il test che riproduce davvero il bug utente.

---

## 4. Commit 2 — `fix(search): bind dei filtri data nel formato di storage` (#11)

**Obiettivo:** `modifiedFrom`/`modifiedTo` filtrano davvero, invece di confrontare
`2026-07-10T00:00:00.0000000Z` con `2026-07-03 14:20:29.912` (`' '` 0x20 < `'T'` 0x54).

**Modifica**
- `FileTracert.Data/Search/FileSearchIndex.cs:237-247`: passare il `DateTime`
  (normalizzato a UTC) come **valore del parametro**, non come stringa `"o"`.
  Commento breve sul perché (il provider serializza nel formato di storage; una
  stringa ISO non è confrontabile lessicalmente con la colonna TEXT).
- Non introdurre `julianday()`/`unixepoch()`: annullerebbero l'uso dell'indice su
  `ModifiedUtc` (usato anche dall'`ORDER BY` di `SearchSort.Date`).

**Test RED→GREEN**
- `tests/FileTracert.Tests/Data/FileSearchIndexTests.cs`: indicizzare file con
  `ModifiedUtc` noti a cavallo di una mezzanotte; assert che
  `modifiedFrom = mezzanotte del giorno X` **includa** i file modificati alle
  14:20 del giorno X (oggi li esclude) e che `modifiedTo = mezzanotte` **escluda**
  il pomeriggio dello stesso giorno (oggi li include).
- `tests/FileTracert.Tests/Host/SearchApiTests.cs`: stesso scenario via API con
  date `...Z`, per coprire anche il binding del controller.

---

## 5. Commit 3 — `feat(search): filtro data nella UI di Ricerca` (C31)

**Obiettivo:** esporre il filtro data (richiesto da §8 schermata 4) e normalizzare a
UTC lato client, così il 400 di `SearchController.cs:42-44` non è raggiungibile.

> ⚠️ **Usare la skill `impeccable`** per il markup/SCSS: il filtro va nel design
> system esistente (`.chip`, `.volume-select` in `search.html:42-84`), niente stile
> ad-hoc, niente framework CSS.

**Modifica**
- `search.html`: due input data («Modificato da» / «a») nella `filter-row`, coerenti
  con i controlli esistenti, con `aria-label`, e comportamento «vuoto = nessun
  filtro». Nessun submit implicito diverso da quello già usato dagli altri filtri
  (`onSubmit()` sul change).
- `search.ts`: handler che converte il valore `yyyy-MM-dd` dell'input in ISO UTC —
  `from` = inizio giornata, `to` = **fine** giornata (23:59:59.999) o inizio del
  giorno successivo con confronto `<`: scegliere una semantica, dichiararla nel
  commento, testarla. Il campo `to` inteso come «fino a tutto quel giorno» è la
  lettura naturale per l'utente.
- Normalizzazione UTC in un helper riutilizzabile (`shared/`), non inline nel
  componente: serve anche al Catalogo quando esporrà lo stesso filtro.
- Decidere se il valore digitato è **locale** o **UTC** e commentarlo: l'input
  `type="date"` non porta timezone; interpretarlo come giornata locale dell'utente
  e convertirlo è la scelta corretta (e va scritta nel test).

**Test (Vitest)**
- `search.store.spec.ts` (o nuovo spec dell'helper): assert che la richiesta
  emessa porti `modifiedFrom`/`modifiedTo` in ISO con `Z` e con i confini di
  giornata attesi; assert che il clear rimetta `null`.

---

## 6. Commit 4 — `test(harness): scenario filtro data sul ferro`

CLAUDE.md «Test» è esplicito: ogni comportamento fixato va coperto in
`FileTracert.HardwareSmoke` e deve dare **PASS** sul ferro configurato
(`D:\Collaudo\A`, `E:\Collaudo\B` — vedi memoria «Harness collaudo procedure»).

- Nuovo `Scenarios/SearchDateFilterScenario.cs` sul modello di
  `FolderMetadataScenarios.cs` (che già costruisce una `FileSearchQuery` con
  `ModifiedFrom/ModifiedTo: null`): creare file con `LastWriteTimeUtc` noti,
  scansionare, poi:
  1. query con `ModifiedFrom` = mezzanotte UTC del giorno dei file → il file **c'è**;
  2. query con `ModifiedTo` = mezzanotte UTC dello stesso giorno → il file **non c'è**;
  3. assert che il DTO restituito abbia `ModifiedUtc.Kind == Utc` (copre anche #12
     end-to-end sul percorso reale).
- Registrare lo scenario in `ScenarioCatalog.cs`.

---

## 7. Criteri di accettazione

1. Nessun timestamp DB-sourced arriva in UI senza `Z`; `relativeTime` mostra il
   valore giusto su macchina con offset ≠ 0 (verificabile dal test JSON, non a mano).
2. `modifiedFrom` a mezzanotte **include** i file di quel giorno; `modifiedTo`
   a mezzanotte **non include** il pomeriggio dello stesso giorno.
3. Il filtro data è usabile dalla schermata Ricerca e non produce mai 400.
4. Test nuovi: RED verificato prima del fix (annotare l'esito RED nel messaggio di
   commit o nel report finale), GREEN dopo.
5. `dotnet build` pulito (warnings-as-errors), `dotnet test` verde,
   `npm run test` (Vitest) verde. Il budget SCSS di `catalog/search.scss` è **rosso
   preesistente** (vedi memoria): non è una regressione di questo task, ma non
   peggiorarlo — se il nuovo markup sfonda ancora il budget, dirlo esplicitamente.
6. Scenario harness in PASS sul ferro.

## 8. Code review finale (obbligatoria)

Review indipendente delle modifiche: correttezza vs criteri; nessun silent catch
(§9); layering (§3 — il converter sta in `Data`, la normalizzazione date UI sta nel
frontend, `Contracts` non cambia); nessuna duplicazione dell'helper date;
test reali RED→GREEN. Riportare cosa è stato trovato e cosa corretto.

## 9. Fuori scope — da non fare qui

`#6` (UsnFileRef non azzerato al move cross-volume), `C19` (Extension/Category al
rename), `C16` (esclusione Hidden/System ereditata), `P2` (collation
`MaterializedPath`), filtro **dimensione** in UI (assente come quello data, ma non
è un bug di correttezza: annotarlo, non implementarlo).
