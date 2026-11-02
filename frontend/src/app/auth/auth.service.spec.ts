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

  it('stores the returned registration token in memory', () => {
    service.register(session.email, 'ValidPassword123!').subscribe();

    const request = http.expectOne(`${environment.apiBaseUrl}/auth/register`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      email: session.email,
      password: 'ValidPassword123!',
    });
    request.flush(session);

    expect(service.session()).toEqual(session);
    expect(sessionStore.accessToken()).toBe(session.accessToken);
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
