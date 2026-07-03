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
import {
  CatalogDirDto, FeasibilityResult, SelectedFile, VolumeDto,
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
  readonly files = input.required<SelectedFile[]>();
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
  protected newFolderName = '';

  protected readonly preview = signal<FeasibilityResult | null>(null);
  protected readonly previewing = signal(false);
  protected readonly enqueueing = signal(false);
  protected readonly enqueued = signal(false);
  protected readonly enqueuedCount = signal(0);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.volumes.loadList();
    const online = this.volumes.catalogable().find(v => v.isOnline);
    if (online) {
      this.targetVolumeId = online.id;
      void this.loadChildren(null);
    }
  }

  protected get totalBytes(): number {
    return this.files().reduce((s, f) => s + f.sizeBytes, 0);
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
    this.newFolderInputOpen.set(true);
  }

  protected cancelNewFolder(): void {
    this.newFolderInputOpen.set(false);
    this.newFolderName = '';
  }

  protected confirmNewFolder(): void {
    const name = this.newFolderName.trim();
    if (!name) return;
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
      this.error.set((e as Error).message);
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
      // Whole batch, one request per file: the backend aggregates the demand and
      // evaluates the ledger once, so the verdict covers the entire selection.
      const result = await firstValueFrom(this.api.previewBatch(
        this.files().map(file => ({
          type: 'MoveFile' as const,
          sourceFileId: file.fileId,
          sourceDirectoryId: null,
          targetVolumeId: this.targetVolumeId!,
          targetRelativePath: folder,
          newName: null,
        })),
      ));
      this.preview.set(result);
    } catch (e) {
      this.error.set((e as Error).message);
    } finally {
      this.previewing.set(false);
    }
  }

  protected async enqueue(): Promise<void> {
    if (!this.canSubmit) return;
    const folder = this.targetFolder;

    this.enqueueing.set(true);
    this.error.set(null);
    let count = 0;
    try {
      for (const file of this.files()) {
        // Send the destination folder only — the backend appends the file name.
        await firstValueFrom(this.api.enqueue({
          type: 'MoveFile',
          sourceFileId: file.fileId,
          sourceDirectoryId: null,
          targetVolumeId: this.targetVolumeId!,
          targetRelativePath: folder,
          newName: null,
        }));
        count++;
      }
      this.enqueuedCount.set(count);
      this.enqueued.set(true);
      this.completed.emit();
    } catch (e) {
      this.error.set((e as Error).message);
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
