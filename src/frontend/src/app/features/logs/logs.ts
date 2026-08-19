import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';

import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { FtPill, PillVariant } from '../../shared/components/ft-pill/ft-pill';
import { LogsStore } from './logs.store';

const LEVELS = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'] as const;

/** Log section: a server-paged, filterable view of the log DB + runtime level control. */
@Component({
  selector: 'ft-logs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FtPanel, FtPill],
  templateUrl: './logs.html',
  styleUrl: './logs.scss',
})
export class Logs implements OnInit {
  private readonly store = inject(LogsStore);

  protected readonly entries = this.store.entries;
  protected readonly totalCount = this.store.totalCount;
  protected readonly loading = this.store.loading;
  protected readonly error = this.store.error;
  protected readonly currentLevel = this.store.currentLevel;
  // Persisted filter state — bound to the controls so they stay in sync with the
  // results when navigating away and back (the store is an app-level singleton).
  protected readonly level = this.store.level;
  protected readonly category = this.store.category;
  protected readonly search = this.store.search;
  protected readonly page = this.store.page;
  protected readonly pageCount = this.store.pageCount;
  protected readonly hasPrev = this.store.hasPrev;
  protected readonly hasNext = this.store.hasNext;

  protected readonly levels = LEVELS;
  protected readonly expanded = signal<ReadonlySet<number>>(new Set());

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // C29 — the debounce must not outlive the view. `LogsStore` is app-level, so a timer that
    // fires after the user has navigated away rewrites the filters of a screen nobody is
    // looking at and spends a request doing it. Whoever comes back would find results they
    // never asked for.
    inject(DestroyRef).onDestroy(() => this.clearSearchTimer());
  }

  ngOnInit(): void {
    void this.store.init();
  }

  protected onMinLevel(value: string): void {
    void this.store.applyFilters({ level: value || null });
  }

  protected onCategory(value: string): void {
    void this.store.applyFilters({ category: value });
  }

  protected onSearch(value: string): void {
    this.clearSearchTimer();
    this.searchTimer = setTimeout(() => {
      this.searchTimer = null;
      void this.store.applyFilters({ search: value });
    }, 300);
  }

  private clearSearchTimer(): void {
    if (this.searchTimer !== null) {
      clearTimeout(this.searchTimer);
      this.searchTimer = null;
    }
  }

  protected onLevelChange(value: string): void {
    void this.store.changeLevel(value);
  }

  protected toggle(id: number): void {
    this.expanded.update((set) => {
      const next = new Set(set);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });
  }

  protected isExpanded(id: number): boolean {
    return this.expanded().has(id);
  }

  protected next(): void {
    void this.store.nextPage();
  }

  protected prev(): void {
    void this.store.prevPage();
  }

  protected variant(level: string): PillVariant {
    switch (level) {
      case 'Critical':
      case 'Error':
        return 'block';
      case 'Warning':
        return 'wait';
      case 'Information':
        return 'run';
      default:
        return 'idle';
    }
  }

  protected formatTime(iso: string): string {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) {
      return iso;
    }
    return d.toLocaleString('it-IT', {
      day: '2-digit',
      month: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    });
  }
}
