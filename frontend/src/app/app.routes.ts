import { Routes } from '@angular/router';

import { adminGuard } from './auth/guards/admin.guard';
import { authGuard } from './auth/guards/auth.guard';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./auth/login/login.component').then((m) => m.LoginComponent) },
  {
    path: 'register',
    loadComponent: () => import('./auth/register/register.component').then((m) => m.RegisterComponent),
  },
  {
    path: '',
    pathMatch: 'full',
    canActivate: [authGuard],
    loadComponent: () => import('./home/home.component').then((m) => m.HomeComponent),
  },
  {
    path: 'admin/ping',
    canActivate: [authGuard, adminGuard],
    loadComponent: () => import('./admin/ping/ping.component').then((m) => m.PingComponent),
  },
  { path: '**', redirectTo: '' },
];
