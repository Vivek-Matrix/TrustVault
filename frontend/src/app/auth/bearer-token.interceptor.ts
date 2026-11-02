import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { environment } from '../../environments/environment';
import { AuthSessionStore } from './auth-session.store';

export const bearerTokenInterceptor: HttpInterceptorFn = (request, next) => {
  const token = inject(AuthSessionStore).accessToken();
  if (
    !token ||
    !request.url.startsWith(environment.apiBaseUrl) ||
    request.url.startsWith(`${environment.apiBaseUrl}/auth/`)
  ) {
    return next(request);
  }

  return next(
    request.clone({
      setHeaders: { Authorization: `Bearer ${token}` },
    }),
  );
};
