import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'ft-catalog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<div class="ft-panel"><h1 class="ft-h1">Catalogo</h1><p>Coming soon…</p></div>',
})
export class Catalog {}
