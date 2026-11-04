import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { environment } from '../../environments/environment';
import { AuthSessionStore } from './auth-session.store';

export const bearerTokenInterceptor: HttpInterceptorFn = (request, next) => {
  const token = inject(AuthSessionStore).accessToken();
  const anonymousAuthPaths = [
    `${environment.apiBaseUrl}/auth/login`,
    `${environment.apiBaseUrl}/auth/refresh`,
  ];
  if (
    !token ||
    !request.url.startsWith(environment.apiBaseUrl) ||
    anonymousAuthPaths.includes(request.url.split('?')[0])
  ) {
    return next(request);
  }

  return next(
    request.clone({
      setHeaders: { Authorization: `Bearer ${token}` },
    }),
  );
};
