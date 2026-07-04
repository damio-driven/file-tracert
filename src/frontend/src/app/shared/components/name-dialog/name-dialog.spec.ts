import { provideZonelessChangeDetection } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { QueueApi } from '../../../core/api/queue-api.service';
import { CreateJobRequest, SelectedItem } from '../../../core/models/catalog.models';
import { NameDialog } from './name-dialog';

interface Harness {
  name: { set(v: string): void };
  onInput(v: string): void;
  submit(): Promise<void>;
  validationError(): string | null;
  canSubmit(): boolean;
  serverError(): string | null;
}

function setup(inputs: Record<string, unknown>) {
  const enqueue = vi.fn((_req: CreateJobRequest) => of({} as never));
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: QueueApi, useValue: { enqueue } },
    ],
  });
  const fixture = TestBed.createComponent(NameDialog);
  for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
  fixture.detectChanges();
  const cmp = fixture.componentInstance as unknown as Harness;
  return { fixture, cmp, enqueue };
}

const folderItem: SelectedItem = {
  kind: 'Folder', id: 5, name: 'Vecchio', sizeBytes: 0, volumeId: 1, relativePath: 'Vecchio',
};
const fileItem: SelectedItem = {
  kind: 'File', id: 8, name: 'a.jpg', sizeBytes: 10, volumeId: 1, relativePath: 'a.jpg',
};

describe('NameDialog — validation', () => {
  it('create: blocks submit and skips the API on an invalid name', async () => {
    const { cmp, enqueue } = setup({ mode: 'create', subjectKind: 'Folder', volumeId: 1, parentPath: 'Docs' });
    cmp.onInput('bad/name');

    expect(cmp.validationError()).not.toBeNull();
    expect(cmp.canSubmit()).toBe(false);

    await cmp.submit();
    expect(enqueue).not.toHaveBeenCalled();
  });

  it('rename: prefills the current name and validates it', () => {
    const { cmp } = setup({ mode: 'rename', item: folderItem });
    expect(cmp.validationError()).toBeNull();
    expect(cmp.canSubmit()).toBe(true);
  });
});

describe('NameDialog — enqueue', () => {
  it('create: enqueues CreateFolder with parent + name joined', async () => {
    const { cmp, enqueue, fixture } = setup({ mode: 'create', subjectKind: 'Folder', volumeId: 3, parentPath: 'Docs' });
    const completed = vi.fn();
    fixture.componentInstance.completed.subscribe(completed);

    cmp.onInput('Nuova');
    await cmp.submit();

    expect(enqueue).toHaveBeenCalledTimes(1);
    const req = enqueue.mock.calls[0][0];
    expect(req.type).toBe('CreateFolder');
    expect(req.targetVolumeId).toBe(3);
    expect(req.targetRelativePath).toBe('Docs\\Nuova');
    expect(completed).toHaveBeenCalledTimes(1);
  });

  it('create at root: no parent means the name is the whole path', async () => {
    const { cmp, enqueue } = setup({ mode: 'create', subjectKind: 'Folder', volumeId: 1, parentPath: '' });
    cmp.onInput('Root Folder');
    await cmp.submit();
    expect(enqueue.mock.calls[0][0].targetRelativePath).toBe('Root Folder');
  });

  it('rename folder: enqueues RenameFolder with sourceDirectoryId', async () => {
    const { cmp, enqueue } = setup({ mode: 'rename', item: folderItem });
    cmp.onInput('Nuovo');
    await cmp.submit();

    const req = enqueue.mock.calls[0][0];
    expect(req.type).toBe('RenameFolder');
    expect(req.sourceDirectoryId).toBe(5);
    expect(req.sourceFileId).toBeNull();
    expect(req.newName).toBe('Nuovo');
  });

  it('rename file: enqueues RenameFile with sourceFileId', async () => {
    const { cmp, enqueue } = setup({ mode: 'rename', item: fileItem });
    cmp.onInput('b.jpg');
    await cmp.submit();

    const req = enqueue.mock.calls[0][0];
    expect(req.type).toBe('RenameFile');
    expect(req.sourceFileId).toBe(8);
    expect(req.newName).toBe('b.jpg');
  });
});

describe('NameDialog — backend rejection', () => {
  it('shows the guard message inline and does not emit completed', async () => {
    const enqueue = vi.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 409, error: { entityType: 'Directory' } })));
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: QueueApi, useValue: { enqueue } }],
    });
    const fixture = TestBed.createComponent(NameDialog);
    fixture.componentRef.setInput('mode', 'rename');
    fixture.componentRef.setInput('item', folderItem);
    fixture.detectChanges();
    const cmp = fixture.componentInstance as unknown as Harness;
    const completed = vi.fn();
    fixture.componentInstance.completed.subscribe(completed);

    cmp.onInput('Nuovo');
    await cmp.submit();

    expect(cmp.serverError()).toContain('cartella');
    expect(completed).not.toHaveBeenCalled();
  });
});
