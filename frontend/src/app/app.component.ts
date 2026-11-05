import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterOutlet } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService } from './auth/auth.service';
import { DocumentsPanelComponent } from './documents/documents-panel.component';
import { AdminMembersComponent } from './admin/admin-members.component';
import { AdminConsoleComponent } from './admin/admin-console.component';

@Component({
  selector: 'app-root',
  imports: [
    AdminConsoleComponent,
    AdminMembersComponent,
    DocumentsPanelComponent,
    RouterOutlet,
    ReactiveFormsModule,
  ],
  template: `
    <main>
      <header class="brand">
        <p class="eyebrow">PRIVATE DOCUMENT SHARING</p>
        <h1>{{ title }}</h1>
        <p>Accounts are managed by the TrustVault administrator.</p>
      </header>

      @if (auth.session(); as session) {
        <section class="auth-card signed-in-card" aria-labelledby="signed-in-heading">
          <div class="status-mark" aria-hidden="true">✓</div>
          <h2 id="signed-in-heading">You're signed in</h2>
          <p class="account-email">{{ session.email }}</p>
          <p class="role-list">Role: {{ session.roles.join(', ') }}</p>
          <p class="session-note">
            This session stays in memory and ends when you sign out or close this page.
          </p>
          @if (session.passwordChangeRequired) {
            <section aria-labelledby="change-password-heading">
              <h3 id="change-password-heading">Set your password</h3>
              <p>Your administrator-issued temporary password must be changed before continuing.</p>
              <p class="security-note">
                Choose at least 12 characters with uppercase and lowercase letters, a number,
                and a symbol.
              </p>
              @if (passwordChangeMessage()) {
                <p class="form-message" role="alert">{{ passwordChangeMessage() }}</p>
              }
              <form [formGroup]="passwordChangeForm" (ngSubmit)="changePassword()" novalidate>
                <label for="current-password">Temporary password</label>
                <input id="current-password" type="password" autocomplete="current-password"
                  formControlName="currentPassword" />
                <label for="new-password">New password</label>
                <input id="new-password" type="password" autocomplete="new-password"
                  formControlName="newPassword" />
                @if (passwordChangeForm.controls.newPassword.touched
                  && passwordChangeForm.controls.newPassword.invalid) {
                  <span class="field-error">
                    The new password must meet all of the requirements above.
                  </span>
                }
                <label for="confirm-password">Confirm new password</label>
                <input id="confirm-password" type="password" autocomplete="new-password"
                  formControlName="confirmPassword" />
                <button class="primary-button" type="submit"
                  [disabled]="passwordChangeForm.invalid || submittingPasswordChange()">
                  {{ submittingPasswordChange() ? 'Please wait...' : 'Change password' }}
                </button>
              </form>
            </section>
          } @else if (session.roles.includes('Administrator')) {
            <app-admin-console />
            <app-admin-members />
          } @else {
            <app-documents-panel />
          }
          <button class="secondary-button" type="button" (click)="logout()">Sign out</button>
        </section>
      } @else {
        <section class="auth-card" aria-labelledby="auth-heading">
          <h2 id="auth-heading">Welcome back</h2>
          <p class="intro">
            Sign in with credentials provided by your administrator.
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
              autocomplete="current-password"
              formControlName="password" />
            @if (form.controls.password.touched && form.controls.password.invalid) {
              <span class="field-error">Enter your password.</span>
            }

            <button class="primary-button" type="submit" [disabled]="form.invalid || submitting()">
              {{ submitting() ? 'Please wait...' : 'Sign in' }}
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
  readonly message = signal<string | null>(null);
  readonly submitting = signal(false);
  readonly passwordChangeMessage = signal<string | null>(null);
  readonly submittingPasswordChange = signal(false);
  readonly form = this.formBuilder.nonNullable.group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
    password: ['', [Validators.required, Validators.maxLength(128)]],
  });
  readonly passwordChangeForm = this.formBuilder.nonNullable.group({
    currentPassword: ['', [Validators.required, Validators.maxLength(128)]],
    newPassword: [
      '',
      [
        Validators.required,
        Validators.minLength(12),
        Validators.maxLength(128),
        Validators.pattern(/^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^a-zA-Z0-9]).+$/),
      ],
    ],
    confirmPassword: ['', [Validators.required, Validators.minLength(12), Validators.maxLength(128)]],
  });

  submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.submitting()) {
      return;
    }

    this.message.set(null);
    this.submitting.set(true);
    const { email, password } = this.form.getRawValue();
    this.auth.login(email, password)
      .pipe(finalize(() => this.submitting.set(false)))
      .subscribe({
        next: () => this.form.controls.password.reset(),
        error: (error: unknown) => this.message.set(this.describeError(error)),
      });
  }

  changePassword(): void {
    this.passwordChangeForm.markAllAsTouched();
    if (this.passwordChangeForm.invalid || this.submittingPasswordChange()) {
      return;
    }

    const { currentPassword, newPassword, confirmPassword } =
      this.passwordChangeForm.getRawValue();
    if (newPassword !== confirmPassword) {
      this.passwordChangeMessage.set('The new passwords do not match.');
      return;
    }

    this.passwordChangeMessage.set(null);
    this.submittingPasswordChange.set(true);
    this.auth.changePassword(currentPassword, newPassword)
      .pipe(finalize(() => this.submittingPasswordChange.set(false)))
      .subscribe({
        next: () => this.passwordChangeForm.reset(),
        error: (error: unknown) =>
          this.passwordChangeMessage.set(this.describePasswordChangeError(error)),
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
    if (error.status === 429) {
      return 'Too many attempts. Wait a minute before trying again.';
    }
    if (error.status === 400) {
      return 'The password could not be changed. Verify the temporary password and new password requirements.';
    }
    return 'The request could not be completed. Try again later.';
  }

  private describePasswordChangeError(error: unknown): string {
    if (!(error instanceof HttpErrorResponse)) {
      return 'Unable to reach the service. Check your connection and try again.';
    }

    if (error.status === 401) {
      return 'Your temporary password was not accepted, or your session expired. Sign out and sign in again.';
    }

    if (error.status === 400) {
      const body: unknown = error.error;
      if (typeof body === 'object' && body !== null && 'errors' in body) {
        const errors = body.errors;
        if (typeof errors === 'object' && errors !== null) {
          const messages = Object.values(errors)
            .flatMap((value) => Array.isArray(value) ? value : [])
            .filter((value): value is string => typeof value === 'string');
          if (messages.length > 0) {
            return messages.join(' ');
          }
        }
      }

      return 'The new password was rejected. Check the password requirements and try again.';
    }

    if (error.status === 429) {
      return 'Too many attempts. Wait a minute before trying again.';
    }
    return 'The password could not be changed. Try again later.';
  }

}
