import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { delay, of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { SearchApi } from '../../core/api/search-api.service';
import { PagedResult, SearchRequest, SearchResultDto } from '../../core/models/catalog.models';
import { SearchStore } from './search.store';

const mockResult: PagedResult<SearchResultDto> = {
  items: [
    {
      fileId: 1, name: 'photo.jpg', relativePath: 'Photos\\photo.jpg',
      volumeId: 1, volumeLabel: 'Disk', volumeLetter: 'D:', volumeIsOnline: true,
      sizeBytes: 1024, modifiedUtc: '2026-01-01T00:00:00Z',
      category: 'Image', projectedState: 'None',
    },
  ],
  totalCount: 1,
  skip: 0,
  take: 50,
};

function setup(apiMock: Partial<SearchApi> = {}) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: SearchApi, useValue: { search: vi.fn(() => of(mockResult)), ...apiMock } },
    ],
  });
  return TestBed.inject(SearchStore);
}

describe('SearchStore', () => {
  it('initialises with empty state', () => {
    const store = setup();
    expect(store.text()).toBe('');
    expect(store.results()).toBeNull();
    expect(store.loading()).toBe(false);
  });

  it('setQuery updates text signal', () => {
    const store = setup();
    store.setQuery('holiday');
    expect(store.text()).toBe('holiday');
  });

  it('search calls API and populates results', async () => {
    const store = setup();
    store.setQuery('photo');
    await store.search();
    expect(store.results()?.totalCount).toBe(1);
    expect(store.results()?.items[0].name).toBe('photo.jpg');
  });

  it('search with empty text is a no-op', async () => {
    const searchSpy = vi.fn(() => of(mockResult));
    const store = setup({ search: searchSpy });
    await store.search(); // text is ''
    expect(searchSpy).not.toHaveBeenCalled();
  });

  it('forwards the date bounds to the API untouched', async () => {
    const searchSpy = vi.fn((_req: SearchRequest) => of(mockResult));
    const store = setup({ search: searchSpy });
    store.setQuery('photo');
    store.setFilters({
      modifiedFrom: '2026-07-03T00:00:00.000Z',
      modifiedTo: '2026-07-03T23:59:59.999Z',
    });

    await store.search();

    const request = searchSpy.mock.calls.at(-1)![0];
    expect(request.modifiedFrom).toBe('2026-07-03T00:00:00.000Z');
    expect(request.modifiedTo).toBe('2026-07-03T23:59:59.999Z');
  });

  it('ignores a stale response that lands after a newer search', async () => {
    const slowPage: PagedResult<SearchResultDto> = { ...mockResult, totalCount: 999 };
    let call = 0;
    const searchSpy = vi.fn((_req: SearchRequest) =>
      ++call === 1 ? of(slowPage).pipe(delay(30)) : of(mockResult));
    const store = setup({ search: searchSpy });
    store.setQuery('photo');

    const stale = store.search();
    const fresh = store.search();
    await Promise.all([stale, fresh]);

    expect(store.results()?.totalCount).toBe(1);
  });

  it('setFilters merges partial filter state', () => {
    const store = setup();
    store.setFilters({ category: 'Image' });
    expect(store.filters().category).toBe('Image');
    expect(store.filters().onlineOnly).toBe(false);
  });

  it('setScope changes scope signal', () => {
    const store = setup();
    store.setScope('FullPath');
    expect(store.scope()).toBe('FullPath');
  });

  it('isCapped is true when totalCount equals 10000', async () => {
    const cappedResult: PagedResult<SearchResultDto> = { ...mockResult, totalCount: 10000 };
    const store = setup({ search: vi.fn(() => of(cappedResult)) });
    store.setQuery('x');
    await store.search();
    expect(store.isCapped()).toBe(true);
  });

  it('error state populated on API failure', async () => {
    const store = setup({ search: vi.fn(() => throwError(() => new Error('Network error'))) });
    store.setQuery('x');
    await store.search();
    expect(store.error()).toBe('Network error');
    expect(store.loading()).toBe(false);
  });

  it('clear resets text and results', async () => {
    const store = setup();
    store.setQuery('photo');
    await store.search();
    store.clear();
    expect(store.text()).toBe('');
    expect(store.results()).toBeNull();
  });

  // ── selection ────────────────────────────────────────────────────────────

  const photoResult = mockResult.items[0];

  it('toggleSelection adds the full file', async () => {
    const store = setup();
    store.setQuery('photo');
    await store.search();
    store.toggleSelection(photoResult);
    expect(store.selectedFileIds()).toContain(1);
    expect(store.hasSelection()).toBe(true);
    expect(store.selectionCount()).toBe(1);
    expect(store.selectedItems()).toEqual([
      { kind: 'File', id: 1, name: 'photo.jpg', sizeBytes: 1024, volumeId: 1, relativePath: 'Photos\\photo.jpg' },
    ]);
  });

  it('toggleSelection removes an already-selected file', async () => {
    const store = setup();
    store.setQuery('photo');
    await store.search();
    store.toggleSelection(photoResult);
    store.toggleSelection(photoResult);
    expect(store.selectedFileIds()).not.toContain(1);
    expect(store.hasSelection()).toBe(false);
  });

  it('clearSelection empties selectedFiles', async () => {
    const store = setup();
    store.setQuery('photo');
    await store.search();
    store.toggleSelection(photoResult);
    store.clearSelection();
    expect(store.selectedItems()).toHaveLength(0);
  });

  // FIX #6 — selection made on one results page must keep its full data after paging
  // to another page (the picker enqueues from selectedFiles, not from the visible page).
  it('selection keeps full file data after loading a different page', async () => {
    const page2: PagedResult<SearchResultDto> = {
      ...mockResult,
      items: [{
        fileId: 2, name: 'clip.mp4', relativePath: 'Videos\\clip.mp4',
        volumeId: 2, volumeLabel: 'Disk', volumeLetter: 'E:', volumeIsOnline: true,
        sizeBytes: 5000, modifiedUtc: '2026-01-01T00:00:00Z',
        category: 'Video', projectedState: 'None',
      }],
      totalCount: 2,
      skip: 1,
    };
    const searchSpy = vi.fn((req: { skip: number }) => of(req.skip === 0 ? mockResult : page2));
    const store = setup({ search: searchSpy as unknown as SearchApi['search'] });
    store.setQuery('x');
    await store.search();

    store.toggleSelection(photoResult);
    await store.loadPage(1); // photo.jpg is no longer on the visible page

    expect(store.selectionCount()).toBe(1);
    expect(store.selectedItems()).toEqual([
      { kind: 'File', id: 1, name: 'photo.jpg', sizeBytes: 1024, volumeId: 1, relativePath: 'Photos\\photo.jpg' },
    ]);
  });

  it('selectPage adds all page file IDs', async () => {
    const store = setup();
    store.setQuery('photo');
    await store.search();
    store.selectPage();
    expect(store.selectedFileIds()).toContain(1);
    expect(store.allPageSelected()).toBe(true);
  });

  it('deselectPage removes page file IDs', async () => {
    const store = setup();
    store.setQuery('photo');
    await store.search();
    store.selectPage();
    store.deselectPage();
    expect(store.selectedFileIds()).not.toContain(1);
    expect(store.allPageSelected()).toBe(false);
  });

  it('allPageSelected false when only some items selected', async () => {
    const twoItems: PagedResult<SearchResultDto> = {
      ...mockResult,
      items: [
        { ...mockResult.items[0] },
        {
          fileId: 2, name: 'clip.mp4', relativePath: 'Videos\\clip.mp4',
          volumeId: 1, volumeLabel: 'Disk', volumeLetter: 'D:', volumeIsOnline: true,
          sizeBytes: 5000, modifiedUtc: '2026-01-01T00:00:00Z',
          category: 'Video', projectedState: 'None',
        },
      ],
      totalCount: 2,
    };
    const store = setup({ search: vi.fn(() => of(twoItems)) });
    store.setQuery('x');
    await store.search();
    store.toggleSelection(photoResult);
    expect(store.allPageSelected()).toBe(false);
  });

  it('clear also resets selectedFileIds', async () => {
    const store = setup();
    store.setQuery('photo');
    await store.search();
    store.toggleSelection(photoResult);
    store.clear();
    expect(store.selectedFileIds()).toHaveLength(0);
  });
});
