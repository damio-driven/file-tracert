import { describe, expect, it } from 'vitest';

import { localDayEndToUtcIso, localDayStartToUtcIso, utcIsoToLocalDay } from './day-range.util';

describe('day-range.util', () => {
  it('turns a picked day into the UTC instant of its local midnight', () => {
    const iso = localDayStartToUtcIso('2026-07-03');

    expect(iso).not.toBeNull();
    expect(iso!.endsWith('Z')).toBe(true);

    const asLocal = new Date(iso!);
    expect(asLocal.getFullYear()).toBe(2026);
    expect(asLocal.getMonth()).toBe(6);
    expect(asLocal.getDate()).toBe(3);
    expect(asLocal.getHours()).toBe(0);
    expect(asLocal.getMinutes()).toBe(0);
  });

  it('turns a picked day into the UTC instant of its local end of day', () => {
    const iso = localDayEndToUtcIso('2026-07-03');

    expect(iso).not.toBeNull();
    expect(iso!.endsWith('Z')).toBe(true);

    const asLocal = new Date(iso!);
    expect(asLocal.getDate()).toBe(3);
    expect(asLocal.getHours()).toBe(23);
    expect(asLocal.getMinutes()).toBe(59);
    expect(asLocal.getSeconds()).toBe(59);
    expect(asLocal.getMilliseconds()).toBe(999);
  });

  it('keeps the whole picked day inside the bounds', () => {
    const start = new Date(localDayStartToUtcIso('2026-07-03')!).getTime();
    const end = new Date(localDayEndToUtcIso('2026-07-03')!).getTime();
    const noon = new Date(2026, 6, 3, 12, 0, 0).getTime();

    expect(start).toBeLessThanOrEqual(noon);
    expect(end).toBeGreaterThanOrEqual(noon);
    expect(end - start).toBe(86_400_000 - 1);
  });

  it('extends the upper bound past millisecond resolution', () => {
    // NTFS timestamps carry 100 ns ticks: a bound truncated at .999 drops files
    // modified in the last fraction of the day.
    expect(localDayEndToUtcIso('2026-07-03')!.endsWith('.9999999Z')).toBe(true);
  });

  it('pads a year below 1000 so the date input can render the bound back', () => {
    expect(utcIsoToLocalDay(localDayStartToUtcIso('0150-07-03'))).toBe('0150-07-03');
  });

  it('reads an ISO bound back as the local day it belongs to', () => {
    expect(utcIsoToLocalDay(localDayStartToUtcIso('2026-07-03'))).toBe('2026-07-03');
    expect(utcIsoToLocalDay(localDayEndToUtcIso('2026-07-03'))).toBe('2026-07-03');
  });

  it('treats empty and malformed input as "no bound"', () => {
    expect(localDayStartToUtcIso('')).toBeNull();
    expect(localDayEndToUtcIso('')).toBeNull();
    expect(localDayStartToUtcIso('not-a-date')).toBeNull();
    expect(localDayStartToUtcIso('2026-13-40')).toBeNull();
    expect(utcIsoToLocalDay(null)).toBe('');
    expect(utcIsoToLocalDay('not-a-date')).toBe('');
  });
});
