import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { TitleStrategy, provideRouter } from '@angular/router';

import { authInterceptor } from './auth/auth.interceptor';
import { AppTitleStrategy } from './core/app-title-strategy';
import { errorInterceptor } from './core/error.interceptor';
import { routes } from './app.routes';

// The error interceptor is outermost so it sees only what is left after the auth interceptor has tried to renew an expired token once.
export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    { provide: TitleStrategy, useExisting: AppTitleStrategy },
    provideHttpClient(withInterceptors([errorInterceptor, authInterceptor])),
  ],
};
