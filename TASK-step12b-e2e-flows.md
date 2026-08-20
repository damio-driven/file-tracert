# TASK — Step 12b: i flussi critici end-to-end (catalogo, ricerca, coda, realtime)

> **Sessione dedicata, agente singolo.** Secondo checkpoint dello step 12 e **ultimo
> dell'MVP** (`CLAUDE.md` §10). **Prerequisito rigido: 12a mergiato** — questo task usa
> l'infrastruttura, la sandbox e gli helper costruiti lì e non ne inventa di nuovi.
> Riferimenti: `CLAUDE.md` §5 (proiezione), §4 (coda, fattibilità, Blocked), §7 (SignalR),
> §8 (schermate), sezione «Test (non negoziabile)».
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

12a prova che il prodotto si accende. Qui si provano le cose che l'MVP **promette** e che
finora nessun livello di test ha visto assemblate:

- la **proiezione** (§5): accodo un'operazione e il file è *già* nella destinazione, con il
  badge di stato — verificato in xUnit sui dati e in Vitest sul componente, mai sullo
  schermo vero, sul catalogo vero, dopo un enqueue vero;
- il **push** (§7): job che avanzano, notifiche, stato di connessione. Gli step 10b e 10c
  hanno dichiarato per iscritto che la prova end-to-end del push **arriva con Playwright**:
  è questo il momento;
- la **ricerca FTS** sul nome proiettato, con i filtri;
- la **coda**: stati, dipendenze, blocco, e ciò che l'utente legge quando un'operazione non
  può partire.

## Prima di tutto: il primo lavoro è il recinto

12a lo ha scritto a chiare lettere chiudendo: **la sandbox protegge la suite, non il
prodotto.** I suoi flussi leggono soltanto; i tuoi accodano operazioni **vere** — move,
rename, delete nel cestino — eseguite dal `JobExecutionEngine` reale su un volume che la
suite rende catalogabile, che su questa macchina è il **disco di sistema**.

Quindi il **primo commit** di questo task non è un test: è il vincolo che rende impossibile
a un test di toccare qualcosa fuori dalla sandbox. Requisiti:

- ogni operazione accodata da un test ha **sorgente e destinazione dentro la sandbox**, e
  questo va **verificato prima dell'esecuzione**, non sperato dalla costruzione del test;
- il vincolo deve fallire **rumorosamente** se violato (test rosso con il path incriminato),
  non silenziosamente correggere;
- vale anche per il **cestino**: ciò che finisce lì viene da lì.

Come ottenerlo è una decisione tua — un watched root che confina il perimetro, un
assertivo sui path prima dell'enqueue, un controllo nel teardown che nulla è cambiato fuori
dalla sandbox, o la combinazione. Ma **motivalo nel commit**, e provalo: un test che
prova a uscire dal recinto deve diventare rosso.

Questo è l'unico punto del task su cui, se non riesci a costruire una garanzia che ti
convince, **ti fermi e lo dici** invece di procedere: un E2E che sposta file veri
dell'utente è un danno, non un test fallito.

## I flussi da coprire

Ordine consigliato, dal più stabile al più delicato.

### 1. Catalogo (lettura)
Albero lazy sulla sandbox: espansione, contatori, un file che si trova dove deve stare.
Copre anche l'esito di 11h: una cartella esclusa dai filtri **non sparisce** dall'albero, e
i suoi file risultano esclusi, non assenti.

### 2. Ricerca
FTS sul nome, filtro categoria, filtro data (il giro «date/UTC» ha chiuso il confronto
lessicale: qui si verifica sullo schermo che un file modificato *oggi* rientri nel filtro
*oggi*). Ricordare che i filtri **dimensione** esistono nello store ma non hanno un
controllo in UI (limite noto): non inventarne uno, semmai annotarlo.

### 3. Accodamento e proiezione (§5, il cuore)
Dal Catalogo: selezione multipla → picker → conferma. Poi, **senza ricaricare la pagina**:
- il file appare nella destinazione con il badge «in spostamento»;
- la Ricerca lo trova con il nome proiettato;
- la Coda mostra il job, e il link dal badge porta alla sua riga (deep link `/queue?job=`).

Con il worker della coda **fermo** questo stato è stabile e osservabile quanto serve; poi
lo si lascia partire e si verifica che il badge sparisca a operazione completata — che è
la prova del ciclo intero.

### 4. Coda: stati veri, non simulati
Un job che **si blocca** per una ragione reale (spazio insufficiente sulla destinazione, o
volume non disponibile) e mostra in UI il motivo con il deficit — dopo 11b il numero
include il margine e lo **dichiara**: verificare che la frase sia quella, non un numero
nudo. Un secondo job sulla stessa entità → `Blocked(DependencyPending)` con il rimando
all'operazione che lo precede (step 9c), non un errore.

### 5. Realtime, la lacuna dichiarata
Le tre prove che solo questo livello può dare:
- **avanzamento**: un job in copia muove la barra **senza** che nessuno ricarichi;
- **notifica**: un errore di background accende la campanella;
- **connessione persa**: si spegne il Host (o si interrompe il socket) e la shell mostra lo
  stato — ambra in riconnessione, rosso quando SignalR ha mollato — e **al ritorno** le
  schermate rileggono invece di restare ferme su un dato vecchio. Questo è il comportamento
  che 10c ha progettato e che nessun test ha mai visto davvero.

## Regole di qualità (le stesse di 12a, ribadite perché qui è più facile sbagliare)

- **Niente sincronizzazione a tempo.** Un `waitForTimeout` in un test di realtime prova che
  hai aspettato, non che il messaggio è arrivato. Asserzioni web-first su ciò che cambia.
- **Niente mock del trasporto.** Se un test intercetta l'hub o finge un messaggio, non è
  E2E: quel livello esiste già in Vitest.
- **Deterministico per costruzione**: file piccoli, quantità piccole, worker accesi solo
  quando servono. Se un flusso è instabile, la risposta è renderlo osservabile, non
  aumentare i timeout.
- **Sandbox isolata**: nessun file dell'utente, cestino compreso.

## Split dei commit (indicativo)

1. `test(e2e): catalog tree over the sandbox`.
2. `test(e2e): search finds the projected name`.
3. `test(e2e): queueing an operation moves the projection`.
4. `test(e2e): the queue explains why a job cannot run`.
5. `test(e2e): the pushed events reach the screen`.

## Verifica

- Tutti i test E2E (12a + 12b) verdi **tre volte di fila**, con la durata riportata.
- **RED dimostrato** su almeno tre punti, rompendo il prodotto: togli la scrittura
  dell'overlay all'enqueue → il badge non compare; togli l'instradamento di un evento nel
  `RealtimeBridge` → la barra non si muove; togli la rilettura alla riconnessione → la
  schermata resta vecchia. Sono esattamente i difetti che questo livello deve intercettare.
- xUnit e Vitest verdi, build backend pulita, `ng build` ok.
- **Harness sul ferro** una passata (baseline 47/47) per confermare che nulla è regredito:
  coppia `D:\Collaudo\A` → `C:\Collaudo\B`, `appsettings.json` rimesso byte-identico.

## Definition of done

- I cinque flussi coperti, verdi ×3.
- **Code review finale** indipendente: nessuna sincronizzazione a tempo, nessun mock del
  trasporto, nessun test che passa anche col prodotto rotto (la review deve **provarlo**
  rompendo qualcosa), sandbox isolata.
- `CLAUDE.md`: paragrafo «Fatto nello step 12b», **chiusura dello step 12 e dell'MVP** nella
  roadmap, e rimozione dei limiti noti che questo giro ha chiuso — in particolare le due
  frasi di 10b/10c sul push mai provato end-to-end.
- Nel report: cosa **resta** scoperto dall'E2E e perché (ci sarà qualcosa: USN non elevato,
  drive esterni, volumi offline reali). Meglio scritto che immaginato coperto.
