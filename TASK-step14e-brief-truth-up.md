# TASK — Step 14e: il brief dice la verità sul codice

> **Sessione dedicata, agente singolo — task corto, ma non meccanico.** Da eseguire **dopo**
> 14d, perché parte di ciò che oggi è falso in `CLAUDE.md` sarà reso vero da quel giro, e
> correggerlo prima significherebbe scriverlo due volte.
> Riferimenti: `CLAUDE.md` tutto, e `CODE-REVIEW-HANDOFF.md`.
> Niente skill `writing-plans`: **questo documento È il piano**. Implementa per commit.

## Perché

`CLAUDE.md` è il documento che ogni agente legge per primo e tratta come vincolante. Quando
mente, non è un refuso: è un'istruzione sbagliata che qualcuno eseguirà.

Lo step 13 ne ha trovata una grossa e per caso: **§3 elenca `UsnSyncWorker` fra i
BackgroundServices**, e quel componente **non è mai stato scritto** (zero occorrenze in
`src/backend`, verificato). Nessuno se n'era accorto per mesi, perché la scansione completa
copre il caso e i test esercitano la piattaforma. Se una discrepanza di quella taglia è
sopravvissuta, altre possono esserci.

## Lavoro

### 1. Le discrepanze note, da chiudere

- **§3 / `UsnSyncWorker`**: dopo 14d o esiste (e la frase è vera), o non esiste ancora (e va
  scritto come pianificato-non-fatto, non come presente). Nessuna terza via.
- **§1.2** («indicizzazione efficiente e incrementale» come nodo tecnico che *definisce il
  prodotto*): allineare a ciò che il codice fa, con l'esito misurato di 14d.
- **§4 / motori di scansione**: la descrizione dell'incrementale va sincronizzata con la
  realtà del percorso implementato.

### 2. L'audit vero: cosa altro promette il brief che il codice non fa

Passa in rassegna le sezioni che descrivono **componenti** e **comportamenti** (§3, §4, §5,
§6, §7, §8) e verifica ognuno contro il codice, non contro la memoria. In particolare:

- ogni BackgroundService nominato esiste ed è registrato;
- ogni endpoint e messaggio SignalR nominato esiste con quel nome;
- ogni campo di schema nominato esiste con quel nome (§6 è lungo e ha subito 11g/11h);
- ogni enum e ogni stato nominato è raggiungibile (lo step 11f ha già tolto stato morto:
  non ripetere quel lavoro, verifica che il brief lo rispecchi);
- le schermate di §8 corrispondono a quelle che esistono.

Per ogni discrepanza trovata: **decidere da che parte sta la verità**. A volte il brief è
giusto e il codice è indietro (allora è una voce di roadmap, non una correzione di testo);
a volte il codice è giusto e il brief è vecchio (allora si corregge il testo). Non
appiattire tutto sulla seconda: è la scorciatoia che trasforma un brief in un changelog.

### 3. `CODE-REVIEW-HANDOFF.md`

Tutti i finding sono chiusi dagli step 11a-11f tranne quelli esplicitamente lasciati aperti.
Verificare che lo stato scritto lì corrisponda, e che ciò che resta aperto sia ancora vero
dopo 14a-14d (E3, E5 e la ricerca sono stati toccati di nuovo).

### 4. La sezione «cosa resta aperto»

`CLAUDE.md` ha accumulato limiti noti da quindici step. Ora che l'MVP è chiuso e lo step 13
ha prodotto una lista di lavoro successivo, quella parte va **riorganizzata**: cosa è debito
datato, cosa è fase 2, cosa è appena stato chiuso. Senza cancellare la storia — i paragrafi
«Fatto nello step N» sono la memoria del progetto e restano — ma la **roadmap in cima** deve
poter essere letta in trenta secondi da chi arriva domani.

## Vincoli

- **Nessuna modifica di codice** in questo task, tranne l'eventuale rimozione di un commento
  che dice il falso (e in quel caso è un commit a sé, con il suo perché).
- Nessuna riscrittura stilistica: si corregge ciò che è **falso** o **irreperibile**, non ciò
  che è scritto in un modo che ti piace meno.
- Ogni correzione deve citare la prova (file:riga o grep) nel messaggio di commit: è il modo
  in cui il prossimo audit parte da qui invece che da zero.

## Definition of done

- Elenco delle discrepanze trovate, ognuna con la prova e la decisione presa (brief sbagliato
  → corretto; codice indietro → voce di roadmap).
- `CLAUDE.md` e `CODE-REVIEW-HANDOFF.md` allineati al codice su `develop`.
- La roadmap in cima leggibile in trenta secondi.
- Suite verde e build pulita (non hai toccato codice, ma si verifica lo stesso: un task che
  non ricompila non ha guardato niente).
