import { HttpErrorResponse } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import {
  AdminAuditPage,
  AdminDashboard,
  AdminDocument,
  AdminDocumentPage,
  AdminOversightService,
  AdminPermission,
  AdminShare,
  AdminSharePage,
  AdminSecurityAlert,
  AdminSystemSettings,
} from './admin-oversight.service';

type AdminSection = 'overview' | 'audit' | 'documents' | 'settings';

@Component({
  selector: 'app-admin-console',
  imports: [DatePipe],
  template: `
    <section class="admin-console" aria-labelledby="admin-console-heading">
      <h2 id="admin-console-heading">Administration</h2>
      <p class="admin-notice">
        Platform oversight only. Administrator access does not grant permission to read document contents.
      </p>
      <nav class="admin-navigation" aria-label="Administration sections">
        <button type="button" (click)="selectSection('overview')">Overview</button>
        <button type="button" (click)="selectSection('audit')">Audit &amp; alerts</button>
        <button type="button" (click)="selectSection('documents')">Document metadata</button>
        <button type="button" (click)="selectSection('settings')">System settings</button>
      </nav>
      @if (message()) {
        <p class="form-message" role="alert">{{ message() }}</p>
      }

      @if (section() === 'overview') {
        <section aria-labelledby="dashboard-heading">
          <h3 id="dashboard-heading">Platform overview</h3>
          @if (dashboard(); as summary) {
            <dl class="admin-stat-grid">
              <div><dt>Active members</dt><dd>{{ summary.activeMembers }}</dd></div>
              <div><dt>Removed members</dt><dd>{{ summary.suspendedMembers }}</dd></div>
              <div><dt>Active documents</dt><dd>{{ summary.activeDocuments }}</dd></div>
              <div><dt>Awaiting clean scan</dt><dd>{{ summary.documentsAwaitingScan }}</dd></div>
              <div><dt>Active named-user grants</dt><dd>{{ summary.activeGrants }}</dd></div>
              <div><dt>Denied attempts · 24h</dt><dd>{{ summary.deniedAttemptsLast24Hours }}</dd></div>
              <div><dt>Downloads · 24h</dt><dd>{{ summary.successfulDownloadsLast24Hours }}</dd></div>
            </dl>
          } @else {
            <button type="button" (click)="loadOverview()">Load overview</button>
          }
          <button type="button" (click)="loadOverview()">Refresh overview</button>
          <h4>Security alerts</h4>
          @if (alerts().length === 0) {
            <p>No threshold alerts are active.</p>
          } @else {
            <ul class="admin-results">
              @for (alert of alerts(); track alert.type + alert.actorUserId + alert.ipAddress) {
                <li>
                  <strong>{{ alert.severity }} · {{ alert.type }}</strong>
                  <span>{{ alert.actorEmail ?? alert.actorUserId }} · {{ alert.ipAddress ?? 'IP unavailable' }}</span>
                  <span>{{ alert.eventCount }} events, {{ alert.windowStartUtc | date:'short' }} – {{ alert.windowEndUtc | date:'short' }}</span>
                </li>
              }
            </ul>
          }
        </section>
      }

      @if (section() === 'audit') {
        <section aria-labelledby="audit-heading">
          <h3 id="audit-heading">Audit investigation</h3>
          <div class="admin-filter-grid">
            <label>User email or ID <input #auditUser /></label>
            <label>Document ID <input #auditDocument /></label>
            <label>Action <input #auditAction /></label>
            <label>IP address <input #auditIp /></label>
            <label>Outcome
              <select #auditOutcome>
                <option value="">All</option><option value="SUCCESS">Success</option><option value="DENIED">Denied</option>
              </select>
            </label>
            <label>From (local time) <input #auditFrom type="datetime-local" /></label>
            <label>To (local time) <input #auditTo type="datetime-local" /></label>
          </div>
          <div class="admin-actions">
            <button type="button" (click)="searchAudit(auditUser.value, auditDocument.value, auditAction.value, auditIp.value, auditOutcome.value, auditFrom.value, auditTo.value)">Search audit</button>
            <button type="button" (click)="exportAudit(auditUser.value, auditDocument.value, auditAction.value, auditIp.value, auditOutcome.value, auditFrom.value, auditTo.value)">Export CSV</button>
            <button type="button" (click)="verifyAudit()">Verify chain</button>
          </div>
          @if (verification(); as result) {
            <p class="admin-notice" role="status">
              Chain {{ result.isValid ? 'valid' : 'INVALID' }} · {{ result.eventCount }} events
              @if (result.error) { · {{ result.error }} }
            </p>
          }
          @if (auditPage(); as result) {
            <p>{{ result.totalCount }} matching event(s)</p>
            <ul class="admin-results">
              @for (event of result.items; track event.id) {
                <li>
                  <strong>{{ event.action }} · {{ event.result }}</strong>
                  <span>{{ event.actorEmail ?? 'System/unknown' }} ({{ event.actorRole || 'no role' }})</span>
                  <span>Document: {{ event.documentId ?? '—' }} · IP: {{ event.ipAddress ?? 'unavailable' }}</span>
                  <time [attr.datetime]="event.timestampUtc">{{ event.timestampUtc | date:'medium' }}</time>
                </li>
              }
            </ul>
            @if (result.page * result.pageSize < result.totalCount) {
              <button type="button" (click)="nextAuditPage()">Next page</button>
            }
          }
        </section>
      }

      @if (section() === 'documents') {
        <section aria-labelledby="admin-documents-heading">
          <h3 id="admin-documents-heading">Document metadata &amp; permissions</h3>
          <p>Document content and storage keys are intentionally not exposed here.</p>
          <div class="admin-filter-grid">
            <label>Title or filename <input #documentSearch /></label>
            <label>Owner email <input #documentOwner /></label>
            <label>Category <input #documentCategory /></label>
          </div>
          <button type="button" (click)="searchDocuments(documentSearch.value, documentOwner.value, documentCategory.value)">Search documents</button>
          <h4>Named-user shares</h4>
          <div class="admin-filter-grid">
            <label>Document title or account email <input #shareSearch /></label>
          </div>
          <button type="button" (click)="searchShares(shareSearch.value)">Search shares</button>
          <p>{{ sharesTotal() }} active share(s)</p>
          <ul class="admin-results">
            @for (share of shares(); track share.documentId + share.userId) {
              <li>
                <strong>{{ share.documentTitle }} · {{ share.permission }}</strong>
                <span>Owner: {{ share.ownerEmail }} · Recipient: {{ share.userEmail }}</span>
                <time [attr.datetime]="share.grantedAtUtc">{{ share.grantedAtUtc | date:'medium' }}</time>
                <label>Revocation reason <input #shareReason maxlength="500" /></label>
                <button type="button" (click)="revokeShare(share, shareReason.value)">Revoke share</button>
              </li>
            }
          </ul>
          @if (sharesPage() * 50 < sharesTotal()) {
            <button type="button" (click)="nextSharesPage()">Next shares page</button>
          }
          <ul class="admin-results">
            @for (document of documents(); track document.id) {
              <li>
                <strong>{{ document.title }} · {{ document.category }}</strong>
                <span>Owner: {{ document.ownerEmail }} · {{ document.originalFileName }} · {{ document.sizeBytes }} bytes</span>
                <span>Uploaded {{ document.createdAtUtc | date:'medium' }} · {{ document.versionCount }} versions · {{ document.activeGrantCount }} active grants · {{ document.scanStatus }}</span>
                <button type="button" (click)="loadPermissions(document)">Permission history</button>
                @if (!document.isDeleted) {
                  <label>Reassign to active member ID <input #newOwnerId /></label>
                  <label>Reason <input #reassignReason maxlength="500" /></label>
                  <button type="button" (click)="reassignOwner(document, newOwnerId.value, reassignReason.value)">Reassign owner</button>
                }
                @if (selectedDocumentId() === document.id) {
                  <ul class="admin-permission-list">
                    @for (permission of permissions(); track permission.userId + permission.grantedAtUtc) {
                      <li>
                        {{ permission.email }} · {{ permission.permission }} ·
                        {{ permission.revokedAtUtc ? 'Revoked' : 'Active' }}
                        @if (!permission.revokedAtUtc) {
                          <label>Reason <input #revokeReason maxlength="500" /></label>
                          <button type="button" (click)="revokePermission(document, permission, revokeReason.value)">Revoke grant</button>
                        }
                      </li>
                    }
                  </ul>
                }
              </li>
            }
          </ul>
          <p>{{ documentsTotal() }} document(s) match</p>
          @if (documentsPage() * 50 < documentsTotal()) {
            <button type="button" (click)="nextDocumentsPage(documentSearch.value, documentOwner.value, documentCategory.value)">Next documents page</button>
          }
        </section>
      }

      @if (section() === 'settings') {
        <section aria-labelledby="settings-heading">
          <h3 id="settings-heading">System policy settings</h3>
          <p class="admin-notice">
            Security-sensitive values are read-only here and must be changed through reviewed deployment configuration.
            No credentials, storage paths, scanner addresses, or secret values are exposed.
          </p>
          @if (settings(); as policy) {
            <dl class="admin-settings-list">
              <dt>Allowed document types</dt><dd>{{ policy.allowedDocumentExtensions.join(', ') }}</dd>
              <dt>Maximum upload size</dt><dd>{{ policy.maximumUploadBytes / 1024 / 1024 }} MiB</dd>
              <dt>Access-token lifetime</dt><dd>{{ policy.accessTokenLifetimeMinutes }} minutes</dd>
              <dt>Sign-in lockout</dt><dd>{{ policy.maximumFailedSignIns }} attempts / {{ policy.lockoutMinutes }} minutes</dd>
              <dt>Anonymous guest links</dt><dd>{{ policy.externalGuestLinksEnabled ? 'Enabled' : 'Disabled' }}</dd>
              <dt>Changes</dt><dd>{{ policy.configurationManagement }}</dd>
            </dl>
          } @else {
            <button type="button" (click)="loadSettings()">Load settings</button>
          }
        </section>
      }
    </section>
  `,
  styleUrl: './admin-console.component.scss',
})
export class AdminConsoleComponent {
  private readonly service = inject(AdminOversightService);
  readonly section = signal<AdminSection>('overview');
  readonly dashboard = signal<AdminDashboard | null>(null);
  readonly alerts = signal<AdminSecurityAlert[]>([]);
  readonly auditPage = signal<AdminAuditPage | null>(null);
  readonly documents = signal<AdminDocument[]>([]);
  readonly documentsPage = signal(0);
  readonly documentsTotal = signal(0);
  readonly selectedDocumentId = signal<string | null>(null);
  readonly permissions = signal<AdminPermission[]>([]);
  readonly shares = signal<AdminShare[]>([]);
  readonly sharesPage = signal(0);
  readonly sharesTotal = signal(0);
  readonly settings = signal<AdminSystemSettings | null>(null);
  readonly verification = signal<{ isValid: boolean; eventCount: number; error: string | null } | null>(null);
  readonly message = signal<string | null>(null);
  private currentAuditFilters: Parameters<AdminOversightService['audit']>[0] = {};
  private currentShareSearch = '';

  constructor() {
    this.loadOverview();
  }

  selectSection(section: AdminSection): void {
    this.section.set(section);
    this.message.set(null);
    if (section === 'overview' && !this.dashboard()) {
      this.loadOverview();
    } else if (section === 'settings' && !this.settings()) {
      this.loadSettings();
    }
  }

  loadOverview(): void {
    this.message.set(null);
    this.service.dashboard().subscribe({
      next: (value) => this.dashboard.set(value),
      error: (error: unknown) => this.showError(error),
    });
    this.service.securityAlerts().subscribe({
      next: (value) => this.alerts.set(value),
      error: (error: unknown) => this.showError(error),
    });
  }

  searchAudit(
    user: string,
    documentId: string,
    action: string,
    ipAddress: string,
    outcome: string,
    from: string,
    to: string,
  ): void {
    this.currentAuditFilters = {
      user,
      documentId,
      action,
      ipAddress,
      outcome,
      fromUtc: this.toUtc(from),
      toUtc: this.toUtc(to),
      page: 1,
      pageSize: 50,
    };
    this.loadAuditPage();
  }

  nextAuditPage(): void {
    const page = this.auditPage();
    if (!page) {
      return;
    }
    this.currentAuditFilters = { ...this.currentAuditFilters, page: page.page + 1 };
    this.loadAuditPage(true);
  }

  exportAudit(
    user: string,
    documentId: string,
    action: string,
    ipAddress: string,
    outcome: string,
    from: string,
    to: string,
  ): void {
    const filters = {
      user,
      documentId,
      action,
      ipAddress,
      outcome,
      fromUtc: this.toUtc(from),
      toUtc: this.toUtc(to),
    };
    this.service.exportAudit(filters).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = 'trustvault-audit.csv';
        anchor.click();
        URL.revokeObjectURL(url);
      },
      error: (error: unknown) => this.showError(error),
    });
  }

  verifyAudit(): void {
    this.service.verifyAudit().subscribe({
      next: (result) => this.verification.set(result),
      error: (error: unknown) => this.showError(error),
    });
  }

  searchDocuments(search: string, owner: string, category: string, page = 1): void {
    this.service.documents(search, owner, category, page).subscribe({
      next: (response: AdminDocumentPage) => {
        this.documents.set(response.items);
        this.documentsPage.set(response.page);
        this.documentsTotal.set(response.totalCount);
        this.selectedDocumentId.set(null);
        this.permissions.set([]);
      },
      error: (error: unknown) => this.showError(error),
    });
  }

  nextDocumentsPage(search: string, owner: string, category: string): void {
    this.searchDocuments(search, owner, category, this.documentsPage() + 1);
  }

  loadPermissions(document: AdminDocument): void {
    this.selectedDocumentId.set(document.id);
    this.service.permissions(document.id).subscribe({
      next: (result) => this.permissions.set(result),
      error: (error: unknown) => this.showError(error),
    });
  }

  searchShares(search: string): void {
    this.currentShareSearch = search;
    this.service.shares(search, 1).subscribe({
      next: (response: AdminSharePage) => {
        this.shares.set(response.items);
        this.sharesPage.set(response.page);
        this.sharesTotal.set(response.totalCount);
      },
      error: (error: unknown) => this.showError(error),
    });
  }

  nextSharesPage(): void {
    this.service.shares(this.currentShareSearch, this.sharesPage() + 1).subscribe({
      next: (response) => {
        this.shares.set([...this.shares(), ...response.items]);
        this.sharesPage.set(response.page);
        this.sharesTotal.set(response.totalCount);
      },
      error: (error: unknown) => this.showError(error),
    });
  }

  revokeShare(share: AdminShare, reason: string): void {
    this.revokeGrant(share.documentId, share.userId, reason, () =>
      this.searchShares(this.currentShareSearch));
  }

  reassignOwner(document: AdminDocument, newOwnerId: string, reason: string): void {
    if (!newOwnerId.trim() || reason.trim().length < 8) {
      this.message.set('Provide an active member ID and a reason of at least 8 characters.');
      return;
    }

    this.service.reassignOwner(document.id, newOwnerId.trim(), reason.trim())
      .subscribe({
        next: () => {
          this.message.set('Document ownership reassigned; all previous grants were revoked.');
          this.searchDocuments('', '', '');
        },
        error: (error: unknown) => this.showError(error),
      });
  }

  revokePermission(document: AdminDocument, permission: AdminPermission, reason: string): void {
    this.revokeGrant(document.id, permission.userId, reason, () => {
      this.loadPermissions(document);
      this.searchDocuments('', '', '');
    });
  }

  private revokeGrant(
    documentId: string,
    userId: string,
    reason: string,
    onSuccess: () => void,
  ): void {
    if (reason.trim().length < 8) {
      this.message.set('Enter a reason of at least 8 characters before revoking a permission.');
      return;
    }

    this.service.revokePermission(documentId, userId, reason.trim())
      .subscribe({
        next: () => {
          this.message.set('Permission revoked. The user loses access on their next request.');
          onSuccess();
        },
        error: (error: unknown) => this.showError(error),
      });
  }

  loadSettings(): void {
    this.service.settings().subscribe({
      next: (value) => this.settings.set(value),
      error: (error: unknown) => this.showError(error),
    });
  }

  private loadAuditPage(append = false): void {
    this.service.audit(this.currentAuditFilters).subscribe({
      next: (result) => this.auditPage.set(append
        ? { ...result, items: [...(this.auditPage()?.items ?? []), ...result.items] }
        : result),
      error: (error: unknown) => this.showError(error),
    });
  }

  private toUtc(value: string): string | undefined {
    if (!value) {
      return undefined;
    }
    return new Date(value).toISOString();
  }

  private showError(error: unknown): void {
    if (error instanceof HttpErrorResponse && error.status === 403) {
      this.message.set('Only the administrator can use these controls.');
      return;
    }
    if (error instanceof HttpErrorResponse && error.status === 0) {
      this.message.set('The service could not be reached. Check the API connection.');
      return;
    }
    this.message.set('The request could not be completed. Try again.');
  }
}
