import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { ScrollingModule } from '@angular/cdk/scrolling';

import { CatalogStore } from './catalog.store';
import { VolumesStore } from '../volumes/volumes.store';
import { BytesPipe } from '../../shared/pipes/bytes.pipe';
import { RelativeTimePipe } from '../../shared/pipes/relative-time.pipe';
import { FtPill } from '../../shared/components/ft-pill/ft-pill';
import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { OperationPicker } from '../../shared/components/operation-picker/operation-picker';
import { CatalogFileDto, FileCategory, SelectedFile, VolumeDto } from '../../core/models/catalog.models';

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
  imports: [ScrollingModule, BytesPipe, RelativeTimePipe, FtPill, FtPanel, OperationPicker],
  templateUrl: './catalog.html',
  styleUrl: './catalog.scss',
})
export class Catalog implements OnInit {
  protected readonly store = inject(CatalogStore);
  protected readonly volumes = inject(VolumesStore);
  protected readonly Math = Math;

  protected pickerOpen = false;

  // The store keeps full SelectedFile objects, so the picker gets the whole selection
  // even when it spans folders/pages that are no longer visible (fix #6).
  protected readonly pickerFiles = computed<SelectedFile[]>(() => this.store.selectedFiles());

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

  protected toggleSelectAll(): void {
    if (this.store.allPageSelected()) {
      this.store.deselectPage();
    } else {
      this.store.selectPage();
    }
  }

  protected isSelected(fileId: number): boolean {
    return this.store.selectedFileIds().includes(fileId);
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
}
