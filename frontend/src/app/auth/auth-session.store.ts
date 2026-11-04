import { computed, Injectable, signal } from '@angular/core';

export interface AuthSession {
  accessToken: string;
  tokenType: string;
  expiresAtUtc: string;
  userId: string;
  email: string;
  roles: string[];
  passwordChangeRequired?: boolean;
  refreshToken?: string;
  refreshTokenExpiresAtUtc?: string;
}

@Injectable({ providedIn: 'root' })
export class AuthSessionStore {
  private readonly currentSession = signal<AuthSession | null>(null);

  readonly session = this.currentSession.asReadonly();
  readonly accessToken = computed(() => this.currentSession()?.accessToken ?? null);

  setSession(session: AuthSession): void {
    this.currentSession.set(session);
  }

  clear(): void {
    this.currentSession.set(null);
  }
}
