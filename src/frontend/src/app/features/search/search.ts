import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { LowerCasePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ScrollingModule } from '@angular/cdk/scrolling';

import { SearchStore } from './search.store';
import { VolumesStore } from '../volumes/volumes.store';
import { BytesPipe } from '../../shared/pipes/bytes.pipe';
import { RelativeTimePipe } from '../../shared/pipes/relative-time.pipe';
import { FtPill } from '../../shared/components/ft-pill/ft-pill';
import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { OperationPicker } from '../../shared/components/operation-picker/operation-picker';
import { FileCategory, SearchResultDto, SearchScope, SearchSort, SelectedItem } from '../../core/models/catalog.models';

@Component({
  selector: 'ft-search',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, ScrollingModule, LowerCasePipe, BytesPipe, RelativeTimePipe, FtPill, FtPanel, OperationPicker],
  templateUrl: './search.html',
  styleUrl: './search.scss',
})
export class Search implements OnInit {
  protected readonly store = inject(SearchStore);
  protected readonly volumes = inject(VolumesStore);
  protected readonly Math = Math;

  protected queryText = '';
  protected pickerOpen = false;

  // The store keeps full SelectedItem objects, so the picker gets the whole selection
  // even when it spans results pages that are no longer visible (fix #6).
  protected readonly pickerItems = computed<SelectedItem[]>(() => this.store.selectedItems());

  protected readonly CATEGORIES: { value: FileCategory; label: string; icon: string }[] = [
    { value: 'Image', label: 'Immagini', icon: 'IMG' },
    { value: 'Video', label: 'Video', icon: 'VID' },
    { value: 'Audio', label: 'Audio', icon: 'AUD' },
    { value: 'Document', label: 'Documenti', icon: 'DOC' },
    { value: 'Archive', label: 'Archivi', icon: 'ARC' },
  ];

  protected readonly SORTS: { value: SearchSort; label: string }[] = [
    { value: 'Relevance', label: 'Rilevanza' },
    { value: 'Name', label: 'Nome' },
    { value: 'Date', label: 'Data' },
    { value: 'Size', label: 'Dimensione' },
  ];

  ngOnInit(): void {
    void this.volumes.loadList();
  }

  protected onSubmit(): void {
    this.store.setQuery(this.queryText);
    void this.store.search();
  }

  protected setScope(scope: SearchScope): void {
    this.store.setScope(scope);
    if (this.store.text()) void this.store.search();
  }

  protected setSort(sort: SearchSort): void {
    const wasSort = this.store.sort() === sort;
    this.store.setSort(sort, wasSort && !this.store.desc());
    if (this.store.text()) void this.store.search();
  }

  protected toggleCategory(cat: FileCategory): void {
    const current = this.store.filters().category;
    this.store.setFilters({ category: current === cat ? null : cat });
    if (this.store.text()) void this.store.search();
  }

  protected toggleOnlineOnly(): void {
    this.store.setFilters({ onlineOnly: !this.store.filters().onlineOnly });
    if (this.store.text()) void this.store.search();
  }

  protected goToPage(skip: number): void {
    void this.store.loadPage(skip);
  }

  protected catIcon(cat: FileCategory): string {
    return this.CATEGORIES.find((c) => c.value === cat)?.icon ?? '???';
  }

  protected catLabel(cat: FileCategory): string {
    return this.CATEGORIES.find((c) => c.value === cat)?.label ?? cat;
  }

  protected get hasPrev(): boolean {
    return (this.store.results()?.skip ?? 0) > 0;
  }

  protected get hasNext(): boolean {
    const r = this.store.results();
    if (!r) return false;
    return r.skip + r.items.length < r.totalCount;
  }

  protected get prevSkip(): number {
    const r = this.store.results();
    if (!r) return 0;
    return Math.max(0, r.skip - r.take);
  }

  protected get nextSkip(): number {
    const r = this.store.results();
    if (!r) return 0;
    return r.skip + r.take;
  }

  protected toggleSelect(result: SearchResultDto): void {
    this.store.toggleSelection(result);
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
