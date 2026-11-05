import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { finalize } from 'rxjs';
import {
  AdminMember,
  AdminMembersService,
  CreatedMember,
  TemporaryCredentials,
} from './admin-members.service';

@Component({
  selector: 'app-admin-members',
  imports: [ReactiveFormsModule],
  template: `
    <section class="admin-members" aria-labelledby="members-heading">
      <h3 id="members-heading">Manage members</h3>
      <p>Add a member account. New accounts receive the standard Viewer role automatically. Document ownership comes from uploading a document, and access to other documents is granted by their owners.</p>

      @if (message()) {
        <p class="form-message" role="alert">{{ message() }}</p>
      }

      @if (createdMember(); as created) {
        <div class="credential-notice" role="status">
          <strong>Temporary password for {{ created.email }}</strong>
          <p>Copy this now and deliver it securely out-of-band. It is shown only once.</p>
          <code>{{ created.temporaryPassword }}</code>
          <p>The member must change this password before accessing TrustVault.</p>
          <button class="secondary-button" type="button" (click)="dismissCredential()">
            Dismiss
          </button>
        </div>
      }

      <form [formGroup]="form" (ngSubmit)="create()" novalidate>
        <label for="member-email">Member email</label>
        <input id="member-email" type="email" autocomplete="off" formControlName="email" />
        <button class="primary-button" type="submit" [disabled]="form.invalid || creating()">
          {{ creating() ? 'Creating...' : 'Add member' }}
        </button>
      </form>

      <h4>Accounts</h4>
      @if (loading()) {
        <p>Loading accounts...</p>
      } @else if (members().length === 0) {
        <p>No members have been added.</p>
      } @else {
        <ul class="member-list">
          @for (member of members(); track member.userId) {
            <li>
              <span>
                <strong>{{ member.email }}</strong>
                <span>{{ member.roles.join(', ') }}</span>
                <span>Account ID: {{ member.userId }}</span>
                @if (member.isSuspended) {
                  <span class="member-status">Removed</span>
                }
              </span>
              @if (!member.isSuspended) {
                <button class="secondary-button" type="button"
                  [disabled]="removingUserId() === member.userId"
                  (click)="resetCredentials(member)">
                  Reset credentials
                </button>
                <button class="secondary-button" type="button"
                  [disabled]="removingUserId() === member.userId"
                  (click)="remove(member)">
                  {{ removingUserId() === member.userId ? 'Removing...' : 'Remove' }}
                </button>
              }
            </li>
          }
        </ul>
      }
    </section>
  `,
  styleUrl: './admin-members.component.scss',
})
export class AdminMembersComponent {
  private readonly formBuilder = inject(FormBuilder);
  private readonly service = inject(AdminMembersService);

  readonly members = signal<AdminMember[]>([]);
  readonly createdMember = signal<CreatedMember | TemporaryCredentials | null>(null);
  readonly message = signal<string | null>(null);
  readonly loading = signal(false);
  readonly creating = signal(false);
  readonly removingUserId = signal<string | null>(null);
  readonly form = this.formBuilder.nonNullable.group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
  });

  constructor() {
    this.loadMembers();
  }

  create(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.creating()) {
      return;
    }

    this.message.set(null);
    this.createdMember.set(null);
    this.creating.set(true);
    const { email } = this.form.getRawValue();
    this.service.create(email)
      .pipe(finalize(() => this.creating.set(false)))
      .subscribe({
        next: (created) => {
          this.form.reset({ email: '' });
          this.createdMember.set(created);
          this.loadMembers();
        },
        error: (error: unknown) => this.message.set(this.describeError(error)),
      });
  }

  remove(member: AdminMember): void {
    if (!window.confirm(
      `Remove ${member.email}? Their access and active document grants will be revoked. Their documents and audit history will be retained.`,
    )) {
      return;
    }

    this.message.set(null);
    this.removingUserId.set(member.userId);
    this.service.remove(member.userId)
      .pipe(finalize(() => this.removingUserId.set(null)))
      .subscribe({
        next: () => this.loadMembers(),
        error: (error: unknown) => this.message.set(this.describeError(error)),
      });
  }

  resetCredentials(member: AdminMember): void {
    const reason = window.prompt(`Why are you resetting credentials for ${member.email}?`);
    if (reason === null) {
      return;
    }
    if (reason.trim().length < 8) {
      this.message.set('Enter a reason of at least 8 characters.');
      return;
    }

    this.message.set(null);
    this.createdMember.set(null);
    this.removingUserId.set(member.userId);
    this.service.resetCredentials(member.userId, reason.trim())
      .pipe(finalize(() => this.removingUserId.set(null)))
      .subscribe({
        next: (created) => this.createdMember.set(created),
        error: (error: unknown) => this.message.set(this.describeError(error)),
      });
  }

  dismissCredential(): void {
    this.createdMember.set(null);
  }

  private loadMembers(): void {
    this.loading.set(true);
    this.service.list()
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (members) => this.members.set(members),
        error: (error: unknown) => this.message.set(this.describeError(error)),
      });
  }

  private describeError(error: unknown): string {
    if (!(error instanceof HttpErrorResponse)) {
      return 'Unable to reach the service. Check your connection and try again.';
    }

    if (error.status === 409) {
      return 'An account with this email already exists.';
    }
    if (error.status === 403) {
      return 'Only the administrator can manage member accounts.';
    }
    if (error.status === 429) {
      return 'Too many requests. Wait a minute before trying again.';
    }
    if (error.status === 400) {
      return 'Check the email address and try again.';
    }
    return 'The request could not be completed. Try again later.';
  }
}
