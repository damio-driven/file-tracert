# TASK — Step 11b: fattibilità a spazio, probe live + margine (WP6)

> **Sessione dedicata, agente singolo.** Secondo dei sei task dei WP minori
> (`TASK-step11-overview.md`). Prerequisito: 11a mergiato, suite verde, Host chiuso.
> Riferimenti: `CLAUDE.md` §4 («Fattibilità a spazio», *«Mai copiare sulla fiducia di una
> stima»*), §7 (fattibilità come oggetto), §3 (layering: `Business` → `Contracts`);
> `CODE-REVIEW-HANDOFF.md` → finding 10.
> ⚠️ Tocca `JobExecutionEngine` e `SpaceLedger`: **niente parallelo** su questi file.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

§4 dice due cose che oggi non succedono:

1. **prima dell'esecuzione reale, ricontrollo *hard* del free space + margine (2–5%)**;
2. il ricontrollo dev'essere **reale**, non una rilettura della stima.

Stato verificato su `88571aa` (riverificare le righe):

- `JobExecutionEngine.ExecuteCrossVolumeAsync` (~:224) passa a `ComputeFeasibilityAsync`
  il valore `tgtVol.FreeBytesLastKnown`, cioè l'**ultimo valore noto** scritto dall'ultimo
  `VolumeSync`. Tra il sync e la copia un altro processo può aver scritto decine di GB:
  il job passa il check e muore disk-full a metà. È esattamente il «copiare sulla fiducia
  di una stima» vietato da §4.
- `SpaceLedger.Compute` (~:191-224) non ha alcun termine di margine.
- `AppSettings.SpaceMarginPercent` è seedato a **3** in `DatabaseInitializer.cs:~103` e ha
  **zero consumatori** (grep: entità + migration + seed, nient'altro). È una manopola che
  non muove niente.
- `IVolumeProbe` (`Contracts/Platform/IVolumeProbe.cs`) espone `EnumerateVolumes()` e
  `TryGetByGuid(volumeGuid)`; `ProbedVolume` porta già i byte liberi (lo usa
  `VolumeMapper`), quindi **la port basta così** — se aggiungi un metodo più mirato
  (es. `TryGetFreeBytes`), motivalo: enumerare tutti i volumi per leggerne uno è caro.

Il ledger resta il posto giusto per la contabilità delle prenotazioni: quello che cambia è
**da dove arriva il numero del libero fisico** e che al confronto si applica un margine.

## Lavoro

### 1. Probe live al momento dell'esecuzione

Nel ricontrollo hard, leggere i byte liberi **dal disco** via `IVolumeProbe` invece che da
`FreeBytesLastKnown`. Regole:

- **`Business` non conosce Win32**: si passa dalla port, che è in `Contracts` (§3).
- Volume **offline o non risolvibile**: non è un `Failed`. Il gate offline (WP2) risponde
  già prima; se ciononostante il probe non torna nulla, → `Blocked` con motivo, non
  eccezione (§4: `Blocked` riattivabile per condizioni recuperabili).
- Il valore letto vale la pena di **aggiornare anche `FreeBytesLastKnown`** (è più fresco
  di quello che c'è). Attenzione a non doppiare il decremento già fatto a fine job
  (`JobExecutionEngine` ~:742): se il valore ora **deriva dal probe**, il decremento
  accumulato diventa ridondante — decidere quale dei due sopravvive e scriverlo nel commit.

### 2. Margine da `AppSettings`

`SpaceMarginPercent` diventa un consumatore vero: la fattibilità **hard** richiede
`required + margine` byte liberi, dove il margine è una percentuale del richiesto (o del
libero: scegli, motiva, scrivilo nel commento — sono due politiche diverse e la differenza
si vede sui job grandi).

Dove applicarlo:
- **sempre** nel ricontrollo di esecuzione;
- nella preview / enqueue: decidere se il margine entra anche lì. Coerenza con §4
  («non rifiutare mai un job all'enqueue») dice che l'enqueue può usarlo per marcare
  `Blocked`, mai per rifiutare.

Il margine deve arrivare al ledger senza che `SpaceLedger` legga da solo il DB delle
settings a ogni chiamata: passalo o iniettalo dietro un accessor già esistente, e ricorda
che il ledger è un **singleton thread-safe** (§3, «Concorrenza»).

### 3. `FeasibilityResult` racconta la verità

§7 vuole la fattibilità come oggetto. Se il numero mostrato all'utente ora viene dal probe
invece che dall'ultimo valore noto, i campi devono dirlo: `estimateIsLive` deve essere
**vero solo quando lo è davvero**. Verificare che il DTO esposto in preview e nella Coda
non dichiari live un dato che live non è (stesso principio dell'indicatore di connessione
dello step 10c: mai vestire da fresco un dato vecchio).

## Split dei commit (indicativo)

1. `feat(business): the hard space re-check reads the disk, not the last-known value`.
2. `feat(business): SpaceMarginPercent finally gates the hard check`.
3. `fix(business): feasibility reports whether its number is live` (se il DTO cambia).

## Test (RED prima del GREEN)

Contro l'implementazione reale (ledger vero, SQLite vero, `IVolumeProbe` sostituito da un
fake **della piattaforma**, che è lecito: il componente sotto esame è il ledger/engine, non
il probe):

- probe che riporta **meno** spazio di `FreeBytesLastKnown` → il job va `Blocked`
  (`InsufficientSpace`) invece di partire. Oggi parte: è il RED.
- probe che riporta **più** spazio → un job che la stima dichiarava infattibile riparte.
- margine: richiesta che entra **senza** margine e non entra **con** margine 3% → `Blocked`;
  con margine 0 → passa. Prova che la manopola muove qualcosa.
- probe che non risolve il volume → `Blocked` con messaggio, **mai** `Failed`, e la riserva
  gestita come negli altri percorsi di blocco (nessuna riserva fantasma: vedi finding 5,
  già chiuso in WP1 — non riaprirlo).

## Harness sul ferro (obbligatorio)

Esiste già `SpaceScenarios`: estenderlo o aggiungere uno scenario che riempie il volume
target **dopo** l'enqueue (file di zavorra scritti fuori dall'app, che è precisamente il
caso reale) e verifica che il job si blocchi invece di rompersi a metà copia; poi libera
lo spazio e verifica che riparta alla rivalutazione. Riportare PASS/FAIL e i numeri.
Rimettere `appsettings.json` come stava a fine collaudo.

## Definition of done

- xUnit verde, build backend pulita (warnings-as-errors).
- Harness: scenario spazio PASS sul ferro, nessun FAIL nuovo.
- **Code review finale** indipendente: correttezza vs scenari di fallimento, no silent
  catch (§9), layering (§3 — nessuna P/Invoke fuori da `Platform`), niente duplicazione,
  crash-safety invariata sulla state machine. Riportare rilievi e correzioni.
- `CLAUDE.md`: paragrafo «Fatto nello step 11b»; in `CODE-REVIEW-HANDOFF.md` marcare il
  finding 10 come chiuso.
