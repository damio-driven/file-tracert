# TASK — Step 10a: Device watcher (push al posto del polling)

> **Sessione dedicata, agente singolo.** Primo dei tre checkpoint dello step 10.
> Riferimenti: `CLAUDE.md` §3 (layering), §4 (identità volumi, coda), §9 (standard, no
> silent catch), §10 punto 10, roadmap «Prossimo, in ordine → 1. Step 10».
> **NON** anticipare 10b (SignalR) né 10c (frontend): questo checkpoint è solo backend
> Platform + Host, e si chiude verde da solo.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

Oggi il remount di un drive viene notato solo dal `VolumeSyncWorker`, che gira ogni
**60 s** (`FileTracertOptions.VolumeSyncIntervalSeconds`, default 60). È quel worker a
risvegliare i job parcheggiati offline:

- `src/backend/FileTracert.Host/Workers/VolumeSyncWorker.cs` — `SyncOnceAsync` chiama
  `VolumeSyncService.SyncAsync` → se qualche volume è tornato online chiama
  `BlockedJobRevaluator.RevaluateAsync` → se ha sbloccato qualcosa chiama
  `IQueueSignal.Signal()`. Il commento nel file lo dice esplicitamente: *«Polling today;
  step 10 replaces this trigger with the device-watcher push»*.

Risultato per l'utente: ricollego il disco e non succede niente per un tempo che può
arrivare a un minuto. La promessa del §1 («eseguire appena i volumi tornano disponibili»)
si sente lenta anche quando è corretta.

Obiettivo di 10a: **l'evento di sistema fa da trigger**, il polling resta solo come rete
di sicurezza.

## Cosa esiste già (verificato su HEAD `9cdcd50` — riverificare file:riga prima di editare)

- `FileTracert.Contracts/Platform/` — le port interface verso la piattaforma
  (`IVolumeProbe`, `IUsnReader`, `IFileMover`, `IDirectoryEnumerator`, …). `IDeviceWatcher`
  **non esiste**: è citato in `CLAUDE.md` §3 ma non è mai stato scritto.
- `FileTracert.Platform/Interop/NativeMethods*.cs` — P/Invoke **source-generated**
  (`[LibraryImport]`, `internal static partial class`). Seguire quello stile, non `DllImport`.
- `FileTracert.Platform/PlatformServiceCollectionExtensions.cs` — registra le implementazioni
  come singleton. Il nuovo watcher va qui.
- `FileTracert.Host/Program.cs` — `AddHostedService<VolumeSyncWorker>()` e compagni.
- `FileTracert.Business/Volumes/VolumeSyncService.cs` — `SyncAsync` ritorna
  `IReadOnlyList<int>` con gli id dei volumi **tornati online**, e aggiorna
  `FreeBytesLastKnown` dal probe live.
- `FileTracert.Business/Operations/BlockedJobRevaluator.cs` — copre già i block reason
  offline (WP2, commit `f3b1fd3`).

## Lavoro

### 1. Port `IDeviceWatcher` in `Contracts/Platform`

Interfaccia orientata all'evento, **zero tipi Win32 esposti**. Forma consigliata (la scelta
esatta è tua, documentala nel commit):

```csharp
public interface IDeviceWatcher : IDisposable
{
    event EventHandler<DeviceChangeEvent>? Changed;
    void Start();
}

public sealed record DeviceChangeEvent(DeviceChangeKind Kind, DateTime TimestampUtc);
public enum DeviceChangeKind { Arrived, Removed }
```

**Nota di dominio importante:** la notifica di sistema porta un *symbolic link name*
(`\\?\STORAGE#Volume#…`), **non** il Volume GUID path che è la nostra chiave (§4). Non
tentare di mapparlo: l'evento vale come «qualcosa è cambiato, ri-sonda». L'identità la
risolve `VolumeSyncService`, che enumera e matcha per GUID come fa già. Per questo il
record dell'evento può non avere un `VolumeGuid`.

### 2. `Win32DeviceWatcher` in `Platform`

**Meccanismo consigliato: `CM_Register_Notification`** (`cfgmgr32.dll`, Win8+) con
`CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE` e `GUID_DEVINTERFACE_VOLUME`
`{53F5630D-B6BF-11D0-94F2-00A0C91EFB8B}`. È callback-based: **non serve una finestra né
l'handle di servizio**, quindi funziona identico in console (dev) e come Windows Service
(prod). È il motivo per cui è preferito a `RegisterDeviceNotification`, che pretende un
HWND o l'handle restituito da `RegisterServiceCtrlHandlerEx` — nessuno dei due è a
disposizione dentro il generic host.

Alternativa se ti blocchi: finestra message-only (`HWND_MESSAGE`) su un thread dedicato
con message loop e `WM_DEVICECHANGE`. Più codice, stesso risultato. Se la scegli, motiva
nel commit.

Trappole da non prendere:
- **Il delegate della callback deve restare vivo** per tutta la registrazione: se lo passi
  inline il GC lo raccoglie e il processo muore con un crash nativo. Tienilo in un campo.
- `CM_Unregister_Notification` nel `Dispose`, sempre, anche se `Start` è fallito a metà.
- La callback arriva su un thread di sistema: **non fare lavoro dentro**. Solleva l'evento
  e torna subito.
- Windows spara **una raffica** di eventi per un singolo inserimento (interfaccia, volume,
  arrivo, query-remove…). La deduplica è responsabilità del consumatore (punto 3).

### 3. `DeviceWatcherWorker` in `Host/Workers`

- Sottoscrive `IDeviceWatcher.Changed`, **debounce/coalescing** della raffica in una
  finestra breve (≈1 s: scegli tu il valore, mettilo in `FileTracertOptions` con un
  default) → **un solo** ciclo di sync per inserimento.
- Il ciclo da eseguire è **esattamente lo stesso** di `VolumeSyncWorker.SyncOnceAsync`:
  sync → revaluator → signal. **Non duplicarlo** (§9): estrai il corpo in un componente
  condiviso (es. `Host/Infrastructure/VolumeSyncCycle`) e fallo usare a entrambi i worker.
- **Serializza**: il ciclo su intervallo e quello su evento non devono mai girare insieme
  (`SemaphoreSlim(1,1)`); il secondo che arriva mentre uno gira o attende o viene scartato
  — decidi e commenta il perché.
- `ApplicationStopping` linkato, `Dispose` del watcher pulito (§3 shutdown).

### 4. Il polling resta, come rete

`VolumeSyncWorker` **non si tocca nel comportamento** (a parte l'estrazione del ciclo
condiviso): resta la rete di sicurezza se il watcher non parte o perde un evento. Non
alzare l'intervallo per «compensare», non abbassarlo per «sicurezza».

### 5. Fallimento del watcher: rumoroso, non fatale (§9)

Se la registrazione nativa fallisce, il servizio **deve continuare a funzionare in
polling**. Quindi: log completo dell'eccezione **e** una riga in `Notifications`
(`INotificationPublisher`, severity Warning) che dice all'utente che il rilevamento
automatico dei drive non è attivo e che i volumi verranno comunque riconosciuti entro
l'intervallo di sync. Mai un `catch` muto, mai un host che non parte per questo.

## Test (non negoziabile)

- **RED prima del GREEN**, contro l'implementazione reale.
- **Host** (`tests/FileTracert.Tests/Host/`): con un `IDeviceWatcher` **fake** (il
  componente sotto esame qui è il worker, non l'interop) →
  - una raffica di N eventi ravvicinati produce **un solo** ciclo di sync;
  - il ciclo chiama revaluator e segnala la coda quando qualcosa si sblocca (riusa il
    pattern di `VolumeSyncWorkerTests.cs` e `QueueStartupRevaluationTests.cs`);
  - il ciclo su evento e quello su intervallo non si sovrappongono.
- **Platform** (`tests/FileTracert.Tests/Platform/`, `[Trait("Category", "Platform")]`):
  ciclo di vita reale del `Win32DeviceWatcher` — `Start()` non lancia, `Dispose()`
  deregistra, doppio `Dispose()` è innocuo, la callback non viene raccolta dal GC
  (`GC.Collect()` + `WaitForPendingFinalizers()` tra Start e Dispose). Un arrivo fisico
  non è forzabile da test: quello lo copre l'harness.
- **Harness** (`src/backend/FileTracert.HardwareSmoke`): `offline-unplug` (già esistente,
  gira solo con `SemiAutomatic=true`) deve mostrare che al ricollegamento il job riparte
  **in pochi secondi**, non al successivo poll: aggiungi l'assert temporale. Se il tempo
  misurato supera la soglia, è un FAIL con il numero in chiaro.
  Gli scenari non interattivi non possono simulare un arrivo vero: dillo nel README
  dell'harness invece di inventare un PASS.

## Criteri di accettazione

- [ ] File:riga riverificati su HEAD prima di editare.
- [ ] `IDeviceWatcher` in `Contracts/Platform`, implementazione **solo** in `Platform`,
      nessun tipo nativo oltre il confine (§3). `Business` non lo referenzia.
- [ ] Raffica di eventi → **un solo** ciclo di sync; nessun ciclo concorrente col worker
      a intervallo; nessuna duplicazione del corpo del ciclo.
- [ ] Registrazione fallita → polling continua + log completo + Notification (§9).
- [ ] `Dispose` deregistra; nessun crash da callback raccolta dal GC.
- [ ] Suite xUnit verde, build backend pulita (warnings-as-errors).
- [ ] Harness: scenari offline ancora verdi; `offline-unplug` con l'assert temporale nuovo
      (se hai un drive esterno a disposizione e `SemiAutomatic=true`; altrimenti dichiara
      esplicitamente nel resoconto che non è stato eseguito e perché).

## Commit suggeriti

1. `feat(contracts): IDeviceWatcher port for volume arrival/removal`
2. `feat(platform): Win32DeviceWatcher on CM_Register_Notification`
3. `refactor(host): extract the volume-sync cycle shared by both triggers`
4. `feat(host): DeviceWatcherWorker — debounce the burst, one sync per event`
5. `test(host+platform): watcher lifecycle, debounce, revaluation trigger`
6. `test(harness): offline-unplug asserts the remount is immediate`

## Code review finale (obbligatoria)

A fine task, review indipendente delle modifiche: correttezza vs criteri e scenari di
fallimento; no silent catch (§9); layering (§3: interop **solo** in Platform); nessuna
duplicazione (il ciclo di sync è uno solo); test reali RED→GREEN; nessun handle o
registrazione nativa che sopravvive allo shutdown. Riportare cosa ha trovato e cosa è
stato corretto (o perché un rilievo è stato lasciato consapevolmente).

## Regole operative

- **Host chiuso prima di ricompilare** (lock DLL).
- Niente branch nuovi: si committa su `develop`.
- Niente scope creep: se serve un pezzo di 10b/10c, fai il minimo e **segnalalo**.
- A fine task aggiorna `CLAUDE.md` (sezione «Fatto nello step 10a…» + roadmap) come hanno
  fatto gli step precedenti, e **cancella questo file**: il piano vive nella history.
