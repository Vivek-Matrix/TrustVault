import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterOutlet } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService } from './auth/auth.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ReactiveFormsModule],
  template: `
    <main>
      <header class="brand">
        <p class="eyebrow">PRIVATE DOCUMENT SHARING</p>
        <h1>{{ title }}</h1>
        <p>Access starts with a verified account and server-issued credentials.</p>
      </header>

      @if (auth.session(); as session) {
        <section class="auth-card" aria-labelledby="signed-in-heading">
          <div class="status-mark" aria-hidden="true">✓</div>
          <h2 id="signed-in-heading">You're signed in</h2>
          <p class="account-email">{{ session.email }}</p>
          <p class="role-list">Role: {{ session.roles.join(', ') }}</p>
          <p class="session-note">
            This session stays in memory and ends when you sign out or close this page.
          </p>
          <button class="secondary-button" type="button" (click)="logout()">Sign out</button>
        </section>
      } @else {
        <section class="auth-card" aria-labelledby="auth-heading">
          <div class="mode-switch" aria-label="Account action">
            <button
              type="button"
              [class.selected]="mode() === 'login'"
              [attr.aria-pressed]="mode() === 'login'"
              (click)="setMode('login')">
              Sign in
            </button>
            <button
              type="button"
              [class.selected]="mode() === 'register'"
              [attr.aria-pressed]="mode() === 'register'"
              (click)="setMode('register')">
              Create account
            </button>
          </div>

          <h2 id="auth-heading">{{ mode() === 'register' ? 'Create your account' : 'Welcome back' }}</h2>
          <p class="intro">
            {{ mode() === 'register'
              ? 'New accounts start with Viewer access.'
              : 'Sign in to continue to your secure workspace.' }}
          </p>

          @if (message()) {
            <p class="form-message" role="alert">{{ message() }}</p>
          }

          <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
            <label for="email">Email address</label>
            <input id="email" type="email" autocomplete="email" formControlName="email" />
            @if (form.controls.email.touched && form.controls.email.invalid) {
              <span class="field-error">Enter a valid email address.</span>
            }

            <label for="password">Password</label>
            <input
              id="password"
              type="password"
              [attr.autocomplete]="mode() === 'register' ? 'new-password' : 'current-password'"
              formControlName="password" />
            @if (mode() === 'register') {
              <span class="field-hint">Use at least 12 characters with upper/lowercase letters, a number, and a symbol.</span>
            }
            @if (form.controls.password.touched && form.controls.password.invalid) {
              <span class="field-error">Enter a password that meets the requirements.</span>
            }

            <button class="primary-button" type="submit" [disabled]="form.invalid || submitting()">
              {{ submitting()
                ? 'Please wait...'
                : mode() === 'register'
                  ? 'Create account'
                  : 'Sign in' }}
            </button>
          </form>

          <p class="security-note">
            Sign-in tokens are short-lived and are not saved in browser storage.
          </p>
        </section>
      }

      <footer>TrustVault · Secure access is enforced by the API.</footer>
      <router-outlet />
    </main>
  `,
  styleUrl: './app.component.scss',
})
export class AppComponent {
  private readonly formBuilder = inject(FormBuilder);
  readonly auth = inject(AuthService);
  readonly title = 'TrustVault';
  readonly mode = signal<'login' | 'register'>('register');
  readonly message = signal<string | null>(null);
  readonly submitting = signal(false);
  readonly form = this.formBuilder.nonNullable.group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
    password: ['', [Validators.required, Validators.maxLength(128)]],
  });

  constructor() {
    this.updatePasswordValidators();
  }

  setMode(mode: 'login' | 'register'): void {
    this.mode.set(mode);
    this.message.set(null);
    this.form.controls.password.reset();
    this.updatePasswordValidators();
  }

  submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.submitting()) {
      return;
    }

    this.message.set(null);
    this.submitting.set(true);
    const { email, password } = this.form.getRawValue();
    const operation = this.mode() === 'register'
      ? this.auth.register(email, password)
      : this.auth.login(email, password);

    operation
      .pipe(finalize(() => this.submitting.set(false)))
      .subscribe({
        next: () => this.form.controls.password.reset(),
        error: (error: unknown) => this.message.set(this.describeError(error)),
      });
  }

  logout(): void {
    this.auth.logout();
    this.form.reset();
    this.message.set(null);
  }

  private describeError(error: unknown): string {
    if (!(error instanceof HttpErrorResponse)) {
      return 'Unable to reach the service. Check your connection and try again.';
    }

    if (error.status === 401) {
      return 'Invalid email or password.';
    }
    if (error.status === 409) {
      return 'An account with this email already exists. Sign in instead.';
    }
    if (error.status === 429) {
      return 'Too many attempts. Wait a minute before trying again.';
    }
    if (error.status === 400) {
      return 'The email or password does not meet the requirements.';
    }
    return 'The request could not be completed. Try again later.';
  }

  private updatePasswordValidators(): void {
    const validators = [Validators.required, Validators.maxLength(128)];
    if (this.mode() === 'register') {
      validators.push(Validators.minLength(12));
    }
    this.form.controls.password.setValidators(validators);
    this.form.controls.password.updateValueAndValidity();
  }
}
