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
    // Session selection is a query param (?session=<id>), not a path segment, so there is only
    // ever one route config here - Angular reuses the same ChatComponent instance across every
    // "select a session" navigation instead of destroying and recreating it. (Two path-based
    // routes pointing at the same component - '' and 'chat/:sessionId' - look equivalent but
    // are NOT: Angular's default route reuse strategy treats them as different configs and tears
    // the component down on every transition between them, silently orphaning any in-flight
    // subscription the old instance was holding - e.g. a chat send's response arriving after
    // navigation, updating a signal nothing renders anymore.)
    path: '',
    pathMatch: 'full',
    canActivate: [authGuard],
    loadComponent: () => import('./chat/chat.component').then((m) => m.ChatComponent),
  },
  {
    path: 'admin/usage',
    canActivate: [authGuard, adminGuard],
    loadComponent: () => import('./admin/usage/usage.component').then((m) => m.UsageComponent),
  },
  { path: '**', redirectTo: '' },
];
