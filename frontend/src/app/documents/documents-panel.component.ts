import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { finalize } from 'rxjs';
import {
  DocumentGrantItem,
  DocumentItem,
  DocumentService,
} from './document.service';

const maximumFileBytes = 10 * 1024 * 1024;

@Component({
  selector: 'app-documents-panel',
  imports: [ReactiveFormsModule],
  template: `
    <section class="documents-card" aria-labelledby="documents-heading">
      <div class="section-heading">
        <div>
          <p class="eyebrow">YOUR PRIVATE FILES</p>
          <h2 id="documents-heading">Documents</h2>
        </div>
        <button
          class="secondary-button refresh-button"
          type="button"
          [disabled]="loading()"
          (click)="refreshDocuments()">
          {{ loading() ? 'Refreshing...' : 'Refresh list' }}
        </button>
      </div>

      <p class="intro">
        Upload a PDF, share it with an existing user, or download a file shared with you.
        Files are limited to 10 MiB.
        Use test files only; this local storage is not for confidential documents.
      </p>

      @if (message()) {
        <p class="form-message success-message" role="status">{{ message() }}</p>
      }
      @if (error()) {
        <p class="form-message" role="alert">{{ error() }}</p>
      }

      <form [formGroup]="form" (ngSubmit)="uploadDocument()" novalidate>
        <label for="document-title">Document title</label>
        <input id="document-title" formControlName="title" maxlength="200" />
        @if (form.controls.title.touched && form.controls.title.invalid) {
          <span class="field-error">Enter a title.</span>
        }

        <label for="document-category">Category</label>
        <input id="document-category" formControlName="category" maxlength="100" />
        @if (form.controls.category.touched && form.controls.category.invalid) {
          <span class="field-error">Enter a category.</span>
        }

        <label for="document-file">PDF file</label>
        <input
          id="document-file"
          type="file"
          accept=".pdf,application/pdf"
          (change)="onFileSelected($event)" />
        @if (selectedFile(); as file) {
          <span class="field-hint">Selected: {{ file.name }} ({{ formatSize(file.size) }})</span>
        } @else {
          <span class="field-hint">Choose a PDF file up to 10 MiB.</span>
        }

        <button
          class="primary-button"
          type="submit"
          [disabled]="form.invalid || !selectedFile() || uploading()">
          {{ uploading() ? 'Uploading...' : 'Upload PDF' }}
        </button>
      </form>

      <div class="list-heading">
        <h3>Your and shared documents</h3>
        <span>{{ documents().length }} {{ documents().length === 1 ? 'file' : 'files' }}</span>
      </div>

      @if (loading() && documents().length === 0) {
        <p class="empty-state">Loading your documents...</p>
      } @else if (documents().length === 0) {
        <p class="empty-state">No documents yet. Upload a test PDF to get started.</p>
      } @else {
        <ul class="document-list">
          @for (item of documents(); track item.id) {
            <li class="document-item">
              <div class="document-details">
                <strong>{{ item.title }}</strong>
                <span>{{ item.originalFileName }} · {{ item.category }} · {{ formatSize(item.sizeBytes) }}</span>
              </div>
              <button
                class="secondary-button download-button"
                type="button"
                [disabled]="downloadingId() === item.id"
                (click)="downloadDocument(item)">
                {{ downloadingId() === item.id ? 'Downloading...' : 'Download' }}
              </button>
              @if (item.isOwner) {
                <button
                  class="secondary-button access-button"
                  type="button"
                  [attr.aria-expanded]="selectedShareDocumentId() === item.id"
                  (click)="toggleAccess(item.id)">
                  {{ selectedShareDocumentId() === item.id ? 'Close access' : 'Manage access' }}
                </button>
              } @else {
                <span class="permission-badge">{{ item.permission }} access</span>
              }
            </li>
            @if (item.isOwner && selectedShareDocumentId() === item.id) {
              <li class="access-panel">
                <h4>People with access</h4>
                @if (grantsLoading()) {
                  <p class="empty-state">Loading access list...</p>
                } @else if (grants().length === 0) {
                  <p class="empty-state">No one else has access to this document.</p>
                } @else {
                  <ul class="grant-list">
                    @for (grant of grants(); track grant.userId) {
                      <li class="grant-item">
                        <span>{{ grant.email }} · {{ grant.permission }}</span>
                        <button
                          class="secondary-button revoke-button"
                          type="button"
                          [disabled]="revokingUserId() === grant.userId"
                          (click)="revokeAccess(item.id, grant)">
                          {{ revokingUserId() === grant.userId ? 'Revoking...' : 'Revoke' }}
                        </button>
                      </li>
                    }
                  </ul>
                }

                <form class="grant-form" (submit)="grantAccess(item.id, $event)">
                  <label [for]="'share-email-' + item.id">Share with an existing account</label>
                  <input
                    [id]="'share-email-' + item.id"
                    type="email"
                    name="email"
                    autocomplete="email"
                    required />
                  <label [for]="'share-permission-' + item.id">Permission</label>
                  <select [id]="'share-permission-' + item.id" name="permission">
                    <option value="READ">Read</option>
                    <option value="EDIT">Edit</option>
                  </select>
                  <span class="field-hint">
                    Edit permission is recorded for the future editing workflow; editing is not available yet.
                  </span>
                  <button
                    class="primary-button"
                    type="submit"
                    [disabled]="grantSubmitting()">
                    {{ grantSubmitting() ? 'Saving access...' : 'Grant access' }}
                  </button>
                </form>
              </li>
            }
          }
        </ul>
      }
    </section>
  `,
  styleUrl: './documents-panel.component.scss',
})
export class DocumentsPanelComponent {
  private readonly formBuilder = inject(FormBuilder);
  private readonly documentService = inject(DocumentService);

  readonly documents = signal<DocumentItem[]>([]);
  readonly selectedFile = signal<File | null>(null);
  readonly loading = signal(false);
  readonly uploading = signal(false);
  readonly downloadingId = signal<string | null>(null);
  readonly selectedShareDocumentId = signal<string | null>(null);
  readonly grants = signal<DocumentGrantItem[]>([]);
  readonly grantsLoading = signal(false);
  readonly grantSubmitting = signal(false);
  readonly revokingUserId = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly form = this.formBuilder.nonNullable.group({
    title: ['', [Validators.required, Validators.maxLength(200)]],
    category: ['', [Validators.required, Validators.maxLength(100)]],
  });

  constructor() {
    this.refreshDocuments();
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    this.selectedFile.set(null);
    this.message.set(null);
    this.error.set(null);

    if (!file) {
      return;
    }

    if (!file.name.toLowerCase().endsWith('.pdf')) {
      this.error.set('Choose a PDF file.');
      input.value = '';
      return;
    }

    if (file.size === 0 || file.size > maximumFileBytes) {
      this.error.set('The PDF must be between 1 byte and 10 MiB.');
      input.value = '';
      return;
    }

    this.selectedFile.set(file);
  }

  refreshDocuments(): void {
    this.loading.set(true);
    this.error.set(null);
    this.documentService
      .list()
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (documents) => this.documents.set(documents),
        error: (error: unknown) => this.error.set(this.describeError(error, 'load your documents')),
      });
  }

  uploadDocument(): void {
    this.form.markAllAsTouched();
    const file = this.selectedFile();
    if (this.form.invalid || !file || this.uploading()) {
      return;
    }

    this.message.set(null);
    this.error.set(null);
    this.uploading.set(true);
    const { title, category } = this.form.getRawValue();
    this.documentService
      .upload(title.trim(), category.trim(), file)
      .pipe(finalize(() => this.uploading.set(false)))
      .subscribe({
        next: (uploaded) => {
          this.documents.update((documents) => [
            uploaded,
            ...documents.filter((document) => document.id !== uploaded.id),
          ]);
          this.form.reset();
          this.selectedFile.set(null);
          const fileInput = globalThis.document.getElementById('document-file');
          if (fileInput instanceof HTMLInputElement) {
            fileInput.value = '';
          }
          this.message.set('Your PDF was uploaded.');
        },
        error: (error: unknown) => this.error.set(this.describeError(error, 'upload this PDF')),
      });
  }

  downloadDocument(item: DocumentItem): void {
    if (this.downloadingId()) {
      return;
    }

    this.message.set(null);
    this.error.set(null);
    this.downloadingId.set(item.id);
    this.documentService
      .download(item.id)
      .pipe(finalize(() => this.downloadingId.set(null)))
      .subscribe({
        next: (file) => {
          const url = URL.createObjectURL(file);
          const link = globalThis.document.createElement('a');
          link.href = url;
          link.download = item.originalFileName;
          link.click();
          URL.revokeObjectURL(url);
          this.message.set(`Downloaded ${item.originalFileName}.`);
        },
        error: (error: unknown) => this.error.set(this.describeError(error, 'download this PDF')),
      });
  }

  toggleAccess(documentId: string): void {
    if (this.selectedShareDocumentId() === documentId) {
      this.selectedShareDocumentId.set(null);
      this.grants.set([]);
      return;
    }

    this.selectedShareDocumentId.set(documentId);
    this.loadGrants(documentId);
  }

  grantAccess(documentId: string, event: Event): void {
    event.preventDefault();
    const form = event.currentTarget;
    if (!(form instanceof HTMLFormElement) || !form.reportValidity() || this.grantSubmitting()) {
      return;
    }

    const values = new FormData(form);
    const email = values.get('email');
    const permission = values.get('permission');
    if (
      typeof email !== 'string' ||
      (permission !== 'READ' && permission !== 'EDIT')
    ) {
      this.error.set('Enter an email and choose READ or EDIT access.');
      return;
    }

    this.error.set(null);
    this.message.set(null);
    this.grantSubmitting.set(true);
    this.documentService
      .grantAccess(documentId, email.trim(), permission)
      .pipe(finalize(() => this.grantSubmitting.set(false)))
      .subscribe({
        next: () => {
          form.reset();
          this.message.set('Document access was saved.');
          this.loadGrants(documentId);
        },
        error: (error: unknown) =>
          this.error.set(this.describeError(error, 'grant document access')),
      });
  }

  revokeAccess(documentId: string, grant: DocumentGrantItem): void {
    if (
      this.revokingUserId() ||
      !globalThis.confirm(`Revoke ${grant.email}'s access to this document?`)
    ) {
      return;
    }

    this.error.set(null);
    this.message.set(null);
    this.revokingUserId.set(grant.userId);
    this.documentService
      .revokeAccess(documentId, grant.userId)
      .pipe(finalize(() => this.revokingUserId.set(null)))
      .subscribe({
        next: () => {
          this.message.set(`Access for ${grant.email} was revoked.`);
          this.loadGrants(documentId);
          this.refreshDocuments();
        },
        error: (error: unknown) =>
          this.error.set(this.describeError(error, 'revoke document access')),
      });
  }

  private loadGrants(documentId: string): void {
    this.grantsLoading.set(true);
    this.error.set(null);
    this.documentService
      .listGrants(documentId)
      .pipe(finalize(() => this.grantsLoading.set(false)))
      .subscribe({
        next: (grants) => this.grants.set(grants),
        error: (error: unknown) =>
          this.error.set(this.describeError(error, 'load document access')),
      });
  }

  formatSize(sizeBytes: number): string {
    if (sizeBytes < 1024) {
      return `${sizeBytes} B`;
    }
    if (sizeBytes < 1024 * 1024) {
      return `${(sizeBytes / 1024).toFixed(1)} KB`;
    }
    return `${(sizeBytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  private describeError(error: unknown, action: string): string {
    if (!(error instanceof HttpErrorResponse)) {
      return `Unable to ${action}. Try again.`;
    }

    if (error.status === 401) {
      return 'Your sign-in has expired. Sign in again and retry.';
    }
    if (error.status === 413) {
      return 'This file is too large. Choose a PDF up to 10 MiB.';
    }
    if (error.status === 400) {
      return 'The PDF or its details were not accepted. Check the file and try again.';
    }
    if (error.status === 404) {
      return 'This document is no longer available.';
    }
    if (error.status === 0) {
      return 'The service could not be reached. Check that the API is running.';
    }
    return `Unable to ${action}. Try again later.`;
  }
}
