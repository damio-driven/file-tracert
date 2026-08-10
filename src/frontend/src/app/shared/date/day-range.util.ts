/**
 * Bridges the `<input type="date">` value (`yyyy-MM-dd`, no timezone at all) and the
 * UTC instants the API expects.
 *
 * The user picks a day on their own calendar, so a picked day means the local day:
 * "modificato dal 3/7" starts at local midnight, "fino al 3/7" ends at the last
 * millisecond of that local day, so the bound covers the whole day the user named.
 * The backend rejects a bound without a timezone designator, and comparing a UTC
 * column against a naive local string is exactly the bug that made the filter lie
 * (review findings #11 / C31): keep the conversion in one place.
 */

const DAY_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

/** Local midnight of the picked day, as an ISO UTC instant. `null` when nothing is picked. */
export function localDayStartToUtcIso(day: string): string | null {
  return toUtcIso(day, 0, 0, 0, 0);
}

/**
 * Last instant of the picked local day, as an ISO UTC instant.
 *
 * The bound is compared inclusively against timestamps that carry 100 ns ticks on NTFS,
 * so stopping at .999 would drop a file modified at 23:59:59.9997. JavaScript dates have
 * no sub-millisecond resolution, but `toISOString()` always emits exactly three fractional
 * digits, so widening the tail to the last tick is a safe suffix swap.
 */
export function localDayEndToUtcIso(day: string): string | null {
  const endOfDay = toUtcIso(day, 23, 59, 59, 999);
  return endOfDay === null ? null : endOfDay.replace('.999Z', '.9999999Z');
}

/** Inverse mapping, so a bound already in the store can be shown back in the input. */
export function utcIsoToLocalDay(iso: string | null | undefined): string {
  if (!iso) {
    return '';
  }

  const instant = new Date(iso);
  if (Number.isNaN(instant.getTime())) {
    return '';
  }

  // The year is padded too: `<input type="date">` rejects "150-07-03" as an invalid value
  // and renders an empty field, which would hide a bound that is still being applied.
  const year = `${instant.getFullYear()}`.padStart(4, '0');
  const month = `${instant.getMonth() + 1}`.padStart(2, '0');
  const day = `${instant.getDate()}`.padStart(2, '0');
  return `${year}-${month}-${day}`;
}

function toUtcIso(
  day: string,
  hours: number,
  minutes: number,
  seconds: number,
  ms: number,
): string | null {
  const match = DAY_PATTERN.exec(day.trim());
  if (!match) {
    return null;
  }

  const [year, month, dayOfMonth] = [+match[1], +match[2], +match[3]];
  const instant = new Date(year, month - 1, dayOfMonth, hours, minutes, seconds, ms);

  // Date rolls impossible values over (month 13 becomes January of the next year);
  // reject them instead of filtering by a day the user never picked.
  const rolled =
    instant.getFullYear() !== year ||
    instant.getMonth() !== month - 1 ||
    instant.getDate() !== dayOfMonth;

  return rolled ? null : instant.toISOString();
}
