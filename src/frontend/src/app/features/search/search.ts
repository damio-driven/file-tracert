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
import { localDayEndToUtcIso, localDayStartToUtcIso, utcIsoToLocalDay } from '../../shared/date/day-range.util';
import { OperationPicker, PickerMode } from '../../shared/components/operation-picker/operation-picker';
import { FtProjectionBadge } from '../../shared/components/ft-projection-badge/ft-projection-badge';
import { FILE_CATEGORIES, fileCategoryLabel, fileCategoryTag } from '../../shared/file-category';
import { FileCategory, SearchResultDto, SearchScope, SearchSort, SelectedItem } from '../../core/models/catalog.models';

@Component({
  selector: 'ft-search',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule, ScrollingModule, LowerCasePipe, BytesPipe, RelativeTimePipe, FtPill, FtPanel,
    FtProjectionBadge, OperationPicker,
  ],
  templateUrl: './search.html',
  styleUrl: './search.scss',
})
export class Search implements OnInit {
  protected readonly store = inject(SearchStore);
  protected readonly volumes = inject(VolumesStore);
  protected readonly Math = Math;

  protected queryText = '';
  protected pickerOpen = false;
  /**
   * Which verb the picker was opened with. Set by the button the user pressed, so the choice is
   * made where the intent is formed rather than by a control inside the dialog.
   */
  protected pickerMode: PickerMode = 'move';

  // The store keeps full SelectedItem objects, so the picker gets the whole selection
  // even when it spans results pages that are no longer visible (fix #6).
  protected readonly pickerItems = computed<SelectedItem[]>(() => this.store.selectedItems());

  protected readonly modifiedFromDay = computed(() => utcIsoToLocalDay(this.store.filters().modifiedFrom));
  protected readonly modifiedToDay = computed(() => utcIsoToLocalDay(this.store.filters().modifiedTo));
  protected readonly hasDateFilter = computed(() => !!this.modifiedFromDay() || !!this.modifiedToDay());

  protected readonly categories = FILE_CATEGORIES;

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
    this.rerun();
  }

  protected setSort(sort: SearchSort): void {
    const wasSort = this.store.sort() === sort;
    this.store.setSort(sort, wasSort && !this.store.desc());
    this.rerun();
  }

  protected toggleCategory(cat: FileCategory): void {
    const current = this.store.filters().category;
    this.store.setFilters({ category: current === cat ? null : cat });
    this.rerun();
  }

  protected toggleOnlineOnly(): void {
    this.store.setFilters({ onlineOnly: !this.store.filters().onlineOnly });
    this.rerun();
  }

  // The date inputs speak local calendar days; the API speaks UTC instants. The bounds
  // widen to cover the whole picked day, so "fino al 3/7" keeps the files modified that
  // afternoon (see day-range.util).
  protected setModifiedFrom(day: string): void {
    this.setBound('from', day);
  }

  protected setModifiedTo(day: string): void {
    this.setBound('to', day);
  }

  protected clearDates(): void {
    this.store.setFilters({ modifiedFrom: null, modifiedTo: null });
    this.rerun();
  }

  /**
   * An empty field clears that end. A day the util cannot read (a half-typed year, a value
   * a non-native picker produced) leaves the applied filter alone: dropping it there would
   * re-run the search unfiltered while the input still showed a date. A bound that would
   * invert the range drags the other end with it, instead of sending from > to and
   * answering "nessun risultato" for a reason nothing on screen explains.
   */
  private setBound(edge: 'from' | 'to', day: string): void {
    if (day.trim() === '') {
      this.store.setFilters(edge === 'from' ? { modifiedFrom: null } : { modifiedTo: null });
      this.rerun();
      return;
    }

    const start = localDayStartToUtcIso(day);
    const end = localDayEndToUtcIso(day);
    if (start === null || end === null) {
      return;
    }

    const { modifiedFrom, modifiedTo } = this.store.filters();
    if (edge === 'from') {
      const inverted = modifiedTo !== null && Date.parse(start) > Date.parse(modifiedTo);
      this.store.setFilters({ modifiedFrom: start, ...(inverted ? { modifiedTo: end } : {}) });
    } else {
      const inverted = modifiedFrom !== null && Date.parse(end) < Date.parse(modifiedFrom);
      this.store.setFilters({ modifiedTo: end, ...(inverted ? { modifiedFrom: start } : {}) });
    }

    this.rerun();
  }

  /** A filter change only re-runs the search once a query is actually active. */
  private rerun(): void {
    if (this.store.text()) void this.store.search();
  }

  protected goToPage(skip: number): void {
    void this.store.loadPage(skip);
  }

  protected catIcon(cat: FileCategory): string {
    return fileCategoryTag(cat);
  }

  protected catLabel(cat: FileCategory): string {
    return fileCategoryLabel(cat);
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

  protected openPicker(mode: PickerMode = 'move'): void {
    this.pickerMode = mode;
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
