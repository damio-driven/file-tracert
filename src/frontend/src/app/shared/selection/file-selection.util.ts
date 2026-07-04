import { SelectedFile } from '../../core/models/catalog.models';

/**
 * Shared multi-file selection logic for the Catalog and Search stores. Both keep a
 * `SelectedFile[]` that survives paging/navigation (fix #6); the toggle/page/all-selected
 * rules were duplicated verbatim in each store — centralized here (C7). The store still owns
 * how it maps its own row type (CatalogFileDto / SearchResultDto) to a SelectedFile.
 */

/** Adds the file if absent, removes it if already selected — matched by fileId. */
export function toggleSelected(selection: SelectedFile[], file: SelectedFile): SelectedFile[] {
  return selection.some(s => s.fileId === file.fileId)
    ? selection.filter(s => s.fileId !== file.fileId)
    : [...selection, file];
}

/** Adds every page file not already selected, preserving existing picks. */
export function addPageToSelection(existing: SelectedFile[], pageFiles: SelectedFile[]): SelectedFile[] {
  const existingIds = new Set(existing.map(f => f.fileId));
  return [...existing, ...pageFiles.filter(f => !existingIds.has(f.fileId))];
}

/** Removes only the files on the current page, leaving cross-page picks intact. */
export function removePageFromSelection(selection: SelectedFile[], pageIds: Iterable<number>): SelectedFile[] {
  const toRemove = new Set(pageIds);
  return selection.filter(f => !toRemove.has(f.fileId));
}

/** True when every file on the page is selected (an empty page is never "all selected"). */
export function isPageFullySelected(pageIds: number[], selection: SelectedFile[]): boolean {
  if (pageIds.length === 0) return false;
  const selected = new Set(selection.map(f => f.fileId));
  return pageIds.every(id => selected.has(id));
}
