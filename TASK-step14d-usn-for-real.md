# TASK — Step 14d: l'USN fatto sul serio (dimensioni dall'MFT, journal id, worker incrementale)

> **Sessione dedicata, agente singolo, macchina ELEVATA.** È il pezzo grosso: rende vero il
> nodo tecnico n° 2 del §1, che oggi **non esiste nel prodotto**.
> Prerequisito: working tree pulito, suite verde. Va eseguito **dopo** 14a/14b/14c, che
> chiudono difetti che l'utente incontra oggi.
> Riferimenti: `CLAUDE.md` §1.2, §3 (BackgroundServices, layering), §4 (motori di scansione),
> §6 (schema `Volumes`), «Fatto nello step 13».
> ⚠️ Servizio installato e attivo sul catalogo reale: fermalo prima di ricompilare.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.
> **Per densità, fermati ai checkpoint indicati**: sono tre pezzi, ognuno chiude verde da solo.

## Cosa lo step 13 ha scoperto, e perché questo task esiste

Tre fatti misurati, non ipotesi:

1. **L'USN non vince, a nessuna scala.** Su `D:` intero (51 710 file + 6 782 directory,
   indicizzati **identici** dai due motori): USN **4,88 s** contro enumerazione **3,74 s**.
   La camminata MFT *è* più veloce (0,57 s contro 1,06 s) ed *è* costo fisso — ma i record
   MFT **non portano dimensione né date**, quindi `ScanService.ResolveFilesAsync` interroga
   il filesystem **file per file** attraverso `IFileMetadataReader`: la fase
   `ReadingMetadata`, **0,53 s per 51 710 file**, che sul ramo a enumerazione non esiste
   (le dimensioni arrivano gratis dalla camminata). Risparmio ≈8 µs/voce contro costo
   ≈10 µs/file: stesso ordine, crescono insieme.
2. **Il percorso incrementale non esiste.** `IUsnReader.ReadChanges` è implementato e testato
   ma **non ha alcun chiamante di prodotto**; `ScanWorker` prende solo i volumi con
   `LastFullScanUtc == null` più le ri-scansioni esplicite; `ScanService` fa **sempre**
   snapshot pieno + merge. Il **`UsnSyncWorker`** che §3 elenca fra i BackgroundServices
   **non è mai stato scritto** (verificato: zero occorrenze in `src/backend`).
3. **Manca la colonna del journal id.** `Volumes` persiste `LastUsn` ma non l'id del
   giornale, e `ReadChanges` ne ha bisogno per accorgersi dell'invalidazione. Senza quella
   colonna il worker incrementale non è nemmeno scrivibile.

Provato sul ferro: host fermo, due file creati + uno rinominato + uno cancellato **fuori
dall'app**, host riavviato, lasciato in pace 75 s → catalogo fermo a 501, `LastUsn`
invariato, nessuna scansione. Una ri-scansione esplicita converge perfettamente, ma
camminando **tutto** l'MFT.

## Checkpoint 1 — le dimensioni arrivano dall'MFT

È ciò che rende l'USN sensato: senza, il motore paga una `stat` per file e non ha motivo di
esistere.

`FSCTL_ENUM_USN_DATA` **non** espone dimensione e date: i record `USN_RECORD` portano nome,
FRN, parent FRN, attributi, timestamp del **record** (non del file) e i flag di motivo.
Servono i due attributi dell'MFT — `$STANDARD_INFORMATION` (date) e `$DATA` (dimensione).

Decidi e motiva la strada:
- lettura diretta degli attributi MFT (potente, invasiva, e da trattare con rispetto: è
  parsing di strutture su disco);
- oppure una lettura di metadati **più economica** dell'attuale per-file (batch, riuso
  dell'handle, `GetFileInformationByHandleEx` su directory, ecc.), che non è la soluzione
  bella ma potrebbe chiudere il divario;
- oppure — esito legittimo — **la conclusione che non conviene**, scritta con i numeri, e
  §1.2 corretto di conseguenza. Se questa è la risposta, il resto del task cade e il brief va
  aggiornato: sarebbe una decisione di prodotto e va portata all'utente, non presa da solo.

Criterio: sullo **stesso** volume `D:`, l'USN deve battere l'enumerazione. Se non lo fa, non
è chiuso.

## Checkpoint 2 — `Volumes` ricorda il giornale

Colonna nuova per l'id del journal (+ migration, backfill nullo: si popola alla prima
lettura). Serve a `ReadChanges` per distinguere «continua da dove eravamo» da «questo
giornale non è più quello di prima, rifai tutto». Lo step 13 ha **osservato** il caso reale:
journal cancellato con `fsutil`, ricreato dal prodotto con id nuovo, scansione completa e
file nuovo recepito — ma la decisione oggi la prende `EnsureJournal`, non il confronto degli
id, perché l'id non è persistito.

## Checkpoint 3 — `UsnSyncWorker`

Il BackgroundService che §3 promette da sempre: per ogni volume NTFS online con
`LastFullScanUtc` valorizzato e un `LastUsn` + journal id coerenti, legge il delta e lo
applica all'indice **senza** camminare l'MFT.

Punti che decidono se è fatto bene:
- **applicare un delta non è applicare uno snapshot**: create/rename/delete/move arrivano
  come record con motivi, e vanno tradotti nelle stesse operazioni che il merge di 9a fa
  sull'indice — riusando quel percorso, non scrivendone un secondo;
- **`IsPresent` e `IsIncluded` mantengono la semantica degli step 11g/11h**: un file
  cancellato dal disco è `IsPresent=false`, un file fuori perimetro è `IsIncluded=false` con
  le sue cause. Il delta deve rispettare il perimetro, non aggirarlo;
- **checkpoint di `LastUsn` in transazione**, come i checkpoint di scansione (9a): un crash a
  metà non deve dichiarare consumato un delta che non è stato applicato;
- **invalidazione**: journal cancellato, sorpassato o volume rimontato → si ripiega su una
  scansione completa, rumorosamente (log + Notification, §9), mai in silenzio;
- il worker rispetta `ApplicationStopping` e non tiene lo shutdown (§3, e vedi 14b).

## Vincoli

- Tutta la P/Invoke resta in `Platform` dietro `IUsnReader` (§3): `Business` non vede Win32.
- **`fsutil usn deletejournal` solo su un volume di test, mai su `C:` né `D:`.**
- Nessuna operazione di coda sui dati veri.
- La scansione completa resta il fallback per exFAT/FAT32 e per ogni caso di invalidazione:
  non si toglie niente, si aggiunge una strada più corta.

## Test

- **Platform**: già esiste `ReadChanges_sees_work_done_outside_the_application` (step 13).
  Aggiungere i casi di invalidazione (id cambiato, `sinceUsn` sorpassato).
- **Business**: applicazione di un delta all'indice, con create/rename/delete/move, che
  produce lo **stesso stato** di una scansione completa sugli stessi fatti. È l'asserzione
  che conta: il percorso corto e quello lungo devono convergere.
- **Host**: il worker che parte, consuma, checkpointa e riprende dopo un restart.
- **Harness sul ferro, elevato**: uno scenario che modifica file **fuori dall'app** e pretende
  che l'indice converga **senza** una scansione completa (baseline 47/47).

## Definition of done

- Il criterio del checkpoint 1 raggiunto (USN più veloce dell'enumerazione su `D:`), oppure
  la conclusione contraria documentata e portata all'utente.
- I tre checkpoint verdi, ognuno con i suoi test.
- **Code review finale** indipendente, con attenzione a crash-safety e alla convergenza
  delta ↔ scansione completa.
- `CLAUDE.md`: paragrafo «Fatto nello step 14d», §3 e §1.2 allineati a ciò che il codice fa
  davvero, e le voci 4 e 5 del lavoro successivo dello step 13 marcate chiuse.
