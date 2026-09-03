import { ChangeDetectionStrategy, Component, inject, input, OnInit, output, signal } from '@angular/core';

import { SetupStore } from '../setup.store';

/**
 * Lazy filesystem tree-picker node. Renders the folders at one path; expanding a
 * child loads its children on demand (one level per request) and recurses. The
 * "Monitora" action bubbles the chosen relative path up to the Setup screen.
 */
@Component({
  selector: 'ft-folder-tree',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FolderTree],
  templateUrl: './folder-tree.html',
  styleUrl: './folder-tree.scss',
})
export class FolderTree implements OnInit {
  protected readonly store = inject(SetupStore);

  readonly path = input<string>('');
  readonly depth = input<number>(0);
  readonly pick = output<string>();

  protected readonly expanded = signal<Set<string>>(new Set());

  ngOnInit(): void {
    void this.store.loadFolders(this.path());
  }

  protected toggle(childPath: string): void {
    const next = new Set(this.expanded());
    if (next.has(childPath)) {
      next.delete(childPath);
    } else {
      next.add(childPath);
      void this.store.loadFolders(childPath);
    }
    this.expanded.set(next);
  }

  protected isExpanded(childPath: string): boolean {
    return this.expanded().has(childPath);
  }

  protected loadMore(): void {
    void this.store.loadMoreFolders(this.path());
  }
}
