import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { QueueApi } from '../../../core/api/queue-api.service';
import { VolumesApi } from '../../../core/api/volumes-api.service';
import { CreateJobRequest, FeasibilityResult, SelectedFile } from '../../../core/models/catalog.models';
import { OperationPicker } from './operation-picker';

const files: SelectedFile[] = [
  { fileId: 1, name: 'photo.jpg', sizeBytes: 1000, volumeId: 1 },
  { fileId: 2, name: 'clip.mp4', sizeBytes: 2000, volumeId: 1 },
];

const feasibility: FeasibilityResult = {
  feasible: true, requiredBytes: 1000, reservedBytes: 0,
  availableEstimateBytes: 9000, deficitBytes: 0, estimateIsLive: true, blockingVolumeId: null,
};

function setup() {
  const enqueue = vi.fn((_req: CreateJobRequest) => of({} as never));
  const preview = vi.fn((_req: CreateJobRequest) => of(feasibility));

  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: QueueApi, useValue: { enqueue, preview } },
      { provide: VolumesApi, useValue: { list: () => of([]) } },
      { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
    ],
  });

  const fixture = TestBed.createComponent(OperationPicker);
  fixture.componentRef.setInput('files', files);
  const cmp = fixture.componentInstance as unknown as {
    targetVolumeId: number | null;
    targetFolder: string;
    enqueue(): Promise<void>;
    runPreview(): Promise<void>;
  };
  cmp.targetVolumeId = 1;
  cmp.targetFolder = 'Archive';

  return { enqueue, preview, cmp };
}

describe('OperationPicker target path', () => {
  it('enqueue sends the folder only, without appending the file name', async () => {
    const { enqueue, cmp } = setup();

    await cmp.enqueue();

    expect(enqueue).toHaveBeenCalledTimes(2);
    expect(enqueue.mock.calls[0][0].targetRelativePath).toBe('Archive');
    expect(enqueue.mock.calls[1][0].targetRelativePath).toBe('Archive');
  });

  it('preview sends the folder only, without appending the file name', async () => {
    const { preview, cmp } = setup();

    await cmp.runPreview();

    expect(preview).toHaveBeenCalledTimes(1);
    expect(preview.mock.calls[0][0].targetRelativePath).toBe('Archive');
  });
});
