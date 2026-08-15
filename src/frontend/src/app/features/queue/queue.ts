import {
  ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, effect, inject,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';

import { BytesPipe } from '../../shared/pipes/bytes.pipe';
import { RelativeTimePipe } from '../../shared/pipes/relative-time.pipe';
import { FtPill, PillVariant } from '../../shared/components/ft-pill/ft-pill';
import { FtBar } from '../../shared/components/ft-bar/ft-bar';
import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { JobBlockReason, JobState, JobType, OperationJobDto } from '../../core/models/catalog.models';
import { QueueStore } from './queue.store';

const ACTIVE_STATES = new Set<JobState>(['Copying', 'Verifying', 'DeletingSource']);
const TERMINAL_STATES = new Set<JobState>(['Completed', 'Failed', 'Cancelled']);

@Component({
  selector: 'ft-queue',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BytesPipe, RelativeTimePipe, FtPill, FtBar, FtPanel],
  templateUrl: './queue.html',
  styleUrl: './queue.scss',
})
export class Queue implements OnInit, OnDestroy {
  protected readonly store = inject(QueueStore);
  private readonly route = inject(ActivatedRoute);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

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
    this.store.load();
    this.pollTimer = setInterval(() => {
      if (this.store.hasActiveJobs()) {
        this.store.refresh();
      }
    }, 2500);
  }

  ngOnDestroy(): void {
    if (this.pollTimer !== null) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
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

  protected progress(job: OperationJobDto): number {
    if (!job.totalBytes) return 0;
    return Math.min(100, Math.round((job.bytesProcessed / job.totalBytes) * 100));
  }

  protected isActive(state: JobState): boolean {
    return ACTIVE_STATES.has(state);
  }

  protected isTerminal(state: JobState): boolean {
    return TERMINAL_STATES.has(state);
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
