import { VolumeDto } from '../core/models/catalog.models';
import { BarColor } from './components/ft-bar/ft-bar';

/** Percentage of capacity in use (0–100). 0 when capacity is unknown. */
export function volumeUsedPercent(v: { capacityBytes: number; freeBytes: number }): number {
  if (v.capacityBytes <= 0) {
    return 0;
  }
  const used = v.capacityBytes - v.freeBytes;
  return Math.max(0, Math.min(100, (used / v.capacityBytes) * 100));
}

/**
 * Space-bar color by headroom: nearly full → amber (warning), barely used →
 * lime (plenty), otherwise teal (normal). Matches the mockup's encoding.
 */
export function volumeBarColor(v: VolumeDto): BarColor {
  const used = volumeUsedPercent(v);
  if (used >= 90) {
    return 'amber';
  }
  if (used <= 25) {
    return 'lime';
  }
  return 'teal';
}
