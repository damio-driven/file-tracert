// TypeScript mirrors of FileTracert.Contracts/Realtime (§7). Thin payloads: an id plus
// the fields that actually changed; anything else is re-read with the GET the screen
// already has. Enums travel as their names (the hub uses a string-enum JSON protocol),
// so the union types are the SAME ones the REST DTOs use — never redeclared here.

import {
  JobBlockReason, JobState, NotificationSeverity, ScanStatusDto,
} from '../models/catalog.models';

export interface VolumeStatusChanged {
  volumeId: number;
  isOnline: boolean;
  freeBytesLastKnown: number;
  lastSeenUtc: string;
}

export interface JobProgress {
  jobId: number;
  bytesProcessed: number;
  totalBytes: number;
}

export interface JobStateChanged {
  jobId: number;
  state: JobState;
  blockReason: JobBlockReason;
  errorMessage: string | null;
}

/** `volumeId` is null when the change is not confined to one volume (cross-volume move). */
export interface ProjectionChanged {
  volumeId: number | null;
  jobId: number | null;
}

export interface NotificationRaised {
  id: number;
  severity: NotificationSeverity;
  title: string;
  timestampUtc: string;
}

/** Method name → payload. The names match `RealtimeMethods` on the server, one for one. */
export interface RealtimeMessageMap {
  VolumeStatusChanged: VolumeStatusChanged;
  JobProgress: JobProgress;
  JobStateChanged: JobStateChanged;
  ScanProgress: ScanStatusDto;
  ProjectionChanged: ProjectionChanged;
  NotificationRaised: NotificationRaised;
}

export type RealtimeMethod = keyof RealtimeMessageMap;

/**
 * Connection state as the UI needs it. `connecting` is the very first attempt and stays
 * silent on screen: otherwise every cold start would flash a warning at the user.
 */
export type RealtimeStatus = 'connecting' | 'connected' | 'reconnecting' | 'offline';
