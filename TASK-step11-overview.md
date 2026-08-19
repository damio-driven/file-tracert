# TASK — Step 11: work package minori (indice)

> **Non è un task da eseguire**: è la mappa dei sei task che chiudono i WP minori
> rimasti dalla code review (`CODE-REVIEW-HANDOFF.md`), nell'ordine in cui vanno
> eseguiti. Ogni file è **autonomo**: un task = una sessione = un agente.
> Stato verificato su `develop` @ `88571aa` (working tree pulito).

## Perché sei file e non uno

I WP rimasti toccano mondi diversi (indice, ledger, logging, Angular, SQL, refactor) e
insieme superano di molto quello che una sessione può portare a termine senza tagliare
l'output. Il criterio di taglio è **il file caldo**, non l'argomento: due task che
scrivono lo stesso file non devono mai stare nella stessa coda parallela né in sessioni
sovrapposte (`CLAUDE.md` → «Lavoro in parallelo»).

## Ordine e contenuto

| # | Task | WP chiusi | File caldi | Taglia |
|---|------|-----------|-----------|--------|
| 1 | `TASK-step11a-index-search.md` | WP5 (#6, C19, C16, P2) + FAIL harness `job-dependencies` cross | `IndexUpdater`, `JobSnapshotRefresher`, `ScanService`, `FileFilter` | L |
| 2 | `TASK-step11b-space.md` | WP6 (finding 10) | `JobExecutionEngine`, `SpaceLedger` | S |
| 3 | `TASK-step11c-logging-shutdown.md` | WP8 (C18, C23, C24, C28) | `Program.cs`, `SqliteLogProcessor`, `SqliteLogStore`, `ScanService` | M |
| 4 | `TASK-step11d-frontend-ux.md` | WP7 (C17, C25, C27, C29, C30, K8, K9, K14) | Angular + `OperationsController`/`QueueService` (endpoint batch) | L |
| 5 | `TASK-step11e-efficiency.md` | WP9 (E1, E3, E4, E5, E6, E7, E8) | `QueueService`, `FileSearchIndex`, `IndexUpdater`, controller | M |
| 6 | `TASK-step11f-cleanup.md` | WP10 (K1, K2, K3, K4, K6, K7, K10, K11, K12, K13) + discrepanze §3 | quasi tutti, in piccolo | M |

**L'ordine non è negoziabile in due punti:**
- **11a prima di 11e** — E4 (upsert FTS set-based) riscrive gli stessi metodi di
  `IndexUpdater` che 11a corregge per il finding 6 e C19. Invertirli significa conflitto
  certo su `IndexUpdater.cs`.
- **11f per ultimo** — il cleanup unifica helper che gli altri cinque task stanno
  modificando (`CleanupPartials`, la stanza cross-volume, il release-then-reserve).
  Unificare prima vuol dire unificare la versione sbagliata.

11b, 11c e 11d sono indipendenti fra loro **sul contenuto**, ma 11b e 11d scrivono
entrambi nel perimetro della coda (`JobExecutionEngine` / `QueueService`): vanno in
**sessioni distinte e sequenziali**, mai in parallelo (`CLAUDE.md`).

## Cosa NON è coperto qui

- **Step 12 (Playwright)**: resta il task successivo, non è un WP minore.
- **Chiusura del debito «classificazione Cloud»** (`CLAUDE.md` §11): l'esclusione manuale
  copre il caso, resta datato.
- **La domanda aperta sui file fuori dai watched root** (marcati `IsPresent=false` al primo
  scan del volume) è una **decisione di prodotto**: è posta in 11f, e in 11f l'agente si
  ferma e chiede invece di scegliere.

## Regole valide per tutti e sei

- Il file TASK **è** il piano: niente skill `writing-plans`, si implementa per commit.
- **Riverificare file:riga su HEAD** prima di editare: i numeri qui e nei task vengono da
  `88571aa` e invecchiano a ogni commit.
- **RED prima del GREEN** su ogni fix, contro l'implementazione reale (§Test di `CLAUDE.md`).
- **Suite verde + build pulita** (warnings-as-errors) a fine task; `ng build` dove si tocca
  Angular (i 4 warning di budget SCSS sono pre-esistenti e restano).
- **Code review finale obbligatoria** sulle modifiche del giro, riportata nel task.
- A fine task: paragrafo «Fatto nello step 11x» in `CLAUDE.md`, con deviazioni e limiti.
