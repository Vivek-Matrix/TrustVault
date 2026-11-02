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

  register(email: string, password: string): Observable<AuthSession> {
    return this.authenticate('register', email, password);
  }

  login(email: string, password: string): Observable<AuthSession> {
    return this.authenticate('login', email, password);
  }

  logout(): void {
    this.sessionStore.clear();
  }

  private authenticate(
    endpoint: 'register' | 'login',
    email: string,
    password: string,
  ): Observable<AuthSession> {
    return this.http
      .post<AuthSession>(`${this.authUrl}/${endpoint}`, { email, password })
      .pipe(tap((session) => this.sessionStore.setSession(session)));
  }
}
