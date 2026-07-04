import {
  ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

import { QueueApi } from '../../../core/api/queue-api.service';
import { CreateJobRequest, SelectedItem, SelectedItemKind } from '../../../core/models/catalog.models';
import { operationErrorMessage } from '../../api/operation-error';
import { validateLeafName } from '../../validation/name.util';

type DialogMode = 'rename' | 'create';

/**
 * Small modal for the two free-text catalog operations: rename an entity (file or
 * folder → RenameFile/RenameFolder) and create a folder in the current directory
 * (→ CreateFolder). Owns its enqueue and its error surface, mirroring OperationPicker,
 * so the parent only supplies context and listens for `completed`. Name validation
 * (validateLeafName) mirrors the backend and blocks submit before any round-trip;
 * the backend stays the authority and its rejection is shown inline.
 */
@Component({
  selector: 'ft-name-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './name-dialog.html',
  styleUrl: './name-dialog.scss',
})
export class NameDialog {
  readonly mode = input.required<DialogMode>();
  /** Rename target — carries kind/id/volumeId/name. Required for mode="rename". */
  readonly item = input<SelectedItem | null>(null);
  /** Create context — volume and parent folder path. Required for mode="create". */
  readonly volumeId = input<number | null>(null);
  readonly parentPath = input<string>('');
  /** Overrides the subject kind for mode="create" (always Folder today). */
  readonly subjectKind = input<SelectedItemKind>('Folder');

  readonly closed = output<void>();
  readonly completed = output<void>();

  private readonly api = inject(QueueApi);

  /** Seeded from the rename target's current name; freely edited thereafter. */
  protected readonly name = linkedSignal<string>(() => this.item()?.name ?? '');
  protected readonly touched = signal(false);
  protected readonly busy = signal(false);
  protected readonly serverError = signal<string | null>(null);

  protected readonly kind = computed<SelectedItemKind>(() =>
    this.mode() === 'rename' ? (this.item()?.kind ?? 'File') : this.subjectKind());

  protected readonly kindLabel = computed(() => this.kind() === 'Folder' ? 'cartella' : 'file');

  protected readonly title = computed(() =>
    this.mode() === 'rename'
      ? `Rinomina ${this.kindLabel()}`
      : 'Nuova cartella');

  /** Live client-side validation error (null when valid), shown only after interaction. */
  protected readonly validationError = computed(() => validateLeafName(this.name()));

  protected readonly canSubmit = computed(() => !this.busy() && this.validationError() === null);

  protected readonly destination = computed(() => {
    const parent = this.parentPath();
    const leaf = this.name().trim() || '…';
    return parent ? `${parent}\\${leaf}` : leaf;
  });

  protected onInput(value: string): void {
    this.name.set(value);
    this.touched.set(true);
    this.serverError.set(null);
  }

  protected async submit(): Promise<void> {
    this.touched.set(true);
    if (!this.canSubmit()) return;

    const request = this.buildRequest();
    if (!request) return;

    this.busy.set(true);
    this.serverError.set(null);
    try {
      await firstValueFrom(this.api.enqueue(request));
      this.completed.emit();
    } catch (e) {
      this.serverError.set(operationErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  private buildRequest(): CreateJobRequest | null {
    const newName = this.name().trim();

    if (this.mode() === 'rename') {
      const it = this.item();
      if (!it) return null;
      return it.kind === 'Folder'
        ? { type: 'RenameFolder', sourceFileId: null, sourceDirectoryId: it.id, targetVolumeId: null, targetRelativePath: null, newName }
        : { type: 'RenameFile', sourceFileId: it.id, sourceDirectoryId: null, targetVolumeId: null, targetRelativePath: null, newName };
    }

    const vol = this.volumeId();
    if (vol === null) return null;
    const parent = this.parentPath();
    const targetRelativePath = parent ? `${parent}\\${newName}` : newName;
    return {
      type: 'CreateFolder',
      sourceFileId: null,
      sourceDirectoryId: null,
      targetVolumeId: vol,
      targetRelativePath,
      newName: null,
    };
  }

  protected close(): void {
    this.closed.emit();
  }

  protected onBackdropClick(event: MouseEvent): void {
    if ((event.target as Element).classList.contains('dialog-backdrop')) {
      this.close();
    }
  }
}
