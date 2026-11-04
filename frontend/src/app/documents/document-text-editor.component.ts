import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { finalize } from 'rxjs';
import { DocumentItem, DocumentService, DocumentVersionItem } from './document.service';

@Component({
  selector: 'app-document-text-editor',
  template: `
    <div class="text-editor-backdrop">
      <section class="text-editor-dialog" role="dialog" aria-modal="true" aria-labelledby="text-editor-title">
        <div class="text-editor-heading">
          <div>
            <p class="eyebrow">DOCUMENT TEXT EDITOR</p>
            <h2 id="text-editor-title">{{ item().title }}</h2>
            <p class="file-name">{{ item().originalFileName }}</p>
          </div>
          <button class="secondary-button" type="button" (click)="closed.emit()">Close</button>
        </div>

        @if (isWordDocument()) {
          <p class="conversion-warning">
            Saving converts this file to a simple DOCX document. Word formatting, images, tables,
            and other embedded content are not preserved. The original file is saved in previous
            versions.
          </p>
        }
        @if (error()) {
          <p class="editor-error" role="alert">{{ error() }}</p>
        }
        @if (message()) {
          <p class="editor-message" role="status">{{ message() }}</p>
        }

        @if (loading()) {
          <p class="editor-hint">Loading document text...</p>
        } @else {
          <label for="document-text-content">Text content</label>
          <textarea
            id="document-text-content"
            [value]="text()"
            (input)="onTextInput($event)"
            [disabled]="saving()"
            spellcheck="true">
          </textarea>
          <button
            class="primary-button"
            type="button"
            [disabled]="saving() || loading()"
            (click)="save()">
            {{ saving() ? 'Saving...' : isWordDocument() ? 'Save as DOCX' : 'Save text' }}
          </button>
        }

        <section class="previous-versions">
          <h3>Previous versions</h3>
          @if (versionsLoading()) {
            <p class="editor-hint">Loading versions...</p>
          } @else if (versions().length === 0) {
            <p class="editor-hint">A copy of the original file will appear here after saving.</p>
          } @else {
            <ul>
              @for (version of versions(); track version.versionNumber) {
                <li>
                  <span>
                    Version {{ version.versionNumber }} · {{ version.originalFileName }}
                    · Scan {{ version.scanStatus.toLowerCase().replaceAll('_', ' ') }}
                  </span>
                  @if (item().isOwner && canRetryVersionScan(version)) {
                    <button
                      class="secondary-button"
                      type="button"
                      [disabled]="retryingVersion() === version.versionNumber"
                      (click)="retryPreviousVersionScan(version)">
                      {{ retryingVersion() === version.versionNumber ? 'Scanning...' : 'Retry scan' }}
                    </button>
                  }
                  <button
                    class="secondary-button"
                    type="button"
                    [disabled]="downloadingVersion() === version.versionNumber || version.scanStatus !== 'CLEAN'"
                    (click)="downloadPreviousVersion(version)">
                    {{ downloadingVersion() === version.versionNumber ? 'Downloading...' : 'Download' }}
                  </button>
                </li>
              }
            </ul>
          }
        </section>
      </section>
    </div>
  `,
  styleUrl: './document-text-editor.component.scss',
})
export class DocumentTextEditorComponent implements OnInit {
  readonly item = input.required<DocumentItem>();
  readonly closed = output<void>();
  readonly saved = output<DocumentItem>();

  private readonly documentService = inject(DocumentService);
  readonly text = signal('');
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly versionsLoading = signal(true);
  readonly downloadingVersion = signal<number | null>(null);
  readonly versions = signal<DocumentVersionItem[]>([]);
  readonly retryingVersion = signal<number | null>(null);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);

  ngOnInit(): void {
    this.documentService
      .getEditableText(this.item().id)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (result) => this.text.set(result.text),
        error: (error: unknown) => this.error.set(this.describeError(error, 'load document text')),
      });
    this.loadVersions();
  }

  isWordDocument(): boolean {
    return /\.(doc|docx)$/i.test(this.item().originalFileName);
  }

  onTextInput(event: Event): void {
    if (event.target instanceof HTMLTextAreaElement) {
      this.text.set(event.target.value);
    }
  }

  save(): void {
    if (this.saving()) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.message.set(null);
    this.documentService
      .updateEditableText(this.item().id, this.text())
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: (updated) => {
          this.saved.emit(updated);
          this.message.set(
            this.isWordDocument()
              ? 'The text was saved as a DOCX file. The original Word file is in previous versions.'
              : 'The text file was saved. The previous version is available below.',
          );
          this.loadVersions();
        },
        error: (error: unknown) => this.error.set(this.describeError(error, 'save document text')),
      });
  }

  downloadPreviousVersion(version: DocumentVersionItem): void {
    if (this.downloadingVersion() || version.scanStatus !== 'CLEAN') {
      return;
    }

    this.downloadingVersion.set(version.versionNumber);
    this.documentService
      .downloadVersion(this.item().id, version.versionNumber)
      .pipe(finalize(() => this.downloadingVersion.set(null)))
      .subscribe({
        next: (file) => {
          const url = URL.createObjectURL(file);
          const link = globalThis.document.createElement('a');
          link.href = url;
          link.download = version.originalFileName;
          link.click();
          URL.revokeObjectURL(url);
        },
        error: (error: unknown) =>
          this.error.set(this.describeError(error, 'download the previous version')),
      });
  }

  canRetryVersionScan(version: DocumentVersionItem): boolean {
    return version.scanStatus === 'PENDING_SCAN'
      || version.scanStatus === 'SCAN_FAILED'
      || (version.scanStatus === 'SCANNING'
        && version.lastScanAtUtc !== null
        && Date.now() - Date.parse(version.lastScanAtUtc) > 10 * 60 * 1000);
  }

  retryPreviousVersionScan(version: DocumentVersionItem): void {
    if (!this.item().isOwner || !this.canRetryVersionScan(version) || this.retryingVersion()) {
      return;
    }

    this.retryingVersion.set(version.versionNumber);
    this.documentService
      .retryVersionScan(this.item().id, version.versionNumber)
      .pipe(finalize(() => this.retryingVersion.set(null)))
      .subscribe({
        next: (result) =>
          this.versions.update((versions) =>
            versions.map((item) =>
              item.versionNumber === version.versionNumber ? { ...item, ...result } : item,
            ),
          ),
        error: (error: unknown) =>
          this.error.set(this.describeError(error, 'retry the previous version scan')),
      });
  }

  private loadVersions(): void {
    this.versionsLoading.set(true);
    this.documentService
      .listVersions(this.item().id)
      .pipe(finalize(() => this.versionsLoading.set(false)))
      .subscribe({
        next: (versions) => this.versions.set(versions),
        error: (error: unknown) =>
          this.error.set(this.describeError(error, 'load previous versions')),
      });
  }

  private describeError(error: unknown, action: string): string {
    if (error instanceof HttpErrorResponse) {
      const detail = error.error?.detail;
      if (typeof detail === 'string') {
        return detail;
      }
      if (error.status === 413) {
        return 'This file contains too much text to edit in the browser.';
      }
      if (error.status === 0) {
        return 'The API could not be reached. Check that the API is running, then try again.';
      }
    }

    return `Could not ${action}. Please try again.`;
  }
}
