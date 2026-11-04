import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface DocumentFilters {
  search?: string;
  category?: string;
  owner?: string;
  createdFrom?: string;
  createdTo?: string;
}

export interface DocumentItem {
  id: string;
  title: string;
  description: string | null;
  category: string;
  originalFileName: string;
  sizeBytes: number;
  createdAtUtc: string;
  updatedAtUtc: string;
  isOwner: boolean;
  permission: 'READ' | 'EDIT' | null;
  scanStatus: 'PENDING_SCAN' | 'SCANNING' | 'CLEAN' | 'REJECTED' | 'SCAN_FAILED';
  scanAttemptCount: number;
  lastScanAtUtc: string | null;
}

export interface DocumentGrantItem {
  userId: string;
  email: string;
  permission: 'READ' | 'EDIT';
  grantedAtUtc: string;
}

export interface DocumentVersionItem {
  versionNumber: number;
  originalFileName: string;
  sizeBytes: number;
  createdAtUtc: string;
  scanStatus: 'PENDING_SCAN' | 'SCANNING' | 'CLEAN' | 'REJECTED' | 'SCAN_FAILED';
  scanAttemptCount: number;
  lastScanAtUtc: string | null;
}

export interface DocumentScanStatusItem {
  scanStatus: 'PENDING_SCAN' | 'SCANNING' | 'CLEAN' | 'REJECTED' | 'SCAN_FAILED';
  scanAttemptCount: number;
  lastScanAtUtc: string | null;
}

export interface DocumentAuditItem {
  id: string;
  actorEmail: string | null;
  activity: 'CREATED' | 'READ' | 'EDITED' | 'SCANNED';
  result: 'SUCCESS' | 'DENIED';
  timestampUtc: string;
}

export interface DocumentEditableText {
  text: string;
}

@Injectable({ providedIn: 'root' })
export class DocumentService {
  private readonly http = inject(HttpClient);
  private readonly documentsUrl = `${environment.apiBaseUrl}/documents`;

  list(filters: DocumentFilters = {}): Observable<DocumentItem[]> {
    let params = new HttpParams();
    if (filters.search) {
      params = params.set('search', filters.search);
    }
    if (filters.category) {
      params = params.set('category', filters.category);
    }
    if (filters.owner) {
      params = params.set('owner', filters.owner);
    }
    if (filters.createdFrom) {
      params = params.set('createdFrom', filters.createdFrom);
    }
    if (filters.createdTo) {
      params = params.set('createdTo', filters.createdTo);
    }

    return this.http.get<DocumentItem[]>(this.documentsUrl, { params });
  }

  upload(title: string, category: string, file: File): Observable<DocumentItem> {
    const formData = new FormData();
    formData.append('Title', title);
    formData.append('Category', category);
    formData.append('File', file, file.name);
    return this.http.post<DocumentItem>(this.documentsUrl, formData);
  }

  download(id: string): Observable<Blob> {
    return this.http.get(`${this.documentsUrl}/${encodeURIComponent(id)}/content`, {
      responseType: 'blob',
    });
  }

  retryScan(documentId: string): Observable<DocumentScanStatusItem> {
    return this.http.post<DocumentScanStatusItem>(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}/scan/retry`,
      {},
    );
  }

  retryVersionScan(documentId: string, versionNumber: number): Observable<DocumentScanStatusItem> {
    return this.http.post<DocumentScanStatusItem>(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}/versions/${versionNumber}/scan/retry`,
      {},
    );
  }

  listGrants(documentId: string): Observable<DocumentGrantItem[]> {
    return this.http.get<DocumentGrantItem[]>(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}/grants`,
    );
  }

  listAudits(documentId: string): Observable<DocumentAuditItem[]> {
    return this.http.get<DocumentAuditItem[]>(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}/audits`,
    );
  }

  grantAccess(
    documentId: string,
    email: string,
    permission: 'READ' | 'EDIT',
  ): Observable<DocumentGrantItem> {
    return this.http.post<DocumentGrantItem>(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}/grants`,
      { email, permission },
    );
  }

  revokeAccess(documentId: string, userId: string): Observable<void> {
    return this.http.delete<void>(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}/grants/${encodeURIComponent(userId)}`,
    );
  }

  deleteDocument(documentId: string): Observable<void> {
    return this.http.delete<void>(`${this.documentsUrl}/${encodeURIComponent(documentId)}`);
  }

  updateMetadata(
    documentId: string,
    metadata: { title: string; description: string; category: string },
  ): Observable<DocumentItem> {
    return this.http.put<DocumentItem>(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}`,
      metadata,
    );
  }

  replaceContent(
    documentId: string,
    file: File,
    purgePreviousVersions = false,
  ): Observable<DocumentItem> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    formData.append('purgePreviousVersions', String(purgePreviousVersions));
    return this.http.put<DocumentItem>(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}/content`,
      formData,
    );
  }

  getEditableText(documentId: string): Observable<DocumentEditableText> {
    return this.http.get<DocumentEditableText>(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}/editable-text`,
    );
  }

  updateEditableText(documentId: string, text: string): Observable<DocumentItem> {
    return this.http.put<DocumentItem>(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}/editable-text`,
      { text },
    );
  }

  listVersions(documentId: string): Observable<DocumentVersionItem[]> {
    return this.http.get<DocumentVersionItem[]>(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}/versions`,
    );
  }

  downloadVersion(documentId: string, versionNumber: number): Observable<Blob> {
    return this.http.get(
      `${this.documentsUrl}/${encodeURIComponent(documentId)}/versions/${versionNumber}/content`,
      { responseType: 'blob' },
    );
  }
}
