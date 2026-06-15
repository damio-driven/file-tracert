// TypeScript mirrors of the FileTracert.Contracts DTOs (camelCase, as serialized
// by ASP.NET). Kept in sync by hand — the contract is small and stable.

/** Volume classification: cloud/system are excluded from cataloguing by default. */
export type VolumeKind = 'Unknown' | 'Fixed' | 'Removable' | 'Cloud' | 'System';

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
  kind: VolumeKind;
  /** Eligible for cataloguing (cloud/system default false; user can override). */
  isCatalogable: boolean;
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
  kind: VolumeKind;
  isCatalogable: boolean;
  serialNumber: string | null;
  physicalDiskId: string | null;
  lastUsn: number | null;
  scanEngine: string;
  watchedRoots: WatchedRootDto[];
  directoryCount: number;
  fileCount: number;
  indexedBytes: number;
}

/** Coarse phase of an in-flight scan (mirrors the backend ScanPhase enum). */
export type ScanPhase = 'Enumerating' | 'ResolvingPaths' | 'ReadingMetadata' | 'Writing' | 'Done' | 'Failed';

/** Live progress of one volume being scanned, polled from /api/scans/status. */
export interface ScanStatusDto {
  volumeId: number;
  label: string | null;
  phase: ScanPhase;
  itemsSeen: number;
  itemsWritten: number;
  currentRoot: string | null;
  startedUtc: string;
  updatedUtc: string;
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

/** One immediate sub-folder from the real-filesystem browse endpoint. */
export interface FolderNodeDto {
  name: string;
  relativePath: string;
  hasChildren: boolean;
}

/** Per-root filter override. useDefault=true clears the override. */
export interface FilterOverrideDto {
  useDefault: boolean;
  extensions: string[];
}

export interface CreateWatchedRootRequest {
  relativePath: string;
  filterOverride: FilterOverrideDto | null;
}

export interface UpdateWatchedRootRequest {
  isActive: boolean | null;
  filterOverride: FilterOverrideDto | null;
}

/** Global default filter (AppSettings). Empty allowedExtensions = all types. */
export interface FilterSettingsDto {
  allowedExtensions: string[];
  excludedPaths: string[];
}

/** Reconcile outcome after a filter change (no rescan). */
export interface ReconcileResultDto {
  includedCount: number;
  excludedCount: number;
  needsScan: boolean;
}

export interface WatchedRootUpdateResponse {
  root: WatchedRootDto;
  reconcile: ReconcileResultDto | null;
}

// ---- Diagnostics: notifications + logs (step 6.6) ----

export type NotificationSeverity = 'Info' | 'Warning' | 'Error';

/** A background event surfaced to the user (the bell / notifications panel). */
export interface NotificationDto {
  id: number;
  timestampUtc: string;
  severity: NotificationSeverity;
  source: string;
  title: string;
  message: string;
  volumeId: number | null;
  isRead: boolean;
  isDismissed: boolean;
}

export interface NotificationCountDto {
  unread: number;
}

/** Canonical log level names (match Microsoft.Extensions.Logging.LogLevel). */
export type LogLevelName = 'Trace' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Critical';

/** One persisted log line for the Log section. */
export interface LogEntryDto {
  id: number;
  timestampUtc: string;
  level: string;
  category: string;
  message: string;
  exception: string | null;
  eventId: number | null;
  scope: string | null;
}

export interface LogLevelDto {
  level: string;
}
