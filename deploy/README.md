# Installare FileTracert come servizio Windows

> **by FAD.iT** — questa cartella contiene tutto ciò che serve a mettere il prodotto su una
> macchina e a toglierlo. Due script, nessun installer, nessuna dipendenza esterna.

## In breve

```powershell
# da un PowerShell ELEVATO, nella radice del repository
powershell -ExecutionPolicy Bypass -File deploy\install-service.ps1
```

Al termine lo script stampa dove ha messo le cose e l'indirizzo della UI:

```
FileTracert is installed and running.
  UI            http://127.0.0.1:5005/   (loopback only)
  Binaries      C:\Program Files\FileTracert
  Data          C:\ProgramData\FileTracert
```

Per toglierlo:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\uninstall-service.ps1
```

## Cosa fa `install-service.ps1`

1. **Pretende l'elevazione.** Registrare un servizio la richiede, e la richiedono anche le due
   cose per cui il prodotto esiste: leggere il **giornale USN** e spostare file dell'utente.
2. **Pubblica il Host, SPA compresa.** `dotnet publish` esegue anche `ng build`, che scrive la
   SPA in `wwwroot`; il publish fallisce se `wwwroot\index.html` non c'è, perché un eseguibile
   che serve una pagina bianca non è un artefatto installabile. Con `-SourcePublishDir <cartella>`
   si usa una publish già fatta e non si compila niente.
3. **Copia in `%ProgramFiles%\FileTracert`** con `robocopy /MIR`, così un aggiornamento non lascia
   dietro pezzi della SPA precedente che il nuovo `index.html` non nomina più.
4. **Prepara `%ProgramData%\FileTracert`** (vedi *Dove finiscono i dati*).
5. **Registra il servizio** `FileTracert`, avvio **automatico**, account **LocalSystem**, con
   riavvio automatico a fronte di tre errori consecutivi e poi stop (un servizio che va in
   crash-loop su un database corrotto deve smettere di riprovare ed essere visibile come fermo).
6. **Lo avvia e aspetta che risponda** su `http://127.0.0.1:<porta>/`. Se il servizio parte e
   muore subito, lo script lo dice invece di dichiarare un successo.

Rieseguirlo **aggiorna** un'installazione esistente: ferma il servizio, sostituisce i file, lo
riavvia. Il database non viene mai toccato.

### Parametri

| Parametro | Default | A cosa serve |
|---|---|---|
| `-SourcePublishDir` | *(vuoto: compila)* | Usa una publish già pronta invece di compilarne una. |
| `-InstallRoot` | `%ProgramFiles%\FileTracert` | Dove vanno i binari. |
| `-DataRoot` | `%ProgramData%\FileTracert` | Cartella dati a cui concedere i permessi. |
| `-Port` | `5005` | Porta di loopback, scritta nell'`appsettings.json` installato. |

## Dove finiscono i dati

**`%ProgramData%\FileTracert`** — cioè `C:\ProgramData\FileTracert`:

| File | Cos'è |
|---|---|
| `filetracert.db` | Il **catalogo**: volumi, cartelle monitorate, file indicizzati, coda operazioni, indice di ricerca FTS5. |
| `filetracert-logs.db` | Database dei **log** dell'applicazione, separato dal catalogo e con retention propria. |
| `*-wal`, `*-shm` | File di appoggio di SQLite (modalità WAL). Non si cancellano a mano. |

È una posizione **machine-wide**, ed è una scelta, non un default preso a caso: il servizio gira
come `LocalSystem`, per cui `%LOCALAPPDATA%` diventerebbe
`C:\Windows\System32\config\systemprofile\AppData\Local` — il servizio partirebbe su un catalogo
**vuoto**, diverso da quello costruito da un'esecuzione in console, senza che niente lo dica.

Lo script concede **Modify al gruppo `Users`** su quella cartella. Il motivo: il servizio scrive
come `LocalSystem`, ma lo stesso file deve poter essere aperto da un'esecuzione in console per
diagnostica o dall'harness hardware, che girano come l'utente connesso. I permessi ereditati da
`ProgramData` darebbero a `Users` la sola lettura, e quel secondo lettore fallirebbe su un database
che è suo. È un compromesso deliberato, sostenibile perché la macchina è personale e single-user e
la UI ascolta **solo** su loopback.

### Se avevi già un catalogo in `%LOCALAPPDATA%\FileTracert`

Va **copiato** una volta sola, a servizio fermo:

```powershell
Stop-Service FileTracert -ErrorAction SilentlyContinue
Copy-Item "$env:LOCALAPPDATA\FileTracert\filetracert.db*"      "$env:ProgramData\FileTracert\" -Force
Copy-Item "$env:LOCALAPPDATA\FileTracert\filetracert-logs.db*" "$env:ProgramData\FileTracert\" -Force
Start-Service FileTracert
```

Copiare, non spostare: l'originale resta dov'è come rete di sicurezza. Copiare **anche** i file
`-wal` e `-shm` se ci sono, altrimenti si perdono le transazioni che stanno lì dentro. Nessuno
script fa questa migrazione da solo: spostare il catalogo di un utente è un'operazione deliberata,
non qualcosa che un servizio decide all'avvio, dove un file copiato a metà è un catalogo corrotto.

## Raggiungere la UI

`http://127.0.0.1:5005/` — il servizio ascolta **solo su loopback** e non è raggiungibile dalla
rete. Ogni chiamata all'API richiede un token generato all'avvio: la pagina lo riceve dal Host
stesso (timbrato in `index.html`), quindi aprire l'indirizzo nel browser basta. Una chiamata senza
token riceve `401`.

## Cosa fa `uninstall-service.ps1`

Ferma il servizio, ne cancella la registrazione, rimuove `%ProgramFiles%\FileTracert` — e
**lascia il database dov'è**. Il catalogo è dell'utente, costruito in settimane: disinstallare non
è chiedere di buttarlo. Per cancellarlo serve dirlo:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\uninstall-service.ps1 -RemoveData
```

che chiede conferma scrivendo per esteso quale cartella sta per cancellare (`-Force` salta la
domanda). Se al momento della disinstallazione qualcosa tiene aperto un handle sul servizio
(`services.msc`, il Visualizzatore eventi), Windows lo marca «in eliminazione» e lo toglie al
riavvio successivo: lo script lo segnala invece di riportare una disinstallazione pulita che non è
ancora avvenuta.

## Diagnostica rapida

```powershell
Get-Service FileTracert                       # stato
Get-Content "$env:ProgramData\FileTracert\*"  # (i log sono in SQLite, si leggono dalla UI → Log)
sc.exe qc FileTracert                         # percorso binario, tipo di avvio, account
```

I log applicativi si leggono dalla schermata **Log** della UI. Se il servizio non parte affatto, il
motivo è nel registro eventi di Windows (`Get-EventLog -LogName Application -Source FileTracert`)
e nel database dei log, che viene drenato anche quando l'avvio fallisce.
