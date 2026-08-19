import {
  ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { CatalogApi } from '../../../core/api/catalog-api.service';
import { QueueApi } from '../../../core/api/queue-api.service';
import { VolumesStore } from '../../../features/volumes/volumes.store';
import { BytesPipe } from '../../pipes/bytes.pipe';
import { RelativeTimePipe } from '../../pipes/relative-time.pipe';
import { FtPill } from '../ft-pill/ft-pill';
import { httpErrorMessage } from '../../../core/http/http-error';
import { validateLeafName } from '../../validation/name.util';
import {
  CatalogDirDto, CreateJobRequest, FeasibilityResult, SelectedItem, VolumeDto,
} from '../../../core/models/catalog.models';

interface FolderCrumb {
  id: number;
  name: string;
}

@Component({
  selector: 'ft-operation-picker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, BytesPipe, RelativeTimePipe, FtPill],
  templateUrl: './operation-picker.html',
  styleUrl: './operation-picker.scss',
})
export class OperationPicker implements OnInit {
  readonly items = input.required<SelectedItem[]>();
  /** Popup dismissed (Annulla, backdrop, X). The parent must NOT clear the selection. */
  readonly closed = output<void>();
  /** Whole batch enqueued successfully — the only event that consumes the selection. */
  readonly completed = output<void>();

  protected readonly volumes = inject(VolumesStore);
  private readonly api = inject(QueueApi);
  private readonly catalogApi = inject(CatalogApi);
  private readonly router = inject(Router);

  protected targetVolumeId: number | null = null;

  protected readonly crumbs = signal<FolderCrumb[]>([]);
  protected readonly newFolderSegments = signal<string[]>([]);
  protected readonly dirChildren = signal<CatalogDirDto[]>([]);
  protected readonly loadingDirs = signal(false);
  protected readonly newFolderInputOpen = signal(false);
  protected readonly newFolderError = signal<string | null>(null);
  protected newFolderName = '';

  protected readonly preview = signal<FeasibilityResult | null>(null);
  protected readonly previewing = signal(false);
  protected readonly enqueueing = signal(false);
  protected readonly enqueued = signal(false);
  protected readonly enqueuedCount = signal(0);
  /** How many of those were parked behind an operation already in the queue (§5). */
  protected readonly waitingCount = signal(0);
  /** How many were parked on a resource: not enough room, or a volume that is unplugged. */
  protected readonly parkedOnResourceCount = signal(0);
  protected readonly error = signal<string | null>(null);

  /** Cold open: nothing can be chosen until the volume list is here. */
  protected readonly volumesLoading = signal(false);
  protected readonly volumesError = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.loadVolumes();
  }

  /**
   * C27 — this used to fire `loadList()` and read `catalogable()` on the very next line, so on
   * a cold store the dialog opened with an empty select, no folder tree and a disabled button
   * that explained nothing. It now waits for the list, and the dialog says which of the three
   * situations it is in: loading, failed (with a way to try again), or no volume to move to.
   */
  protected async loadVolumes(): Promise<void> {
    // A warm store already holds the answer. Showing a loading state over data we have would be
    // a flicker that means nothing, so pick the default at once and refresh underneath.
    const warm = this.volumes.volumes().length > 0;
    this.volumesError.set(null);
    if (warm) {
      this.selectDefaultVolume();
    } else {
      this.volumesLoading.set(true);
    }

    try {
      await this.volumes.loadList();
    } finally {
      this.volumesLoading.set(false);
    }

    // The store never rejects: it swallows the failure into its own `error` signal, which
    // `loadList` clears at the start. That signal is shared with the Volumi screen, so in
    // principle a failure raised there in the same instant would be read here as ours — the
    // narrow price of not giving the dialog a second copy of the list.
    this.volumesError.set(this.volumes.error());
    this.selectDefaultVolume();
  }

  /** First online catalogable volume, once. A choice already made by the user is never undone. */
  private selectDefaultVolume(): void {
    if (this.targetVolumeId !== null) return;
    const online = this.volumes.catalogable().find(v => v.isOnline);
    if (!online) return;
    this.targetVolumeId = online.id;
    void this.loadChildren(null);
  }

  /** True when the list came back and simply has nothing to offer as a destination. */
  protected get noDestinations(): boolean {
    return !this.volumesLoading()
      && this.volumesError() === null
      && this.volumes.catalogable().length === 0;
  }

  protected get totalBytes(): number {
    return this.items().reduce((s, f) => s + f.sizeBytes, 0);
  }

  protected get folderCount(): number {
    return this.items().filter(i => i.kind === 'Folder').length;
  }

  /** Move request for one selected item: MoveFile for files, MoveFolder for folders. */
  private toMoveRequest(item: SelectedItem, folder: string): CreateJobRequest {
    return item.kind === 'Folder'
      ? { type: 'MoveFolder', sourceFileId: null, sourceDirectoryId: item.id, targetVolumeId: this.targetVolumeId!, targetRelativePath: folder, newName: null }
      : { type: 'MoveFile', sourceFileId: item.id, sourceDirectoryId: null, targetVolumeId: this.targetVolumeId!, targetRelativePath: folder, newName: null };
  }

  protected get targetVolume(): VolumeDto | undefined {
    return this.volumes.catalogable().find(v => v.id === this.targetVolumeId);
  }

  /** Real crumbs (existing folders) + virtual segments (not created yet), joined for the API. */
  protected get targetFolder(): string {
    return [...this.crumbs().map(c => c.name), ...this.newFolderSegments()].join('\\');
  }

  protected get canSubmit(): boolean {
    return this.targetVolumeId !== null;
  }

  protected async onVolumeChange(): Promise<void> {
    this.preview.set(null);
    this.crumbs.set([]);
    this.newFolderSegments.set([]);
    this.newFolderInputOpen.set(false);
    await this.loadChildren(null);
  }

  protected async openDirectory(dir: CatalogDirDto): Promise<void> {
    this.preview.set(null);
    this.crumbs.update(c => [...c, { id: dir.id, name: dir.name }]);
    await this.loadChildren(dir.id);
  }

  protected async navigateToRoot(): Promise<void> {
    this.preview.set(null);
    this.crumbs.set([]);
    this.newFolderSegments.set([]);
    await this.loadChildren(null);
  }

  protected async navigateToCrumb(index: number): Promise<void> {
    this.preview.set(null);
    const target = this.crumbs()[index];
    this.crumbs.update(c => c.slice(0, index + 1));
    this.newFolderSegments.set([]);
    await this.loadChildren(target.id);
  }

  /** Virtual crumbs never hit the API — dropping the deeper ones is a pure client-side truncation. */
  protected navigateToVirtualCrumb(index: number): void {
    this.preview.set(null);
    this.newFolderSegments.update(s => s.slice(0, index + 1));
  }

  protected openNewFolderInput(): void {
    this.newFolderName = '';
    this.newFolderError.set(null);
    this.newFolderInputOpen.set(true);
  }

  protected cancelNewFolder(): void {
    this.newFolderInputOpen.set(false);
    this.newFolderName = '';
    this.newFolderError.set(null);
  }

  /** Clears a stale complaint as soon as the user starts fixing the name. */
  protected onNewFolderInput(): void {
    if (this.newFolderError() !== null) this.newFolderError.set(null);
  }

  protected confirmNewFolder(): void {
    const name = this.newFolderName.trim();
    // K14 — the same rules, and the same words, as the rename / new-folder dialog. This used to
    // check "not empty" only, so `foo\bar` became one virtual segment here and was refused by
    // the other dialog: two answers to one question, and the backend's is a third round trip
    // away (OperationName.TryValidateLeaf, which this mirrors).
    const problem = validateLeafName(name);
    if (problem !== null) {
      this.newFolderError.set(problem);
      return;
    }
    this.newFolderError.set(null);
    this.preview.set(null);
    this.newFolderSegments.update(s => [...s, name]);
    this.dirChildren.set([]);
    this.newFolderInputOpen.set(false);
    this.newFolderName = '';
  }

  private async loadChildren(dirId: number | null): Promise<void> {
    if (this.targetVolumeId === null || this.newFolderSegments().length > 0) {
      this.dirChildren.set([]);
      return;
    }
    this.loadingDirs.set(true);
    this.error.set(null);
    try {
      const result = await firstValueFrom(this.catalogApi.children(this.targetVolumeId, dirId));
      this.dirChildren.set(result.directories);
    } catch (e) {
      this.error.set(httpErrorMessage(e));
      this.dirChildren.set([]);
    } finally {
      this.loadingDirs.set(false);
    }
  }

  protected async runPreview(): Promise<void> {
    if (!this.canSubmit) return;
    const folder = this.targetFolder;

    this.previewing.set(true);
    this.preview.set(null);
    this.error.set(null);
    try {
      // Whole batch, one request per item: the backend aggregates the demand and
      // evaluates the ledger once (weighing each folder's subtree), so the verdict
      // covers the entire selection.
      const result = await firstValueFrom(this.api.previewBatch(
        this.items().map(item => this.toMoveRequest(item, folder)),
      ));
      this.preview.set(result);
    } catch (e) {
      this.error.set(httpErrorMessage(e));
    } finally {
      this.previewing.set(false);
    }
  }

  protected async enqueue(): Promise<void> {
    if (!this.canSubmit) return;
    const folder = this.targetFolder;

    this.enqueueing.set(true);
    this.error.set(null);
    try {
      // C25 — one gesture, one request. The loop this replaces could stop halfway and leave
      // part of the selection queued with nothing on screen admitting it; the backend now
      // takes the whole batch in one transaction, so a failure here means nothing was queued
      // and pressing "Accoda" again is safe.
      // The destination folder is sent alone — the backend appends each entity's name.
      const jobs = await firstValueFrom(this.api.enqueueBatch(
        this.items().map(item => this.toMoveRequest(item, folder)),
      ));
      this.enqueuedCount.set(jobs.length);
      // Since 9c a conflicting operation is ACCEPTED and parked behind the job that holds
      // the entity, so the confirmation has to say "queued, waiting" — reporting a plain
      // success would leave the user wondering why nothing moved.
      this.waitingCount.set(jobs.filter(j => j.blockReason === 'DependencyPending').length);
      // Same reasoning, different cause: the backend weighs the batch as ONE demand, so a
      // selection can legitimately come back with its tail parked for space or for a volume
      // that is not connected. Announcing a clean success for operations the user will not
      // see happen is the thing this screen exists to avoid.
      this.parkedOnResourceCount.set(jobs.filter(j =>
        j.blockReason === 'InsufficientSpace'
        || j.blockReason === 'TargetVolumeOffline'
        || j.blockReason === 'SourceVolumeOffline').length);
      this.enqueued.set(true);
      this.completed.emit();
    } catch (e) {
      this.error.set(httpErrorMessage(e));
    } finally {
      this.enqueueing.set(false);
    }
  }

  protected goToQueue(): void {
    this.closed.emit();
    void this.router.navigate(['/queue']);
  }

  protected close(): void {
    this.closed.emit();
  }

  protected onBackdropClick(event: MouseEvent): void {
    if ((event.target as Element).classList.contains('picker-backdrop')) {
      this.close();
    }
  }
}
