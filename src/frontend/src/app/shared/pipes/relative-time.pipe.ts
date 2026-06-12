import { Pipe, PipeTransform } from '@angular/core';

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/**
 * Short Italian relative time for "last seen / estimate" labels:
 * `"adesso"`, `"5m fa"`, `"3h fa"`, `"2g fa"`, then `dd/mm/yy`. `null → "—"`.
 */
@Pipe({ name: 'relativeTime' })
export class RelativeTimePipe implements PipeTransform {
  transform(value: string | Date | null | undefined, now: number = Date.now()): string {
    if (value == null) {
      return '—';
    }

    const then = value instanceof Date ? value.getTime() : Date.parse(value);
    if (Number.isNaN(then)) {
      return '—';
    }

    const elapsed = now - then;
    if (elapsed < MINUTE) {
      return 'adesso';
    }
    if (elapsed < HOUR) {
      return `${Math.floor(elapsed / MINUTE)}m fa`;
    }
    if (elapsed < DAY) {
      return `${Math.floor(elapsed / HOUR)}h fa`;
    }
    if (elapsed < 30 * DAY) {
      return `${Math.floor(elapsed / DAY)}g fa`;
    }

    return new Date(then).toLocaleDateString('it-IT', {
      day: '2-digit',
      month: '2-digit',
      year: '2-digit',
    });
  }
}
