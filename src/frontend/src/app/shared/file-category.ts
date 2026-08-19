import { FileCategory } from '../core/models/catalog.models';

/**
 * One category vocabulary for the whole client (K9).
 *
 * Catalogo and Ricerca each carried their own copy and had already drifted: Ricerca used
 * plurals, Catalogo singulars, and `Other` was missing from Ricerca entirely — so a file the
 * catalogue labelled "Altro" could not be filtered for at all.
 *
 * The labels are SINGULAR everywhere, and that is the deciding argument rather than a taste:
 * the same label is printed on the tag of ONE file in both result lists, where a plural would
 * simply be wrong; on a filter chip a singular reads as the name of the category, which is
 * what it is. Both maps are `Record`s keyed by the union, so a new `FileCategory` stops the
 * build until it has a label and a tag.
 */
export const FILE_CATEGORY_LABELS: Record<FileCategory, string> = {
  Image: 'Immagine',
  Video: 'Video',
  Audio: 'Audio',
  Document: 'Documento',
  Archive: 'Archivio',
  Other: 'Altro',
};

/** Three-letter mono tag shown on a file row. Deliberately not an icon font: this app spells things out. */
export const FILE_CATEGORY_TAGS: Record<FileCategory, string> = {
  Image: 'IMG',
  Video: 'VID',
  Audio: 'AUD',
  Document: 'DOC',
  Archive: 'ARC',
  Other: 'ALT',
};

/** Display order for the filter chips. */
export const FILE_CATEGORIES: readonly FileCategory[] = [
  'Image', 'Video', 'Audio', 'Document', 'Archive', 'Other',
];

export function fileCategoryLabel(category: FileCategory): string {
  return FILE_CATEGORY_LABELS[category] ?? category;
}

export function fileCategoryTag(category: FileCategory): string {
  return FILE_CATEGORY_TAGS[category] ?? '???';
}
