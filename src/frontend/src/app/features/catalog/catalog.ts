import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ScrollingModule } from '@angular/cdk/scrolling';

import { CatalogStore } from './catalog.store';
import { VolumesStore } from '../volumes/volumes.store';
import { BytesPipe } from '../../shared/pipes/bytes.pipe';
import { RelativeTimePipe } from '../../shared/pipes/relative-time.pipe';
import { FtPill } from '../../shared/components/ft-pill/ft-pill';
import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { OperationPicker } from '../../shared/components/operation-picker/operation-picker';
import { NameDialog } from '../../shared/components/name-dialog/name-dialog';
import { CatalogDirDto, CatalogFileDto, FileCategory, SelectedItem, VolumeDto } from '../../core/models/catalog.models';

const CATEGORY_LABELS: Record<FileCategory, string> = {
  Image: 'Immagine', Video: 'Video', Audio: 'Audio',
  Document: 'Documento', Archive: 'Archivio', Other: 'Altro',
};

const CATEGORY_ICONS: Record<FileCategory, string> = {
  Image: 'IMG', Video: 'VID', Audio: 'AUD',
  Document: 'DOC', Archive: 'ARC', Other: '???',
};

@Component({
  selector: 'ft-catalog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ScrollingModule, BytesPipe, RelativeTimePipe, FtPill, FtPanel, OperationPicker, NameDialog],
  templateUrl: './catalog.html',
  styleUrl: './catalog.scss',
})
export class Catalog implements OnInit {
  protected readonly store = inject(CatalogStore);
  protected readonly volumes = inject(VolumesStore);
  protected readonly Math = Math;

  protected pickerOpen = false;
  protected readonly renameTarget = signal<SelectedItem | null>(null);
  protected readonly newFolderOpen = signal(false);

  // The store keeps full SelectedItem objects (files + folders), so the picker gets the
  // whole selection even when it spans folders/pages that are no longer visible (fix #6).
  protected readonly pickerItems = computed<SelectedItem[]>(() => this.store.selectedItems());

  ngOnInit(): void {
    void this.volumes.loadList();
  }

  protected selectVolume(vol: VolumeDto): void {
    void this.store.selectVolume(vol);
  }

  protected openDir(id: number, name: string, path: string): void {
    void this.store.openDirectory(id, name, path);
  }

  protected navigateTo(index: number): void {
    void this.store.navigateTo(index);
  }

  protected loadFilePage(skip: number): void {
    void this.store.loadFilePage(skip);
  }

  protected catLabel(cat: FileCategory): string {
    return CATEGORY_LABELS[cat] ?? cat;
  }

  protected catIcon(cat: FileCategory): string {
    return CATEGORY_ICONS[cat] ?? '???';
  }

  protected get hasPrev(): boolean {
    return (this.store.children()?.files.skip ?? 0) > 0;
  }

  protected get hasNext(): boolean {
    const f = this.store.children()?.files;
    if (!f) return false;
    return f.skip + f.items.length < f.totalCount;
  }

  protected get prevSkip(): number {
    const f = this.store.children()?.files;
    if (!f) return 0;
    return Math.max(0, f.skip - f.take);
  }

  protected get nextSkip(): number {
    const f = this.store.children()?.files;
    if (!f) return 0;
    return f.skip + f.take;
  }

  protected toggleSelect(file: CatalogFileDto): void {
    this.store.toggleSelection(file);
  }

  protected toggleSelectDir(dir: CatalogDirDto): void {
    this.store.toggleDirSelection(dir);
  }

  protected toggleSelectAll(): void {
    if (this.store.allPageSelected()) {
      this.store.deselectPage();
    } else {
      this.store.selectPage();
    }
  }

  protected isFileSelected(id: number): boolean {
    return this.store.selectedKeys().has(`File:${id}`);
  }

  protected isDirSelected(id: number): boolean {
    return this.store.selectedKeys().has(`Folder:${id}`);
  }

  protected openPicker(): void {
    this.pickerOpen = true;
  }

  /** Closing the picker (Annulla/backdrop) must not lose the selection (UX fix). */
  protected closePicker(): void {
    this.pickerOpen = false;
  }

  /** Only a successful enqueue consumes the selection. */
  protected onPickerCompleted(): void {
    this.store.clearSelection();
  }

  // ── rename / new-folder dialogs ─────────────────────────────────────────────

  protected openRename(target: SelectedItem): void {
    this.renameTarget.set(target);
  }

  protected closeRename(): void {
    this.renameTarget.set(null);
  }

  protected openNewFolder(): void {
    this.newFolderOpen.set(true);
  }

  protected closeNewFolder(): void {
    this.newFolderOpen.set(false);
  }

  /** A dialog enqueued its job: close it and drop the (now-actioned) selection. */
  protected onDialogCompleted(): void {
    this.renameTarget.set(null);
    this.newFolderOpen.set(false);
    this.store.clearSelection();
  }

  /** Noun phrase for the selection bar; the count is rendered separately. */
  protected selectionLabel(): string {
    const total = this.store.selectionCount();
    const folders = this.store.folderSelectionCount();
    const files = total - folders;

    if (total === 1) return folders === 1 ? 'cartella selezionata' : 'file selezionato';
    if (folders === 0) return 'file selezionati';
    if (files === 0) return 'cartelle selezionate';
    return 'elementi selezionati';
  }
}
