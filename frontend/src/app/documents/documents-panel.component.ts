import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { finalize } from 'rxjs';
import {
  DocumentAuditItem,
  DocumentGrantItem,
  DocumentItem,
  DocumentService,
} from './document.service';
import { DocumentEditorComponent } from './document-editor.component';
import { DocumentTextEditorComponent } from './document-text-editor.component';

const maximumFileBytes = 10 * 1024 * 1024;

@Component({
  selector: 'app-documents-panel',
  imports: [ReactiveFormsModule, DocumentEditorComponent, DocumentTextEditorComponent],
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
        Upload PDF, TXT, DOC, or DOCX files, share them with an existing user, or download files
        shared with you. Files are limited to 10 MiB.
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

        <label for="document-file">Document file</label>
        <input
          id="document-file"
          type="file"
          accept=".pdf,.txt,.doc,.docx"
          (change)="onFileSelected($event)" />
        @if (selectedFile(); as file) {
          <span class="field-hint">Selected: {{ file.name }} ({{ formatSize(file.size) }})</span>
        } @else {
          <span class="field-hint">Choose a PDF, TXT, DOC, or DOCX file up to 10 MiB.</span>
        }

        <button
          class="primary-button"
          type="submit"
          [disabled]="form.invalid || !selectedFile() || uploading()">
          {{ uploading() ? 'Uploading...' : 'Upload file' }}
        </button>
      </form>

      <form class="filter-form" [formGroup]="filtersForm" (ngSubmit)="applyFilters()" novalidate>
        <div class="filter-grid">
          <div>
            <label for="document-search">Search</label>
            <input id="document-search" formControlName="search" placeholder="Title, category, owner, filename" />
          </div>
          <div>
            <label for="document-filter-category">Category</label>
            <input id="document-filter-category" formControlName="category" placeholder="Category" />
          </div>
          <div>
            <label for="document-filter-owner">Owner</label>
            <input id="document-filter-owner" formControlName="owner" placeholder="Owner" />
          </div>
          <div>
            <label for="document-filter-created-from">Created from</label>
            <input id="document-filter-created-from" type="date" formControlName="createdFrom" />
          </div>
          <div>
            <label for="document-filter-created-to">Created to</label>
            <input id="document-filter-created-to" type="date" formControlName="createdTo" />
          </div>
        </div>
        <div class="filter-actions">
          <button class="primary-button" type="submit">Apply filters</button>
          <button class="secondary-button" type="button" (click)="clearFilters()">Clear</button>
        </div>
      </form>

      <div class="list-heading">
        <h3>Your and shared documents</h3>
        <span>{{ documents().length }} {{ documents().length === 1 ? 'file' : 'files' }}</span>
      </div>

      @if (loading() && documents().length === 0) {
        <p class="empty-state">Loading your documents...</p>
      } @else if (documents().length === 0) {
        <p class="empty-state">No documents yet. Upload a test file to get started.</p>
      } @else {
        <ul class="document-list">
          @for (item of documents(); track item.id) {
            <li class="document-item">
              <div class="document-details">
                <strong>{{ item.title }}</strong>
                <span>{{ item.originalFileName }} · {{ item.category }} · {{ formatSize(item.sizeBytes) }}</span>
                <span class="scan-status" [attr.data-status]="item.scanStatus">
                  Scan: {{ formatScanStatus(item.scanStatus) }}
                </span>
              </div>
              <div class="document-actions">
                <button
                  class="secondary-button download-button"
                  type="button"
                  [attr.aria-label]="downloadingId() === item.id ? 'Downloading document' : 'Download document'"
                  [title]="downloadingId() === item.id ? 'Downloading...' : 'Download'"
                  [disabled]="downloadingId() === item.id || item.scanStatus !== 'CLEAN'"
                  (click)="downloadDocument(item)">
                  <span aria-hidden="true">{{ downloadingId() === item.id ? '…' : '↓' }}</span>
                </button>
                @if (canRetryScan(item)) {
                  <button
                    class="secondary-button retry-scan-button"
                    type="button"
                    [disabled]="retryingScanId() === item.id"
                    (click)="retryScan(item)">
                    {{ retryingScanId() === item.id ? 'Scanning...' : 'Retry scan' }}
                  </button>
                }
                @if (canEdit(item) && item.scanStatus === 'CLEAN') {
                  <button
                    class="secondary-button edit-button"
                    type="button"
                    aria-label="Edit document"
                    title="Edit"
                    (click)="openEditor(item)">
                    <span aria-hidden="true">✎</span>
                  </button>
                }
                <button
                  class="secondary-button audits-button"
                  type="button"
                  [attr.aria-expanded]="selectedAuditDocumentId() === item.id"
                  aria-label="Audits"
                  title="Audits"
                  (click)="toggleAudits(item.id)">
                  <span aria-hidden="true">◷</span>
                </button>
                @if (item.isOwner) {
                  <button
                    class="secondary-button remove-button"
                    type="button"
                    [disabled]="deletingId() === item.id"
                    aria-label="Remove document"
                    title="Remove document"
                    (click)="removeDocument(item)">
                    <span aria-hidden="true">{{ deletingId() === item.id ? '…' : '🗑' }}</span>
                  </button>
                  <button
                    class="secondary-button access-button"
                    type="button"
                    [attr.aria-expanded]="selectedShareDocumentId() === item.id"
                    [attr.aria-label]="selectedShareDocumentId() === item.id ? 'Close access management' : 'Manage access'"
                    [title]="selectedShareDocumentId() === item.id ? 'Close access' : 'Manage access'"
                    (click)="toggleAccess(item.id)">
                    <span aria-hidden="true">⇄</span>
                  </button>
                } @else {
                  <span class="permission-badge">{{ item.permission }} access</span>
                }
              </div>
            </li>
            @if (selectedAuditDocumentId() === item.id) {
              <li class="audit-panel">
                <h4>Document activity</h4>
                @if (auditsLoading()) {
                  <p class="empty-state">Loading activity...</p>
                } @else if (audits().length === 0) {
                  <p class="empty-state">No document activity has been recorded yet.</p>
                } @else {
                  <ol class="audit-list">
                    @for (audit of audits(); track audit.id) {
                      <li class="audit-item">
                        <strong>
                          {{ audit.activity === 'CREATED' ? 'Created' : audit.activity === 'EDITED' ? 'Edited' : audit.activity === 'SCANNED' ? 'Scanned' : 'Read' }}
                        </strong>
                        <span>{{ audit.actorEmail ?? 'Unknown user' }}</span>
                        <span
                          class="audit-result"
                          [class.denied]="audit.result === 'DENIED'">
                          {{ audit.result === 'SUCCESS' ? 'Successful' : 'Denied' }}
                        </span>
                        <time [attr.datetime]="audit.timestampUtc">{{ formatAuditDate(audit.timestampUtc) }}</time>
                      </li>
                    }
                  </ol>
                }
              </li>
            }
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
                    <option value="READ">Read-only</option>
                    <option value="EDIT">Can edit</option>
                  </select>
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
    @if (editingDocument(); as item) {
      @if (isPdf(item)) {
        @defer (when editingDocument() !== null) {
          <app-document-editor
            [item]="item"
            (closed)="closeEditor()"
            (saved)="onDocumentSaved($event)" />
        }
      } @else {
        <app-document-text-editor
          [item]="item"
          (closed)="closeEditor()"
          (saved)="onDocumentSaved($event)" />
      }
    }
  `,
  styleUrl: './documents-panel.component.scss',
})
export class DocumentsPanelComponent {
  private readonly formBuilder = inject(FormBuilder);
  private readonly documentService = inject(DocumentService);

  readonly documents = signal<DocumentItem[]>([]);
  readonly editingDocument = signal<DocumentItem | null>(null);
  readonly selectedFile = signal<File | null>(null);
  readonly loading = signal(false);
  readonly uploading = signal(false);
  readonly downloadingId = signal<string | null>(null);
  readonly retryingScanId = signal<string | null>(null);
  readonly deletingId = signal<string | null>(null);
  readonly selectedShareDocumentId = signal<string | null>(null);
  readonly selectedAuditDocumentId = signal<string | null>(null);
  readonly audits = signal<DocumentAuditItem[]>([]);
  readonly auditsLoading = signal(false);
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
  readonly filtersForm = this.formBuilder.nonNullable.group({
    search: [''],
    category: [''],
    owner: [''],
    createdFrom: [''],
    createdTo: [''],
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

    if (!/\.(pdf|txt|doc|docx)$/i.test(file.name)) {
      this.error.set('Choose a PDF, TXT, DOC, or DOCX file.');
      input.value = '';
      return;
    }

    if (file.size === 0 || file.size > maximumFileBytes) {
      this.error.set('The file must be between 1 byte and 10 MiB.');
      input.value = '';
      return;
    }

    this.selectedFile.set(file);
  }

  applyFilters(): void {
    this.refreshDocuments();
  }

  clearFilters(): void {
    this.filtersForm.reset();
    this.refreshDocuments();
  }

  refreshDocuments(): void {
    this.loading.set(true);
    this.error.set(null);
    const filters = this.filtersForm.getRawValue();
    this.documentService
      .list({
        search: filters.search?.trim() || undefined,
        category: filters.category?.trim() || undefined,
        owner: filters.owner?.trim() || undefined,
        createdFrom: filters.createdFrom || undefined,
        createdTo: filters.createdTo || undefined,
      })
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
          this.message.set(
            uploaded.scanStatus === 'CLEAN'
              ? `${file.name} was uploaded and passed the malware scan.`
              : `${file.name} was uploaded with scan status ${this.formatScanStatus(uploaded.scanStatus)}. It is not available until it passes the scan.`,
          );
        },
        error: (error: unknown) => this.error.set(this.describeError(error, 'upload this file')),
      });
  }

  downloadDocument(item: DocumentItem): void {
    if (this.downloadingId() || item.scanStatus !== 'CLEAN') {
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
        error: (error: unknown) => this.error.set(this.describeError(error, 'download this file')),
      });
  }

  removeDocument(item: DocumentItem): void {
    if (
      this.deletingId()
      || !globalThis.confirm(
        `Remove "${item.title}"? This will delete the document and revoke all access grants, so everyone you shared it with will lose access. This cannot be undone.`,
      )
    ) {
      return;
    }

    this.message.set(null);
    this.error.set(null);
    this.deletingId.set(item.id);
    this.documentService
      .deleteDocument(item.id)
      .pipe(finalize(() => this.deletingId.set(null)))
      .subscribe({
        next: () => {
          this.documents.update((documents) =>
            documents.filter((document) => document.id !== item.id),
          );
          if (this.selectedAuditDocumentId() === item.id) {
            this.selectedAuditDocumentId.set(null);
            this.audits.set([]);
          }
          if (this.selectedShareDocumentId() === item.id) {
            this.selectedShareDocumentId.set(null);
            this.grants.set([]);
          }
          if (this.editingDocument()?.id === item.id) {
            this.editingDocument.set(null);
          }
          this.message.set(`"${item.title}" was removed and shared access was revoked.`);
        },
        error: (error: unknown) =>
          this.error.set(this.describeError(error, 'remove this document')),
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

  toggleAudits(documentId: string): void {
    if (this.selectedAuditDocumentId() === documentId) {
      this.selectedAuditDocumentId.set(null);
      this.audits.set([]);
      return;
    }

    this.selectedAuditDocumentId.set(documentId);
    this.audits.set([]);
    this.loadAudits(documentId);
  }

  canEdit(item: DocumentItem): boolean {
    return item.scanStatus === 'CLEAN' && (item.isOwner || item.permission === 'EDIT');
  }

  canRetryScan(item: DocumentItem): boolean {
    const retryableStatus = item.scanStatus === 'PENDING_SCAN'
      || item.scanStatus === 'SCAN_FAILED'
      || (item.scanStatus === 'SCANNING'
        && item.lastScanAtUtc !== null
        && Date.now() - Date.parse(item.lastScanAtUtc) > 2 * 60 * 1000);
    return item.isOwner && retryableStatus;
  }

  formatScanStatus(status: DocumentItem['scanStatus']): string {
    return status.replaceAll('_', ' ').toLowerCase();
  }

  retryScan(item: DocumentItem): void {
    if (!this.canRetryScan(item) || this.retryingScanId()) {
      return;
    }

    this.error.set(null);
    this.message.set(null);
    this.retryingScanId.set(item.id);
    this.documentService
      .retryScan(item.id)
      .pipe(finalize(() => this.retryingScanId.set(null)))
      .subscribe({
        next: (result) => {
          this.documents.update((documents) =>
            documents.map((document) =>
              document.id === item.id ? { ...document, ...result } : document,
            ),
          );
          this.message.set(
            result.scanStatus === 'CLEAN'
              ? `${item.originalFileName} passed the malware scan.`
              : `Scan finished with status ${this.formatScanStatus(result.scanStatus)}.`,
          );
        },
        error: (error: unknown) =>
          this.error.set(this.describeError(error, 'retry the malware scan')),
      });
  }

  isPdf(item: DocumentItem): boolean {
    return item.originalFileName.toLowerCase().endsWith('.pdf');
  }

  openEditor(item: DocumentItem): void {
    if (this.canEdit(item)) {
      this.editingDocument.set(item);
      this.error.set(null);
      this.message.set(null);
    }
  }

  closeEditor(): void {
    this.editingDocument.set(null);
  }

  onDocumentSaved(updated: DocumentItem): void {
    this.documents.update((documents) =>
      documents.map((item) => (item.id === updated.id ? updated : item)),
    );
    this.editingDocument.set(updated);
    this.message.set('Document changes were saved.');
    this.refreshDocuments();
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
      this.error.set('Enter an email and choose Read or Edit access.');
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

  private loadAudits(documentId: string): void {
    this.auditsLoading.set(true);
    this.error.set(null);
    this.documentService
      .listAudits(documentId)
      .pipe(finalize(() => this.auditsLoading.set(false)))
      .subscribe({
        next: (audits) => this.audits.set(audits),
        error: (error: unknown) =>
          this.error.set(this.describeError(error, 'load document audits')),
      });
  }

  formatAuditDate(timestampUtc: string): string {
    return new Date(timestampUtc).toLocaleString();
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
