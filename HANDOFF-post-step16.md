# HANDOFF — dopo lo step 16 (2026-09-03)

> Scritto per essere letto **da una sessione pulita**, insieme a `CLAUDE.md`.
> Non sostituisce il brief: dice **dove siamo**, **cosa resta**, e — la parte che serve a
> pianificare — **cosa richiede l'elevazione e cosa la vieta**.

---

## Stato

**HEAD `8ffead6` su `develop`, albero pulito. Lo step 16 è chiuso e distribuito.**

| | |
|---|---|
| xUnit | **949 verdi** (baseline dello step: 882) |
| Vitest | 256 (frontend non toccato dal 15b) |
| Build | pulita, warnings-as-errors, **Debug e Release** |
| Harness sul ferro | **59 scenari, 59 PASS, 0 FAIL, 0 SKIP** — elevato, due passate |
| Servizio installato | gira **step 16** dal 2026-09-03, migration `AddFileExcludedByPath` applicata |
| Catalogo reale | 742 675 file · 114 212 directory · 742 669 FTS · 30 volumi · 0 violazioni FK |

**Lo step 16** ha chiuso le voci **A2** e **A3** della roadmap: una decisione di perimetro ora
raggiunge le righe che il catalogo ha già. Quarta causa `Files.ExcludedByPath` separata dagli
attributi, riconciliazione che sa disfare la metà path, e il delta USN che porta l'esclusione a
tutto il sottoalbero. Dettagli, misure e limiti nel paragrafo «Fatto nello step 16» di `CLAUDE.md`.

**Chiuso anche il debito più vecchio**: il **riavvio della macchina** è provato (boot 09:38:57 UTC,
prima riga del servizio 09:39:13, senza che nessuno lo avvii). Era l'ultima voce aperta dello
step 13.

---

## La domanda che decide il piano: elevato o no?

**Non tutto si può fare in una sessione sola, e non per pigrizia.** Due vincoli **opposti**:

- **Gli E2E si RIFIUTANO di partire da elevato** (`tests/e2e/src/global-setup.ts:45-63`, decisione
  del 12a). Il motivo è di prodotto, non di test: su NTFS la scansione *prova sempre* USN, e con i
  privilegi `EnsureJournal` **creerebbe un giornale sul volume di sistema** — una modifica
  persistente fuori dalla sandbox, fatta da un test. Non elevati, Windows rifiuta e il prodotto
  ripiega sull'enumerazione della sola cartella monitorata.
- **L'harness sul percorso USN PRETENDE l'elevazione.** Senza, il volume non si apre, ogni scan
  ripiega sull'enumerazione e i due scenari USN fanno **SKIP** invece di passare per la strada
  sbagliata. Idem per qualunque sonda che legga l'MFT (es. P1) e per **installare il servizio**.

**Un subagent NON eredita l'elevazione della sessione** (misurato il 2026-09-03: sessione padre
elevata, subagent con `IsInRole(Administrator)` a `False`). Quella metà non è delegabile.

### Cosa si può fare NON elevato — la maggior parte

| lavoro | note |
|---|---|
| **Passata E2E** (25 test) | **richiede** non elevato. Da `tests/e2e`, `npm test`, shell **con console** |
| Tutto xUnit / Vitest / build | indifferente |
| **Ri-scansione di `C:`** via API | è una POST su loopback; il *servizio* è già elevato per conto suo |
| Lettura del catalogo vivo in sola lettura | vedi la ricetta in memoria (`live-catalog-sql-probe`) |
| **C32** (label null nell'enqueue) | due righe, test in-process |
| **A5 Cloud**, metà diagnostica | logging dei segnali grezzi al `VolumeClassifier` |
| **Test sul piano di `InSubtree`** | in-process su fixture |
| Igiene C (`%TEMP%`, `StopAsync` sul factory) | in-process |
| Decisioni di prodotto (paging sottocartelle, filtro dimensione) | frontend + API |

### Cosa PRETENDE l'elevazione

| lavoro | perché |
|---|---|
| Harness con gli scenari USN | senza giornale fanno SKIP, e un PASS proverebbe l'enumerazione |
| **A4** (scelta del motore sui sotto-alberi) | è *sul* percorso USN: va provato sul giornale vero |
| **P1** (sonda hard link sull'MFT) | legge lo snapshot MFT |
| Deploy (`install-service.ps1`) | registra il servizio e scrive in `Program Files` |

### Piano consigliato, in due sessioni

1. **Sessione NON elevata, la più grossa.** E2E per primi (sono l'unica cosa bloccata dall'altro
   verso, e il codice del 15b non li ha mai attraversati), poi i lavori piccoli e indipendenti.
2. **Sessione elevata, corta.** Solo ciò che tocca il giornale o installa: harness a fine lavoro,
   eventuale A4/P1, deploy.

---

## Cosa resta, in ordine di valore

### 1. Una passata E2E — **il buco più vero**
25 test, 10 spec, sviluppati in 12a/12b. **Non girano dal 2026-08-21**, cioè da otto step.
Nel frattempo il **frontend è cambiato nel 15b** (la corsa dell'auto-selezione su Volumi) e quella
modifica **non è mai stata attraversata dagli E2E**. È l'unico livello che tocca lo schermo.

**Come si lanciano** (costato una passata rossa da 22 minuti a chi non lo sapeva): da `tests/e2e`,
`npm test`, da una shell **con console**. `scripts/stop-host.ps1` spegne il Host con
`AttachConsole` + `GenerateConsoleCtrlEvent`; da una shell staccata non c'è console a cui
agganciarsi, il Ctrl+C non arriva, e **tutti e 25 falliscono sul teardown** per un motivo che non
è loro. Un fallimento di massa con quel messaggio va letto come «lanciata male».

### 2. Difetti noti, nessuno MVP
- **A4 — l'USN perde sui sotto-alberi.** `FSCTL_ENUM_USN_DATA` cammina tutta l'MFT ignorando il
  perimetro (misurato in 14d). **Attenzione, è più di un'ottimizzazione**: un volume scansionato per
  enumerazione non ha cursore, quindi non ha l'incrementale. L'euristica non sceglie «cosa è più
  veloce oggi», sceglie **se quel volume avrà i delta**. Su `D:` la spegnerebbe. È una decisione di
  prodotto prima che un fix.
- **A5 — classificazione Cloud** (debito datato 6.7). Coperto dall'esclusione manuale, che funziona.
  Primo passo è **diagnostica**, non fix: loggare i segnali grezzi e vedere su quale ramo cade.
- **C32** — `SourceVolumeLabel`/`TargetVolumeLabel` null nella risposta di enqueue. Innocuo.
- **P1** — hard link nello snapshot MFT: `nodes[frn]` è last-write-wins, sopravvive un path solo.
  **Prima misurare** (quanti FRN duplicati esistono davvero), poi decidere: lo schema assume
  *un FRN = una riga* (`(VolumeId, UsnFileRef)` è **unique**), quindi il fix vero è un cambio di
  modello.

### 3. I tre residui dichiarati dello step 16
Stanno nei limiti di `TASK-step16-perimeter-reaches-the-catalog.md` e accanto al `KNOWN HOLE` di
`UsnDeltaApplier`. **Vanno letti insieme**: radice condivisa, chiusure diverse.
- il traffico di scrittura normale **disfa** l'esclusione per attributi;
- il delta **fa crescere** il catalogo dentro il sottoalbero escluso;
- il caso **stesso-tick** (cartella spostata dentro quella appena esclusa).

I primi due richiedono un fatto che il catalogo **non ha** — un flag di inclusione sulle
`Directories`, che **11g ha deciso di non avere**: è una decisione di prodotto, non un fix. Il
terzo si chiude **dentro `Classify`**, ed è l'unico dei tre alla portata di un giro normale.

### 4. Debito nuovo, piccolo, creato da questo giro
**Nessun test pinna il piano di `InSubtree`.** Il deploy ha misurato che **non** è una seek su
`IX_Directories_MaterializedPath` ma uno `SEARCH … USING INDEX IX_Directories_VolumeId_ParentId`
(SQLite non guida un indice di prefisso da un `LIKE` con pattern **parametro**): 31 ms per directory
esclusa sulle 113 831 del volume di sistema. Il commento è stato corretto; la guardia manca.

### 5. Decisioni di prodotto ferme in attesa dell'utente
- **Paging delle sottocartelle del Catalogo** — oggi illimitato (E5, §7). Cambia
  `CatalogChildrenDto` e la schermata.
- **Filtro dimensione in Ricerca** — `SizeBytesMin/Max` esistono nell'API e nello store, non a video.
- **Accendere l'incrementale su `C:`** — deciso **NO** il 2026-08-27, e la decisione regge: costa
  una camminata completa dell'MFT per indicizzare tre sottoalberi. Conseguenza da sapere: il pass
  di sottoalbero di A3 **non gira da solo su `C:`**, perché `UsnJournalId` è `NULL` lì.

### 6. Igiene tollerata
`%TEMP%` accumula `ft-test-*-logs.db` (costa una `StopAsync` sul factory) · `ScenarioEnvironment`
pulisce tutti i pool SQLite · `IndexUpdater` col filtro di default fuori root · gli handler di
**Move** non scrivono cause di perimetro · gate offline prima del refresh degli snapshot.

---

## Regole operative apprese, da non ripagare

- **Un solo `dotnet` alla volta.** Build o test in parallelo portano lo spawn dei processi da
  ~25 ms a **2–22 secondi**: la suite passa da 30 s a ore, i test temporali cadono a caso e ogni
  numero misurato descrive la macchina invece del codice. **Non uccidere processi che non sono
  tuoi** — un `dotnet build` di un'altra solution è lavoro dell'utente.
- **Prima di scrivere cosa produce una mutazione, eseguila.** Lo step 16 ha pagato **tre volte**
  per affermazioni plausibili non verificate, e due di quelle erano la *correzione* di
  un'affermazione sbagliata.
- **Conta i rossi sull'intera suite, non sulla classe.** Due dei difetti peggiori dello step erano
  mutazioni che lasciavano verdi **tutti** i test.
- **Un limite dichiarato è un contratto**: se lo trovi, verifica che sia *esatto*, non ri-segnalarlo.
- Log in **UTC**, tempi di processo in **locale**: leggerne uno contro l'altro fa sembrare fallito
  un avvio automatico che è andato benissimo.
