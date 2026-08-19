import {
  ChangeDetectionStrategy, Component, OnInit, computed, effect, inject,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { BytesPipe } from '../../shared/pipes/bytes.pipe';
import { RelativeTimePipe } from '../../shared/pipes/relative-time.pipe';
import { FtPill, PillVariant } from '../../shared/components/ft-pill/ft-pill';
import { FtBar } from '../../shared/components/ft-bar/ft-bar';
import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { JobBlockReason, JobState, JobType, OperationJobDto } from '../../core/models/catalog.models';
import { isActiveJobState, isTerminalJobState } from '../../core/models/job-state';
import { QueueStore } from './queue.store';

@Component({
  selector: 'ft-queue',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BytesPipe, RelativeTimePipe, FtPill, FtBar, FtPanel, RouterLink],
  templateUrl: './queue.html',
  styleUrl: './queue.scss',
})
export class Queue implements OnInit {
  protected readonly store = inject(QueueStore);
  private readonly route = inject(ActivatedRoute);

  private readonly queryParams = toSignal(this.route.queryParamMap);

  /**
   * Job to point at, read from `?job=<id>`. The projection badges in Catalogo and Ricerca
   * link here, and a link that drops the user into a 50-row table without saying which row
   * is theirs is a dead end.
   */
  protected readonly focusedJobId = computed(() => {
    const raw = this.queryParams()?.get('job') ?? null;
    if (raw === null) return null;
    const id = Number(raw);
    return Number.isInteger(id) && id > 0 ? id : null;
  });

  constructor() {
    // Rows arrive after the first render, so bring the row into view once it exists.
    // Instant, not smooth: no motion to opt out of, and the user asked to be taken there.
    effect(() => {
      const id = this.focusedJobId();
      if (id === null || this.store.jobs().length === 0) return;
      requestAnimationFrame(() =>
        document.getElementById(`job-${id}`)?.scrollIntoView({ block: 'nearest' }));
    });
  }

  ngOnInit(): void {
    // One load. From here the hub patches the rows it changes (step 10c): no timer, so a
    // queue left open overnight costs nothing and a byte counter still moves once a second.
    this.store.load();
  }

  protected stateVariant(state: JobState): PillVariant {
    switch (state) {
      case 'Pending':
      case 'SpaceReserved': return 'wait';
      case 'Copying':
      case 'Verifying':
      case 'DeletingSource': return 'run';
      case 'Completed': return 'done';
      case 'Blocked':
      case 'Failed': return 'block';
      case 'Cancelled': return 'off';
    }
  }

  protected stateLabel(state: JobState): string {
    switch (state) {
      case 'Pending': return 'In attesa';
      case 'SpaceReserved': return 'Prenotato';
      case 'Copying': return 'Copiando';
      case 'Verifying': return 'Verifica';
      case 'DeletingSource': return 'Eliminando';
      case 'Completed': return 'Completato';
      case 'Blocked': return 'Bloccato';
      case 'Failed': return 'Errore';
      case 'Cancelled': return 'Annullato';
    }
  }

  protected typeLabel(type: JobType): string {
    switch (type) {
      case 'CreateFolder': return 'Crea cartella';
      case 'RenameFile': return 'Rinomina file';
      case 'RenameFolder': return 'Rinomina cartella';
      case 'MoveFile': return 'Sposta file';
      case 'MoveFolder': return 'Sposta cartella';
    }
  }

  protected blockReasonLabel(reason: JobBlockReason): string {
    switch (reason) {
      case 'None': return '';
      case 'InsufficientSpace': return 'Spazio insufficiente';
      case 'TargetVolumeOffline': return 'Volume destinazione offline';
      case 'SourceVolumeOffline': return 'Volume sorgente offline';
      case 'NameCollision': return 'Conflitto di nome';
      case 'DependencyPending': return 'Dipendenza in attesa';
      case 'DependencyCancelled': return 'Dipendenza annullata';
    }
  }

  /**
   * The job this one is waiting for, when that is what blocks it — null otherwise, so the
   * template falls back to the plain reason label. Returning the id (not a boolean) lets the
   * template bind it with `@if (…; as id)` and keeps the null-check in one place.
   */
  protected dependencyOf(job: OperationJobDto): number | null {
    const dependsOnDependency =
      job.blockReason === 'DependencyPending' || job.blockReason === 'DependencyCancelled';
    return dependsOnDependency ? job.dependsOnJobId : null;
  }

  /** Reads into the "#12" link that follows it, so the two together form one sentence. */
  protected dependencyLead(reason: JobBlockReason): string {
    return reason === 'DependencyCancelled'
      ? 'Dipendenza interrotta:'
      : "In attesa dell'operazione";
  }

  protected progress(job: OperationJobDto): number {
    if (!job.totalBytes) return 0;
    return Math.min(100, Math.round((job.bytesProcessed / job.totalBytes) * 100));
  }

  protected isActive(state: JobState): boolean {
    return isActiveJobState(state);
  }

  protected isTerminal(state: JobState): boolean {
    return isTerminalJobState(state);
  }

  protected isCancelling(id: number): boolean {
    return this.store.cancellingIds().includes(id);
  }

  protected cancel(id: number): void {
    this.store.cancel(id);
  }

  /** Riprova is offered only for Blocked/Failed jobs (the backend rejects the rest). */
  protected canRetry(state: JobState): boolean {
    return state === 'Blocked' || state === 'Failed';
  }

  protected isRetrying(id: number): boolean {
    return this.store.retryingIds().includes(id);
  }

  protected retry(id: number): void {
    this.store.retry(id);
  }

  protected refresh(): void {
    this.store.refresh();
  }
}
