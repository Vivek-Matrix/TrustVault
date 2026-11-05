import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface AdminMember {
  userId: string;
  email: string;
  roles: string[];
  isSuspended: boolean;
  createdAtUtc: string;
}

export interface CreatedMember {
  userId: string;
  email: string;
  role: 'Viewer';
  temporaryPassword: string;
  passwordChangeRequired: boolean;
}

export interface TemporaryCredentials {
  userId: string;
  email: string;
  temporaryPassword: string;
  passwordChangeRequired: boolean;
}

@Injectable({ providedIn: 'root' })
export class AdminMembersService {
  private readonly http = inject(HttpClient);
  private readonly membersUrl = `${environment.apiBaseUrl}/admin/members`;

  list(): Observable<AdminMember[]> {
    return this.http.get<AdminMember[]>(this.membersUrl);
  }

  create(email: string): Observable<CreatedMember> {
    return this.http.post<CreatedMember>(this.membersUrl, { email });
  }

  remove(userId: string): Observable<void> {
    return this.http.delete<void>(`${this.membersUrl}/${userId}`);
  }

  resetCredentials(userId: string, reason: string): Observable<TemporaryCredentials> {
    return this.http.post<TemporaryCredentials>(
      `${this.membersUrl}/${encodeURIComponent(userId)}/reset-credentials`,
      { reason },
    );
  }
}
