import { Pipe, PipeTransform } from '@angular/core';

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
const formatter = new Intl.NumberFormat('it-IT', { maximumFractionDigits: 1 });

/**
 * Human byte sizes, decimal (1000) base, Italian decimal comma:
 * `8200000 → "8,2 MB"`, `null → "—"`. One decimal, trailing zero stripped.
 */
@Pipe({ name: 'bytes' })
export class BytesPipe implements PipeTransform {
  transform(bytes: number | null | undefined): string {
    if (bytes == null) {
      return '—';
    }
    if (bytes < 1000) {
      return `${bytes} B`;
    }

    let value = bytes;
    let unit = 0;
    while (value >= 1000 && unit < UNITS.length - 1) {
      value /= 1000;
      unit++;
    }
    return `${formatter.format(value)} ${UNITS[unit]}`;
  }
}
