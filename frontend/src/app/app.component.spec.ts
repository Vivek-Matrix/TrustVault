import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../environments/environment';
import { AuthSessionStore } from './auth/auth-session.store';
import { bearerTokenInterceptor } from './auth/bearer-token.interceptor';
import { AppComponent } from './app.component';

describe('AppComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [
        provideHttpClient(withInterceptors([bearerTokenInterceptor])),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('should create the app', () => {
    const fixture = TestBed.createComponent(AppComponent);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it(`should have the 'TrustVault' title`, () => {
    const fixture = TestBed.createComponent(AppComponent);
    const app = fixture.componentInstance;
    expect(app.title).toEqual('TrustVault');
  });

  it('should render title', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('TrustVault');
  });

  it('does not expose public account creation', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('Create account');
    expect(fixture.nativeElement.querySelector('button')?.textContent).toContain('Sign in');
  });

  it('requires the full password policy before submitting a password change', () => {
    TestBed.inject(AuthSessionStore).setSession({
      accessToken: 'temporary-access-token',
      tokenType: 'Bearer',
      expiresAtUtc: '2026-11-03T00:15:00Z',
      userId: 'member-1',
      email: 'member@example.test',
      roles: ['Viewer'],
      passwordChangeRequired: true,
    });
    const fixture = TestBed.createComponent(AppComponent);
    const app = fixture.componentInstance;
    app.passwordChangeForm.setValue({
      currentPassword: 'TemporaryPassword123!',
      newPassword: 'lowercasepassword123!',
      confirmPassword: 'lowercasepassword123!',
    });

    expect(app.passwordChangeForm.controls.newPassword.invalid).toBeTrue();
    app.changePassword();

    http.expectNone(`${environment.apiBaseUrl}/auth/change-password`);
  });

  it('displays the password policy returned by the API', () => {
    TestBed.inject(AuthSessionStore).setSession({
      accessToken: 'temporary-access-token',
      tokenType: 'Bearer',
      expiresAtUtc: '2026-11-03T00:15:00Z',
      userId: 'member-1',
      email: 'member@example.test',
      roles: ['Viewer'],
      passwordChangeRequired: true,
    });
    const fixture = TestBed.createComponent(AppComponent);
    const app = fixture.componentInstance;
    app.passwordChangeForm.setValue({
      currentPassword: 'TemporaryPassword123!',
      newPassword: 'NewStrongPassword123!',
      confirmPassword: 'NewStrongPassword123!',
    });

    app.changePassword();
    http.expectOne(`${environment.apiBaseUrl}/auth/change-password`).flush(
      { errors: { password: ['Passwords must contain an uppercase character.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain(
      'Passwords must contain an uppercase character.',
    );
  });

  it('shows the documents panel for a signed-in user and removes it on sign-out', () => {
    TestBed.inject(AuthSessionStore).setSession({
      accessToken: 'test-access-token',
      tokenType: 'Bearer',
      expiresAtUtc: '2026-11-03T00:15:00Z',
      userId: 'owner-1',
      email: 'owner@example.test',
      roles: ['Viewer'],
    });
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();

    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([]);
    expect(fixture.nativeElement.querySelector('app-documents-panel')).not.toBeNull();

    const signOutButton = fixture.nativeElement.querySelector(
      '.signed-in-card > .secondary-button',
    ) as HTMLButtonElement;
    signOutButton.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('app-documents-panel')).toBeNull();
    fixture.destroy();
  });
});
