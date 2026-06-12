// TypeScript mirrors of the FileTracert.Contracts DTOs (camelCase, as serialized
// by ASP.NET). Kept in sync by hand — the contract is small and stable.

/** Volume row for the list / dashboard, with freshness flags. */
export interface VolumeDto {
  id: number;
  volumeGuid: string;
  label: string | null;
  currentLetter: string | null;
  fileSystem: string;
  isRemovable: boolean;
  isOnline: boolean;
  lastSeenUtc: string;
  capacityBytes: number;
  freeBytes: number;
  fileCount: number;
  lastFullScanUtc: string | null;
  /** = isOnline. Figures are live. */
  dataIsLive: boolean;
  /** = !isOnline. Free/existence are a last-known snapshot. */
  isStale: boolean;
}

/** A monitored root folder with its resolved filter summary. */
export interface WatchedRootDto {
  id: number;
  relativePath: string;
  isActive: boolean;
  effectiveFilter: string;
}

/** Full volume detail: identity + monitored roots + index statistics. */
export interface VolumeDetailDto {
  id: number;
  volumeGuid: string;
  label: string | null;
  currentLetter: string | null;
  fileSystem: string;
  isRemovable: boolean;
  isOnline: boolean;
  lastSeenUtc: string;
  capacityBytes: number;
  freeBytes: number;
  lastFullScanUtc: string | null;
  dataIsLive: boolean;
  isStale: boolean;
  serialNumber: string | null;
  physicalDiskId: string | null;
  lastUsn: number | null;
  scanEngine: string;
  watchedRoots: WatchedRootDto[];
  directoryCount: number;
  fileCount: number;
  indexedBytes: number;
}

/** Dashboard aggregates. Queue fields are placeholders (0) until step 8. */
export interface DashboardStatsDto {
  totalFiles: number;
  totalBytes: number;
  volumesOnline: number;
  volumesTotal: number;
  queuedJobs: number;
  blockedJobs: number;
  runningJobs: number;
  pendingBytes: number;
}

/** Shared server-side paging envelope (used by Catalogo/Ricerca at step 7). */
export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  skip: number;
  take: number;
}
