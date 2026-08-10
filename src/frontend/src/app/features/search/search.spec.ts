import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { SearchApi } from '../../core/api/search-api.service';
import { VolumesApi } from '../../core/api/volumes-api.service';
import { PagedResult, SearchRequest, SearchResultDto } from '../../core/models/catalog.models';
import { Search } from './search';
import { SearchStore } from './search.store';

const emptyPage: PagedResult<SearchResultDto> = { items: [], totalCount: 0, skip: 0, take: 50 };

describe('Search screen — modified-date filter', () => {
  let fixture: ComponentFixture<Search>;
  let searchSpy: ReturnType<typeof makeSearchSpy>;

  const makeSearchSpy = () => vi.fn((_req: SearchRequest) => of(emptyPage));

  const lastRequest = (): SearchRequest => searchSpy.mock.calls.at(-1)![0];

  const dateInputs = (): HTMLInputElement[] =>
    Array.from(fixture.nativeElement.querySelectorAll('.date-input'));

  const pick = async (input: HTMLInputElement, day: string): Promise<void> => {
    input.value = day;
    input.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    fixture.detectChanges();
  };

  beforeEach(async () => {
    searchSpy = makeSearchSpy();

    await TestBed.configureTestingModule({
      imports: [Search],
      providers: [
        provideZonelessChangeDetection(),
        { provide: SearchApi, useValue: { search: searchSpy } },
        { provide: VolumesApi, useValue: { list: vi.fn(() => of([])) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(Search);
    // A query must be active, otherwise changing a filter is a no-op by design.
    TestBed.inject(SearchStore).setQuery('holiday');
    fixture.detectChanges();
  });

  it('sends the lower bound as the local start of the picked day', async () => {
    await pick(dateInputs()[0], '2026-07-03');

    const from = new Date(lastRequest().modifiedFrom!);
    expect(lastRequest().modifiedFrom!.endsWith('Z')).toBe(true);
    expect(from.getDate()).toBe(3);
    expect(from.getHours()).toBe(0);
    expect(from.getMinutes()).toBe(0);
  });

  it('sends the upper bound as the end of the picked day, so that day stays in', async () => {
    await pick(dateInputs()[1], '2026-07-03');

    const to = new Date(lastRequest().modifiedTo!);
    expect(lastRequest().modifiedTo!.endsWith('Z')).toBe(true);
    expect(to.getDate()).toBe(3);
    expect(to.getHours()).toBe(23);
    expect(to.getMinutes()).toBe(59);
    expect(new Date(2026, 6, 3, 14, 20).getTime()).toBeLessThan(to.getTime());
  });

  it('clears both bounds and re-runs the search', async () => {
    await pick(dateInputs()[0], '2026-07-03');
    await pick(dateInputs()[1], '2026-07-04');

    const clear: HTMLButtonElement = fixture.nativeElement.querySelector('.date-range__clear');
    expect(clear).not.toBeNull();

    clear.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(lastRequest().modifiedFrom).toBeNull();
    expect(lastRequest().modifiedTo).toBeNull();
    expect(fixture.nativeElement.querySelector('.date-range__clear')).toBeNull();
  });
});
