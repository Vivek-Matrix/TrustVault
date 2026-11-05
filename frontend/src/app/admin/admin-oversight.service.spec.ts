import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { AdminOversightService } from './admin-oversight.service';

describe('AdminOversightService', () => {
  let service: AdminOversightService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AdminOversightService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends audit investigation filters to the administrator API', () => {
    service.audit({
      user: 'member@example.test',
      action: 'document.download',
      outcome: 'DENIED',
      page: 2,
      pageSize: 25,
    }).subscribe();

    const request = http.expectOne((candidate) =>
      candidate.url === `${environment.apiBaseUrl}/admin/audit`
      && candidate.params.get('user') === 'member@example.test'
      && candidate.params.get('action') === 'document.download'
      && candidate.params.get('outcome') === 'DENIED'
      && candidate.params.get('page') === '2'
      && candidate.params.get('pageSize') === '25',
    );
    expect(request.request.method).toBe('GET');
    request.flush({ page: 2, pageSize: 25, totalCount: 0, items: [] });
  });

  it('includes the required reason when an administrator revokes a permission', () => {
    service.revokePermission('document-id', 'member-id', 'Incident response review')
      .subscribe();

    const request = http.expectOne(
      `${environment.apiBaseUrl}/admin/documents/document-id/permissions/member-id`,
    );
    expect(request.request.method).toBe('DELETE');
    expect(request.request.body).toEqual({ reason: 'Incident response review' });
    request.flush(null);
  });

  it('posts the credential reset reason and receives a one-time temporary password', () => {
    service.resetCredentials('member-id', 'Compromised password reported').subscribe();

    const request = http.expectOne(
      `${environment.apiBaseUrl}/admin/members/member-id/reset-credentials`,
    );
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ reason: 'Compromised password reported' });
    request.flush({
      userId: 'member-id',
      email: 'member@example.test',
      role: 'Viewer',
      temporaryPassword: 'OneTimePassword123!',
      passwordChangeRequired: true,
    });
  });
});
