import { RelativeTimePipe } from './relative-time.pipe';

describe('RelativeTimePipe', () => {
  const pipe = new RelativeTimePipe();
  const now = Date.parse('2026-06-12T12:00:00Z');

  it('renders null as an em dash', () => {
    expect(pipe.transform(null, now)).toBe('—');
  });

  it('uses minutes / hours / days buckets', () => {
    expect(pipe.transform('2026-06-12T11:30:00Z', now)).toBe('30m fa');
    expect(pipe.transform('2026-06-12T09:00:00Z', now)).toBe('3h fa');
    expect(pipe.transform('2026-06-10T12:00:00Z', now)).toBe('2g fa');
  });

  it('falls back to a date beyond a month', () => {
    expect(pipe.transform('2026-01-01T12:00:00Z', now)).toBe('01/01/26');
  });
});
