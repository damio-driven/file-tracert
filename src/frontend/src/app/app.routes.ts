import { Routes } from '@angular/router';

/** Feature routes are lazy (loadComponent), one chunk per domain. */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  {
    path: 'dashboard',
    title: 'Dashboard · FileTracert',
    loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.Dashboard),
  },
  {
    path: 'volumes',
    title: 'Volumi · FileTracert',
    loadComponent: () => import('./features/volumes/volumes').then((m) => m.Volumes),
  },
  { path: '**', redirectTo: 'dashboard' },
];
