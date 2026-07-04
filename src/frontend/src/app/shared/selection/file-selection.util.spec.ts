import { describe, expect, it } from 'vitest';

import { SelectedItem } from '../../core/models/catalog.models';
import {
  addPageToSelection, isPageFullySelected, removePageFromSelection, selectionKey, toggleSelected,
} from './file-selection.util';

function file(id: number): SelectedItem {
  return { kind: 'File', id, name: `f${id}`, sizeBytes: id, volumeId: 1, relativePath: `f${id}` };
}
function folder(id: number): SelectedItem {
  return { kind: 'Folder', id, name: `d${id}`, sizeBytes: 0, volumeId: 1, relativePath: `d${id}` };
}

describe('selectionKey', () => {
  it('is distinct for a file and a folder that share a numeric id', () => {
    expect(selectionKey(file(1))).toBe('File:1');
    expect(selectionKey(folder(1))).toBe('Folder:1');
    expect(selectionKey(file(1))).not.toBe(selectionKey(folder(1)));
  });
});

describe('toggleSelected (mixed items)', () => {
  it('adds an absent item', () => {
    expect(toggleSelected([], file(1))).toEqual([file(1)]);
  });

  it('removes an item already selected, matched by kind+id', () => {
    expect(toggleSelected([file(1), folder(2)], file(1))).toEqual([folder(2)]);
  });

  it('keeps a file and a folder with the same id as two distinct picks', () => {
    const afterFolder = toggleSelected([], folder(1));
    const afterBoth = toggleSelected(afterFolder, file(1));
    expect(afterBoth).toHaveLength(2);
    // toggling the file off leaves the folder intact
    expect(toggleSelected(afterBoth, file(1))).toEqual([folder(1)]);
  });
});

describe('addPageToSelection', () => {
  it('adds only the not-yet-selected items', () => {
    const result = addPageToSelection([file(1)], [file(1), file(2), folder(1)]);
    expect(result).toEqual([file(1), file(2), folder(1)]);
  });
});

describe('removePageFromSelection', () => {
  it('drops only the given keys, keeping cross-page and cross-kind picks', () => {
    const result = removePageFromSelection([file(1), folder(1), file(2)], ['File:1']);
    expect(result).toEqual([folder(1), file(2)]);
  });
});

describe('isPageFullySelected', () => {
  it('true only when every page key is selected', () => {
    const sel = [file(1), file(2)];
    expect(isPageFullySelected(['File:1', 'File:2'], sel)).toBe(true);
    expect(isPageFullySelected(['File:1', 'File:3'], sel)).toBe(false);
  });

  it('an empty page is never fully selected', () => {
    expect(isPageFullySelected([], [file(1)])).toBe(false);
  });
});
