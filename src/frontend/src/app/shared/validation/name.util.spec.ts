import { describe, expect, it } from 'vitest';

import { validateLeafName } from './name.util';

describe('validateLeafName', () => {
  it('accepts an ordinary name', () => {
    expect(validateLeafName('Vacanze 2025')).toBeNull();
    expect(validateLeafName('report-final')).toBeNull();
  });

  it('rejects empty / whitespace', () => {
    expect(validateLeafName('')).not.toBeNull();
    expect(validateLeafName('   ')).not.toBeNull();
  });

  it('rejects . and ..', () => {
    expect(validateLeafName('.')).not.toBeNull();
    expect(validateLeafName('..')).not.toBeNull();
  });

  it('rejects path separators', () => {
    expect(validateLeafName('a\\b')).not.toBeNull();
    expect(validateLeafName('a/b')).not.toBeNull();
  });

  it('rejects Windows-reserved characters', () => {
    for (const bad of ['a:b', 'a*b', 'a?b', 'a"b', 'a<b', 'a>b', 'a|b']) {
      expect(validateLeafName(bad), bad).not.toBeNull();
    }
  });

  it('allows spaces and dashes inside the name', () => {
    expect(validateLeafName('New Album - 2025')).toBeNull();
  });
});
