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
  {
    path: 'setup',
    title: 'Setup · FileTracert',
    loadComponent: () => import('./features/setup/setup').then((m) => m.Setup),
  },
  {
    path: 'logs',
    title: 'Log · FileTracert',
    loadComponent: () => import('./features/logs/logs').then((m) => m.Logs),
  },
  {
    path: 'catalog',
    title: 'Catalogo · FileTracert',
    loadComponent: () => import('./features/catalog/catalog').then((m) => m.Catalog),
  },
  {
    path: 'search',
    title: 'Ricerca · FileTracert',
    loadComponent: () => import('./features/search/search').then((m) => m.Search),
  },
  { path: '**', redirectTo: 'dashboard' },
];
