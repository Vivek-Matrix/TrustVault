import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { AuthSession, AuthSessionStore } from './auth-session.store';
import { AuthService } from './auth.service';
import { bearerTokenInterceptor } from './bearer-token.interceptor';

describe('AuthService', () => {
  let service: AuthService;
  let http: HttpTestingController;
  let sessionStore: AuthSessionStore;

  const session: AuthSession = {
    accessToken: 'test-access-token',
    tokenType: 'Bearer',
    expiresAtUtc: '2026-11-02T13:15:00Z',
    userId: 'user-id',
    email: 'person@example.test',
    roles: ['Viewer'],
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([bearerTokenInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    service = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
    sessionStore = TestBed.inject(AuthSessionStore);
  });

  afterEach(() => http.verify());

  it('stores the returned login token in memory', () => {
    service.login(session.email, 'ValidPassword123!').subscribe();

    const request = http.expectOne(`${environment.apiBaseUrl}/auth/login`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      email: session.email,
      password: 'ValidPassword123!',
    });
    request.flush(session);

    expect(service.session()).toEqual(session);
    expect(sessionStore.accessToken()).toBe(session.accessToken);
  });

  it('updates the stored session after changing a temporary password', () => {
    sessionStore.setSession({ ...session, passwordChangeRequired: true });
    const updatedSession = {
      ...session,
      accessToken: 'replacement-token',
      passwordChangeRequired: false,
    };

    service.changePassword('TemporaryPassword123!', 'NewStrongPassword123!').subscribe();

    const request = http.expectOne(`${environment.apiBaseUrl}/auth/change-password`);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Authorization')).toBe('Bearer test-access-token');
    expect(request.request.body).toEqual({
      currentPassword: 'TemporaryPassword123!',
      newPassword: 'NewStrongPassword123!',
    });
    request.flush(updatedSession);

    expect(service.session()).toEqual(updatedSession);
    expect(sessionStore.accessToken()).toBe('replacement-token');
  });

  it('does not send access tokens to anonymous login or refresh endpoints', () => {
    sessionStore.setSession(session);
    service.login(session.email, 'ValidPassword123!').subscribe();

    const loginRequest = http.expectOne(`${environment.apiBaseUrl}/auth/login`);
    expect(loginRequest.request.headers.has('Authorization')).toBeFalse();
    loginRequest.flush(session);
  });

  it('adds the bearer token to protected API requests only', () => {
    sessionStore.setSession(session);

    const client = TestBed.inject(HttpClient);
    client.get(`${environment.apiBaseUrl}/system/whoami`).subscribe();

    const request = http.expectOne(`${environment.apiBaseUrl}/system/whoami`);
    expect(request.request.headers.get('Authorization')).toBe(`Bearer ${session.accessToken}`);
    request.flush({});
  });
});
