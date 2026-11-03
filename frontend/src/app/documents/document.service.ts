import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface DocumentItem {
  id: string;
  title: string;
  description: string | null;
  category: string;
  originalFileName: string;
  sizeBytes: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

@Injectable({ providedIn: 'root' })
export class DocumentService {
  private readonly http = inject(HttpClient);
  private readonly documentsUrl = `${environment.apiBaseUrl}/documents`;

  list(): Observable<DocumentItem[]> {
    return this.http.get<DocumentItem[]>(this.documentsUrl);
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
}
