import { SelectedItem } from '../../core/models/catalog.models';

/**
 * Shared multi-item selection logic for the Catalog (files + folders) and Search
 * (files only) stores. Both keep a `SelectedItem[]` that survives paging/navigation
 * (fix #6); the toggle/page/all-selected rules were duplicated verbatim in each store
 * — centralized here (C7), then generalized to mixed file+folder selection (step 8).
 *
 * A file and a folder can share the same numeric id (Files and Directories are
 * separate tables), so every comparison is keyed by kind+id, never by id alone.
 * The store still owns how it maps its own row type to a SelectedItem.
 */

/** Stable identity of a selected item across kinds. */
export function selectionKey(item: SelectedItem): string {
  return `${item.kind}:${item.id}`;
}

/** Adds the item if absent, removes it if already selected — matched by kind+id. */
export function toggleSelected(selection: SelectedItem[], item: SelectedItem): SelectedItem[] {
  const key = selectionKey(item);
  return selection.some(s => selectionKey(s) === key)
    ? selection.filter(s => selectionKey(s) !== key)
    : [...selection, item];
}

/** Adds every page item not already selected, preserving existing picks. */
export function addPageToSelection(existing: SelectedItem[], pageItems: SelectedItem[]): SelectedItem[] {
  const existingKeys = new Set(existing.map(selectionKey));
  return [...existing, ...pageItems.filter(i => !existingKeys.has(selectionKey(i)))];
}

/** Removes only the items on the current page, leaving cross-page picks intact. */
export function removePageFromSelection(selection: SelectedItem[], pageKeys: Iterable<string>): SelectedItem[] {
  const toRemove = new Set(pageKeys);
  return selection.filter(i => !toRemove.has(selectionKey(i)));
}

/** True when every item on the page is selected (an empty page is never "all selected"). */
export function isPageFullySelected(pageKeys: string[], selection: SelectedItem[]): boolean {
  if (pageKeys.length === 0) return false;
  const selected = new Set(selection.map(selectionKey));
  return pageKeys.every(key => selected.has(key));
}
