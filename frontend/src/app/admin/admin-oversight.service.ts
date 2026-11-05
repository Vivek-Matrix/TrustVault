import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface AdminDashboard {
  activeMembers: number;
  suspendedMembers: number;
  activeDocuments: number;
  documentsAwaitingScan: number;
  activeGrants: number;
  deniedAttemptsLast24Hours: number;
  successfulDownloadsLast24Hours: number;
}

export interface AdminAuditEvent {
  id: string;
  actorUserId: string | null;
  actorEmail: string | null;
  actorRole: string;
  documentId: string | null;
  action: string;
  result: string;
  timestampUtc: string;
  requestId: string;
  ipAddress: string | null;
  userAgent: string | null;
}

export interface AdminAuditPage {
  page: number;
  pageSize: number;
  totalCount: number;
  items: AdminAuditEvent[];
}

export interface AdminSecurityAlert {
  type: string;
  severity: string;
  actorUserId: string | null;
  actorEmail: string | null;
  ipAddress: string | null;
  eventCount: number;
  windowStartUtc: string;
  windowEndUtc: string;
}

export interface AdminDocument {
  id: string;
  title: string;
  category: string;
  originalFileName: string;
  sizeBytes: number;
  ownerId: string;
  ownerEmail: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  versionCount: number;
  activeGrantCount: number;
  scanStatus: string;
  isDeleted: boolean;
}

export interface AdminDocumentPage {
  page: number;
  pageSize: number;
  totalCount: number;
  items: AdminDocument[];
}

export interface AdminPermission {
  userId: string;
  email: string;
  permission: string;
  grantedByUserId: string;
  grantedAtUtc: string;
  revokedAtUtc: string | null;
  revocationReason: string | null;
}

export interface AdminShare {
  documentId: string;
  documentTitle: string;
  ownerId: string;
  ownerEmail: string;
  userId: string;
  userEmail: string;
  permission: string;
  grantedAtUtc: string;
}

export interface AdminSharePage {
  page: number;
  pageSize: number;
  totalCount: number;
  items: AdminShare[];
}

export interface AdminSystemSettings {
  allowedDocumentExtensions: string[];
  maximumUploadBytes: number;
  accessTokenLifetimeMinutes: number;
  maximumFailedSignIns: number;
  lockoutMinutes: number;
  externalGuestLinksEnabled: boolean;
  configurationManagement: string;
}

export interface AuditFilters {
  user?: string;
  documentId?: string;
  action?: string;
  fromUtc?: string;
  toUtc?: string;
  ipAddress?: string;
  outcome?: string;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class AdminOversightService {
  private readonly http = inject(HttpClient);
  private readonly adminUrl = `${environment.apiBaseUrl}/admin`;

  dashboard(): Observable<AdminDashboard> {
    return this.http.get<AdminDashboard>(`${this.adminUrl}/dashboard`);
  }

  securityAlerts(): Observable<AdminSecurityAlert[]> {
    return this.http.get<AdminSecurityAlert[]>(`${this.adminUrl}/security-alerts`);
  }

  audit(filters: AuditFilters): Observable<AdminAuditPage> {
    return this.http.get<AdminAuditPage>(
      `${this.adminUrl}/audit`,
      { params: this.createParams(filters) },
    );
  }

  exportAudit(filters: AuditFilters): Observable<Blob> {
    return this.http.get(
      `${this.adminUrl}/audit/export`,
      { params: this.createParams(filters), responseType: 'blob' },
    );
  }

  verifyAudit(): Observable<{ isValid: boolean; eventCount: number; error: string | null }> {
    return this.http.get<{ isValid: boolean; eventCount: number; error: string | null }>(
      `${this.adminUrl}/audit/verify`,
    );
  }

  documents(
    search: string,
    owner: string,
    category: string,
    page: number,
  ): Observable<AdminDocumentPage> {
    const params = this.createParams({ search, owner, category, page, pageSize: 50 });
    return this.http.get<AdminDocumentPage>(`${this.adminUrl}/documents`, { params });
  }

  permissions(documentId: string): Observable<AdminPermission[]> {
    return this.http.get<AdminPermission[]>(
      `${this.adminUrl}/documents/${encodeURIComponent(documentId)}/permissions`,
    );
  }

  shares(search: string, page: number): Observable<AdminSharePage> {
    const params = this.createParams({ search, page, pageSize: 50 });
    return this.http.get<AdminSharePage>(`${this.adminUrl}/shares`, { params });
  }

  revokePermission(documentId: string, userId: string, reason: string): Observable<void> {
    return this.http.delete<void>(
      `${this.adminUrl}/documents/${encodeURIComponent(documentId)}/permissions/${encodeURIComponent(userId)}`,
      { body: { reason } },
    );
  }

  reassignOwner(documentId: string, newOwnerId: string, reason: string): Observable<void> {
    return this.http.post<void>(
      `${this.adminUrl}/documents/${encodeURIComponent(documentId)}/reassign-owner`,
      { newOwnerId, reason },
    );
  }

  settings(): Observable<AdminSystemSettings> {
    return this.http.get<AdminSystemSettings>(`${this.adminUrl}/settings`);
  }

  resetCredentials(userId: string, reason: string): Observable<{
    userId: string;
    email: string;
    temporaryPassword: string;
    passwordChangeRequired: boolean;
  }> {
    return this.http.post<{
      userId: string;
      email: string;
      temporaryPassword: string;
      passwordChangeRequired: boolean;
    }>(`${this.adminUrl}/members/${encodeURIComponent(userId)}/reset-credentials`, { reason });
  }

  private createParams(values: object): HttpParams {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(values)) {
      if ((typeof value === 'string' && value !== '')
        || typeof value === 'number') {
        params = params.set(key, value);
      }
    }

    return params;
  }
}
