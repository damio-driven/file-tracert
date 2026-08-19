import { describe, expect, it } from 'vitest';

import { FileCategory } from '../core/models/catalog.models';
import {
  FILE_CATEGORIES, FILE_CATEGORY_LABELS, FILE_CATEGORY_TAGS, fileCategoryLabel, fileCategoryTag,
} from './file-category';

/**
 * K9 — Catalogo and Ricerca each had their own copy of this map and had already drifted:
 * plurals against singulars, and `Other` missing from Ricerca entirely, so files the catalogue
 * called "Altro" could not be filtered for.
 */
describe('file category vocabulary', () => {
  const all = Object.keys(FILE_CATEGORY_LABELS) as FileCategory[];

  it('covers every category, chips included', () => {
    expect(all).toHaveLength(6);
    expect([...FILE_CATEGORIES].sort()).toEqual([...all].sort());
    expect(Object.keys(FILE_CATEGORY_TAGS).sort()).toEqual([...all].sort());
  });

  it('labels are singular, because the same one names a category and tags one file', () => {
    expect(fileCategoryLabel('Image')).toBe('Immagine');
    expect(fileCategoryLabel('Document')).toBe('Documento');
    expect(fileCategoryLabel('Archive')).toBe('Archivio');
    for (const label of Object.values(FILE_CATEGORY_LABELS)) {
      expect(label.endsWith('i')).toBe(false);
    }
  });

  it('every category has a tag of its own, "Altro" included', () => {
    const tags = Object.values(FILE_CATEGORY_TAGS);
    expect(new Set(tags).size).toBe(tags.length);
    // '???' is reserved for a value the client does not know, so it must not be a real tag.
    expect(tags).not.toContain('???');
    expect(fileCategoryTag('Other')).toBe('ALT');
  });

  it('falls back visibly on a category this build has never heard of', () => {
    expect(fileCategoryTag('Hologram' as FileCategory)).toBe('???');
    expect(fileCategoryLabel('Hologram' as FileCategory)).toBe('Hologram');
  });
});
