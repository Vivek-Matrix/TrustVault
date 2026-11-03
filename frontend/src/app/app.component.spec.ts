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
