import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { VolumesApi } from '../../core/api/volumes-api.service';
import { VolumeDto } from '../../core/models/catalog.models';
import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { FtPill } from '../../shared/components/ft-pill/ft-pill';
import { FilterEditor } from './filter-editor/filter-editor';
import { FolderTree } from './folder-tree/folder-tree';
import { SetupStore } from './setup.store';

/** Setup: pick folders to monitor (real-FS tree-picker) and set the file-type filter. */
@Component({
  selector: 'ft-setup',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FtPanel, FtPill, FolderTree, FilterEditor],
  templateUrl: './setup.html',
  styleUrl: './setup.scss',
})
export class Setup implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly volumesApi = inject(VolumesApi);
  protected readonly store = inject(SetupStore);

  protected readonly volume = signal<VolumeDto | null>(null);

  async ngOnInit(): Promise<void> {
    const volumes = await firstValueFrom(this.volumesApi.list());
    const requested = Number(this.route.snapshot.queryParamMap.get('volume'));
    const target =
      volumes.find((v) => v.id === requested && v.isOnline) ?? volumes.find((v) => v.isOnline) ?? null;

    this.volume.set(target);
    if (target) {
      this.store.init(target.id);
      await Promise.all([this.store.loadFolders(''), this.store.loadFilter(), this.store.loadRoots()]);
    }
  }

  protected addRoot(path: string): void {
    void this.store.addRoot(path);
  }
}
