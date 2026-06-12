import { BytesPipe } from './bytes.pipe';

describe('BytesPipe', () => {
  const pipe = new BytesPipe();

  it('renders null as an em dash', () => {
    expect(pipe.transform(null)).toBe('—');
  });

  it('keeps small values in bytes', () => {
    expect(pipe.transform(512)).toBe('512 B');
  });

  it('scales to the right unit with an Italian decimal comma', () => {
    expect(pipe.transform(8_200_000)).toBe('8,2 MB');
    expect(pipe.transform(1_900_000_000)).toBe('1,9 GB');
  });

  it('drops a trailing zero decimal', () => {
    expect(pipe.transform(88_000_000_000)).toBe('88 GB');
  });
});
