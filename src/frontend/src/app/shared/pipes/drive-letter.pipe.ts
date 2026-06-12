import { Pipe, PipeTransform } from '@angular/core';

/**
 * Renders the (mutable, sometimes-absent) current drive letter. The letter is a
 * hint, not identity — when there's no mount point we show an em dash.
 */
@Pipe({ name: 'driveLetter' })
export class DriveLetterPipe implements PipeTransform {
  transform(letter: string | null | undefined): string {
    return letter && letter.length > 0 ? letter : '—';
  }
}
