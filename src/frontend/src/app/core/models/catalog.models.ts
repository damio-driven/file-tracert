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

// ---- Step 7: Search + Catalog ----

export type SearchScope = 'Name' | 'FullPath';
export type SearchSort = 'Relevance' | 'Name' | 'Date' | 'Size';
export type FileCategory = 'Image' | 'Video' | 'Audio' | 'Document' | 'Archive' | 'Other';

export interface SearchRequest {
  text: string;
  scope: SearchScope;
  category: FileCategory | null;
  extensions: string[] | null;
  sizeBytesMin: number | null;
  sizeBytesMax: number | null;
  modifiedFrom: string | null;
  modifiedTo: string | null;
  volumeId: number | null;
  onlineOnly: boolean;
  sort: SearchSort;
  desc: boolean;
  skip: number;
  take: number;
}

/**
 * What the queue is about to do to a catalog row (§5 projection model). 'None' means the
 * row is exactly what the last scan found on disk.
 */
export type EntityPendingState = 'None' | 'PendingCreate' | 'PendingRename' | 'PendingMove';

/**
 * `name`, `relativePath` and `volumeId` are PROJECTED: a queued rename/move is reflected
 * here immediately, while the file still physically sits where it was.
 */
export interface SearchResultDto {
  fileId: number;
  name: string;
  relativePath: string;
  volumeId: number;
  volumeLabel: string | null;
  volumeLetter: string | null;
  volumeIsOnline: boolean;
  sizeBytes: number;
  modifiedUtc: string;
  category: FileCategory;
  projectedState: EntityPendingState;
  /** The queued job behind `projectedState`, for the link to the Coda. */
  pendingJobId: number | null;
}

/** `name` is projected; `materializedPath` stays the physical path (the row's identity). */
export interface CatalogDirDto {
  id: number;
  name: string;
  materializedPath: string;
  childDirectoryCount: number;
  fileCount: number;
  projectedState: EntityPendingState;
  pendingJobId: number | null;
}

/** `name` is projected: a queued rename shows the new name at once. */
export interface CatalogFileDto {
  id: number;
  name: string;
  sizeBytes: number;
  modifiedUtc: string;
  category: FileCategory;
  projectedState: EntityPendingState;
  pendingJobId: number | null;
}

export interface CatalogChildrenDto {
  directories: CatalogDirDto[];
  files: PagedResult<CatalogFileDto>;
  volumeIsOnline: boolean;
  volumeLabel: string | null;
  volumeLetter: string | null;
  currentDirectoryId: number | null;
  currentDirectoryPath: string | null;
}

// ---- Step 8: Queue / Operations ----

export type JobState =
  | 'Pending' | 'SpaceReserved' | 'Copying' | 'Verifying' | 'DeletingSource'
  | 'Completed' | 'Blocked' | 'Failed' | 'Cancelled';

export type JobType =
  | 'CreateFolder' | 'RenameFile' | 'RenameFolder' | 'MoveFile' | 'MoveFolder';

export type JobBlockReason =
  | 'None' | 'InsufficientSpace' | 'TargetVolumeOffline' | 'SourceVolumeOffline'
  | 'NameCollision' | 'DependencyPending' | 'DependencyCancelled';

export interface FeasibilityResult {
  feasible: boolean;
  requiredBytes: number;
  reservedBytes: number;
  availableEstimateBytes: number;
  deficitBytes: number;
  estimateIsLive: boolean;
  blockingVolumeId: number | null;
}

export interface OperationJobDto {
  id: number;
  type: JobType;
  state: JobState;
  blockReason: JobBlockReason;
  sourceVolumeId: number | null;
  sourceVolumeLabel: string | null;
  targetVolumeId: number | null;
  targetVolumeLabel: string | null;
  sourcePath: string | null;
  targetPath: string | null;
  isIntraVolume: boolean;
  totalBytes: number;
  bytesProcessed: number;
  requiredBytesTarget: number;
  freedBytesSource: number;
  estimateIsLive: boolean;
  sequenceOrder: number;
  errorMessage: string | null;
  createdUtc: string;
  startedUtc: string | null;
  completedUtc: string | null;
  feasibility: FeasibilityResult | null;
}

export interface CreateJobRequest {
  type: JobType;
  sourceFileId: number | null;
  sourceDirectoryId: number | null;
  targetVolumeId: number | null;
  targetRelativePath: string | null;
  newName: string | null;
}

/** A file or a folder — the unit of catalog selection. */
export type SelectedItemKind = 'File' | 'Folder';

/**
 * An item selected in the Catalog (files or folders) or Search (files only),
 * passed to the OperationPicker. Kept as a full object, not a bare id, so the
 * selection survives paging/navigation with everything the picker needs.
 * `id` is the FileId or the DirectoryId depending on `kind` — the two can
 * collide (separate tables), so selection is always keyed by kind+id.
 * `sizeBytes` is the file size; folders carry 0 (their subtree weight is
 * computed server-side at preview time).
 */
export interface SelectedItem {
  kind: SelectedItemKind;
  id: number;
  name: string;
  sizeBytes: number;
  volumeId: number;
  /** Volume-relative path (folder = its own path; file = containing path), for display. */
  relativePath: string;
}
