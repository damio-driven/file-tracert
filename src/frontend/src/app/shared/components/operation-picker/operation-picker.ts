import {
  ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { QueueApi } from '../../../core/api/queue-api.service';
import { VolumesStore } from '../../../features/volumes/volumes.store';
import { BytesPipe } from '../../pipes/bytes.pipe';
import { FtPill } from '../ft-pill/ft-pill';
import { FeasibilityResult, SelectedFile } from '../../../core/models/catalog.models';

@Component({
  selector: 'ft-operation-picker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, BytesPipe, FtPill],
  templateUrl: './operation-picker.html',
  styleUrl: './operation-picker.scss',
})
export class OperationPicker implements OnInit {
  readonly files = input.required<SelectedFile[]>();
  readonly closed = output<void>();

  protected readonly volumes = inject(VolumesStore);
  private readonly api = inject(QueueApi);
  private readonly router = inject(Router);

  protected targetVolumeId: number | null = null;
  protected targetFolder = '';

  protected readonly preview = signal<FeasibilityResult | null>(null);
  protected readonly previewing = signal(false);
  protected readonly enqueueing = signal(false);
  protected readonly enqueued = signal(false);
  protected readonly enqueuedCount = signal(0);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.volumes.loadList();
    const online = this.volumes.catalogable().find(v => v.isOnline);
    if (online) this.targetVolumeId = online.id;
  }

  protected get totalBytes(): number {
    return this.files().reduce((s, f) => s + f.sizeBytes, 0);
  }

  protected get canSubmit(): boolean {
    return !!this.targetVolumeId && this.targetFolder.trim().length > 0;
  }

  protected async runPreview(): Promise<void> {
    if (!this.canSubmit) return;
    const first = this.files()[0];
    const folder = this.targetFolder.trim();

    this.previewing.set(true);
    this.preview.set(null);
    this.error.set(null);
    try {
      const result = await firstValueFrom(this.api.preview({
        type: 'MoveFile',
        sourceFileId: first.fileId,
        sourceDirectoryId: null,
        targetVolumeId: this.targetVolumeId!,
        targetRelativePath: folder + '\\' + first.name,
        newName: null,
      }));
      this.preview.set(result);
    } catch (e) {
      this.error.set((e as Error).message);
    } finally {
      this.previewing.set(false);
    }
  }

  protected async enqueue(): Promise<void> {
    if (!this.canSubmit) return;
    const folder = this.targetFolder.trim();

    this.enqueueing.set(true);
    this.error.set(null);
    let count = 0;
    try {
      for (const file of this.files()) {
        await firstValueFrom(this.api.enqueue({
          type: 'MoveFile',
          sourceFileId: file.fileId,
          sourceDirectoryId: null,
          targetVolumeId: this.targetVolumeId!,
          targetRelativePath: folder + '\\' + file.name,
          newName: null,
        }));
        count++;
      }
      this.enqueuedCount.set(count);
      this.enqueued.set(true);
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
