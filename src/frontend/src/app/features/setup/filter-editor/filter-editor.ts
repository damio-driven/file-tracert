import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

import { FilterSettingsDto } from '../../../core/models/catalog.models';

interface CategoryDef {
  key: string;
  label: string;
  extensions: string[];
}

/** Category → representative extensions (mirrors the ExtensionCategories seed, abridged). */
const CATEGORIES: CategoryDef[] = [
  { key: 'image', label: 'Immagini', extensions: ['jpg', 'jpeg', 'png', 'gif', 'bmp', 'tiff', 'webp', 'heic', 'raw', 'svg'] },
  { key: 'video', label: 'Video', extensions: ['mp4', 'mov', 'avi', 'mkv', 'wmv', 'webm', 'm4v', 'mpg', 'mpeg'] },
  { key: 'audio', label: 'Audio', extensions: ['mp3', 'wav', 'flac', 'aac', 'ogg', 'wma', 'm4a', 'opus'] },
  { key: 'document', label: 'Documenti', extensions: ['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'txt', 'md', 'csv'] },
  { key: 'archive', label: 'Archivi', extensions: ['zip', 'rar', '7z', 'tar', 'gz', 'iso'] },
];

/**
 * Editor for a file-type filter expressed as category toggles. "All types" = no
 * categories selected → empty allow-list. Emits a full FilterSettingsDto on save.
 */
@Component({
  selector: 'ft-filter-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './filter-editor.html',
  styleUrl: './filter-editor.scss',
})
export class FilterEditor {
  readonly value = input<FilterSettingsDto>({ allowedExtensions: [], excludedPaths: [] });
  readonly save = output<FilterSettingsDto>();

  protected readonly categories = CATEGORIES;
  protected readonly selected = signal<Set<string>>(new Set());

  protected readonly allTypes = computed(() => this.selected().size === 0);

  constructor() {
    queueMicrotask(() => {
      const allowed = new Set(this.value().allowedExtensions);
      const on = new Set(CATEGORIES.filter((c) => c.extensions.some((e) => allowed.has(e))).map((c) => c.key));
      this.selected.set(on);
    });
  }

  protected toggle(key: string): void {
    const next = new Set(this.selected());
    next.has(key) ? next.delete(key) : next.add(key);
    this.selected.set(next);
  }

  protected isOn(key: string): boolean {
    return this.selected().has(key);
  }

  protected emitSave(): void {
    const extensions = CATEGORIES.filter((c) => this.selected().has(c.key)).flatMap((c) => c.extensions);
    this.save.emit({ allowedExtensions: extensions, excludedPaths: this.value().excludedPaths });
  }
}
