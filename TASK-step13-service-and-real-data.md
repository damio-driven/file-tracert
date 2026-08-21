# TASK — Step 13: il prodotto installato, sui dati veri, con l'USN alla scala giusta

> **Sessione dedicata, agente singolo, macchina ELEVATA.** Primo passo fuori dall'MVP: non
> aggiunge funzionalità, mette alla prova le due fondamenta che nessun test ha ancora
> toccato — il **Windows Service** e il **motore USN su un volume vero**.
> Prerequisito: step 12 chiuso, suite verde, working tree pulito.
> Riferimenti: `CLAUDE.md` §1 (i tre nodi tecnici), §3 (hosting: servizio elevato, Kestrel
> su loopback, security locale), §4 (motori di scansione), §6 (schema, retention log).
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

Il collaudo elevato del 2026-08-21 ha provato che `NtfsUsnReader` funziona (5 test reali in
17 s sull'MFT di `C:`, harness 47/47 **senza** un solo fallback a enumerazione). Ma ha anche
lasciato un numero che non torna: su 2 002 file l'USN costa **7,56 s** al primo scan e
**2,07 s** al re-scan, contro **1,05 s / 0,83 s** dell'enumerazione. Il motore «efficiente»
è quattro volte più lento di quello che deve sostituire.

L'ipotesi è benigna — `FSCTL_ENUM_USN_DATA` cammina l'**intera MFT del volume**, non la
cartella sorvegliata, quindi è costo fisso che si ammortizza sulla scala — ma **è
un'ipotesi**, e finché lo resta non abbiamo una baseline. Si verifica in un modo solo:
misurando su un volume vero, con centinaia di migliaia di file.

Nello stesso giro va provato ciò che il brief dà per scontato da §3 e che non è mai
successo: **l'app installata come servizio Windows**, che parte da sola, elevata, e
indicizza i dati reali dell'utente per giorni.

## Regola di sicurezza che governa tutto il task

**Questo step INDICIZZA. Non sposta, non rinomina, non cancella nulla dei dati veri.**
La scansione è read-only per costruzione; la coda no. Quindi:

- nessun job accodato su file dell'utente, in nessuna forma, nemmeno «per provare»;
- la coda si esercita **solo** dentro una cartella scratch che l'utente designa, e questo
  resta un extra, non l'obiettivo del giro;
- prima di installare il servizio: verificare che nessun `ScanWorker` parta su volumi che
  l'utente non ha scelto (i watched root li decide lui, e `IsCatalogable` di default va
  rispettato).

Se una verifica richiede di scrivere sui dati reali, **non farla**: annota cosa resterebbe
da provare e vai avanti.

## 0. La domanda che viene prima del codice (già posta all'utente)

`DatabaseLocation.Resolve` usa `%LOCALAPPDATA%\FileTracert\filetracert.db`. Un servizio
Windows gira come **LocalSystem**, per cui quel percorso diventa
`C:\Windows\System32\config\systemprofile\AppData\Local\…`: **il servizio partirebbe su un
catalogo vuoto**, diverso da quello che le esecuzioni in console hanno costruito dal 2026-07
(≈742 000 file). Non è un dettaglio di deployment: decide dove vivono i dati dell'utente.

La risposta dell'utente è riportata nel prompt di questo task e va implementata così com'è.
Le tre strade erano: (a) DB **machine-wide** in `%ProgramData%\FileTracert` — la posizione
giusta per un servizio, e il catalogo esistente si **migra** una volta sola; (b) servizio
che gira **come l'utente** invece che LocalSystem, così `%LOCALAPPDATA%` resta quello di
prima; (c) percorso **esplicito** in configurazione, senza default implicito.

Qualunque sia, il codice deve renderla **evidente**: chi legge `DatabaseLocation` fra un
anno deve capire dove finisce il DB in servizio e perché, senza dedurlo.

## 1. Publish e installazione ripetibili

Oggi non esiste nulla che installi il prodotto: `AddWindowsService` è cablato, ma non c'è
publish, non c'è `sc create`, e `wwwroot` è popolato a mano da uno script di test.

Serve:
- una **build di publish** che produca l'eseguibile **con la SPA dentro `wwwroot`** — la
  copia dal `dist` di Angular deve essere parte della build, non un gesto manuale;
- **install / uninstall** (script PowerShell, elevati): registrazione del servizio, avvio
  automatico, avvio e arresto, disinstallazione che **non lascia niente dietro** tranne il
  database (che è dell'utente: mai cancellarlo senza chiederlo);
- un **README di installazione** breve: cosa fa, dove mette i dati, come si disinstalla,
  come si raggiunge la UI, e che il servizio ascolta **solo su loopback** (§3).

Verifica: installato, il servizio sopravvive a un **riavvio della macchina** e la UI si apre
sull'istanza servita da lui, non su un residuo in console.

## 2. USN sul serio: i quattro comportamenti mai visti

Con il servizio elevato e un watched root scelto dall'utente su un volume NTFS grande:

1. **Primo scan via MFT** (`FSCTL_ENUM_USN_DATA`): completa, e i path ricostruiti dai
   `ParentFileReferenceNumber` corrispondono al disco. Verifica a campione, non a fiducia.
2. **Delta incrementale** (`FSCTL_READ_USN_JOURNAL`): si modificano file **fuori dall'app**
   (crea, rinomina, sposta, cancella) e la scansione successiva li recepisce **senza**
   ri-camminare tutto. È la promessa di §1.2 e non è mai stata osservata.
3. **`LastUsn` persistito e ripreso**: si ferma il servizio, si modifica altro, si riavvia →
   riprende dall'USN salvato, non da zero.
4. **Journal azzerato o sorpassato**: il caso di errore che nessuno ha mai provato. Si
   simula con `fsutil usn deletejournal` **su un volume di test, mai su `C:`** (e
   `createjournal` per rimetterlo). L'app deve accorgersene e **ripiegare su una scansione
   completa**, non fallire in silenzio né dichiarare aggiornato un indice che non lo è.

## 3. La misura che risponde alla domanda aperta

Sullo **stesso** volume grande, stessi file:
- scan con **USN**, primo e incrementale;
- scan con **enumerazione** (si ottiene senza privilegi, oppure forzando il fallback);
- numero di file, tempo, e — se lo si può isolare — quanto costa la sola camminata MFT.

L'esito atteso è che l'USN vinca alla scala e perda in piccolo, che è esattamente il motivo
per cui esistono due motori. **Se non fosse così**, è una scoperta importante: significa che
la scelta architetturale di §1.2 va rivista, e va scritta come tale invece di essere
arrotondata.

## 4. Soak: cosa succede in giorni, non in secondi

Con il servizio che gira sui dati reali:
- **crescita del database** (righe, MB, dimensione del WAL) e costo di un re-scan a quella
  scala — le misure storiche sono su 2 002 file, tre ordini di grandezza sotto;
- **retention dei log** (§6): il DB dei log si trimma davvero, o cresce senza limite?
- **memoria e CPU** del servizio a riposo e durante uno scan;
- **le schermate** con un catalogo vero: il Catalogo con cartelle da decine di migliaia di
  figli (il paging delle sottocartelle è un limite noto aperto dallo step 11e), la Ricerca
  FTS su centinaia di migliaia di righe, la Dashboard.

Qui non si scrive codice: si guarda, si misura e si riporta. Ciò che emerge diventa il
lavoro successivo — e se emerge qualcosa di grave, **si ferma e si dice**, non si aggiusta
di corsa dentro questo giro.

## Split dei commit (indicativo)

1. `feat(host): the database has one home, and it survives running as a service`.
2. `build: publish the Host with the SPA inside wwwroot`.
3. `feat(deploy): install and uninstall the Windows Service`.
4. `docs: how to install FileTracert, and where it puts your data`.
5. `test(harness): the USN journal reset falls back to a full scan` (se il caso 2.4 si
   copre con uno scenario, ed è preferibile a una prova manuale).

## Verifica

- La suite resta verde (xUnit, Vitest, E2E **non elevati**: il `globalSetup` rifiuta
  l'elevazione per progetto), build pulita.
- Harness sul ferro con l'USN attivo: baseline **47/47 PASS**, `appsettings.json` rimesso
  byte-identico (sha256 `653f5990…`).
- Servizio installato, riavvio della macchina superato, UI raggiungibile, disinstallazione
  pulita.
- Tutte le misure del §3 e del §4 riportate con i numeri, non con aggettivi.

## Definition of done

- Il prodotto si installa e si disinstalla con un comando documentato.
- I quattro comportamenti USN del §2 sono **osservati**, ognuno con la sua prova.
- La domanda sulla lentezza dell'USN in piccolo ha una **risposta misurata**.
- **Code review finale** indipendente sulle modifiche di codice del giro (non sulle misure).
- `CLAUDE.md`: paragrafo «Fatto nello step 13» con le misure, la decisione sul percorso del
  database, e ciò che il soak ha fatto emergere come lavoro successivo.
