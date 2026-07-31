import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withFetch } from '@angular/common/http';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),

    // withFetch: the fetch backend is the modern default and avoids the
    // XHR shim. No interceptors yet - authentication is #42.
    provideHttpClient(withFetch())
  ]
};
