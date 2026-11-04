import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthSession, AuthSessionStore } from './auth-session.store';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly sessionStore = inject(AuthSessionStore);
  private readonly authUrl = `${environment.apiBaseUrl}/auth`;

  readonly session = this.sessionStore.session;

  login(email: string, password: string): Observable<AuthSession> {
    return this.http
      .post<AuthSession>(`${this.authUrl}/login`, { email, password })
      .pipe(tap((session) => this.sessionStore.setSession(session)));
  }

  changePassword(currentPassword: string, newPassword: string): Observable<AuthSession> {
    return this.http
      .post<AuthSession>(`${this.authUrl}/change-password`, {
        currentPassword,
        newPassword,
      })
      .pipe(tap((session) => this.sessionStore.setSession(session)));
  }

  logout(): void {
    this.sessionStore.clear();
  }
}
