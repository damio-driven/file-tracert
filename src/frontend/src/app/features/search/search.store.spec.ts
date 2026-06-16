import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { SearchApi } from '../../core/api/search-api.service';
import { PagedResult, SearchResultDto } from '../../core/models/catalog.models';
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
});
