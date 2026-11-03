import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { AuthSessionStore } from '../auth/auth-session.store';
import { bearerTokenInterceptor } from '../auth/bearer-token.interceptor';
import { DocumentsPanelComponent } from './documents-panel.component';
import { DocumentItem } from './document.service';

describe('DocumentsPanelComponent', () => {
  let http: HttpTestingController;
  let sessionStore: AuthSessionStore;

  const documentItem: DocumentItem = {
    id: 'document-1',
    title: 'Test document',
    description: null,
    category: 'Manual test',
    originalFileName: 'test.pdf',
    sizeBytes: 64,
    createdAtUtc: '2026-11-03T00:00:00Z',
    updatedAtUtc: '2026-11-03T00:00:00Z',
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [DocumentsPanelComponent],
      providers: [
        provideHttpClient(withInterceptors([bearerTokenInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    sessionStore = TestBed.inject(AuthSessionStore);
    sessionStore.setSession({
      accessToken: 'test-document-token',
      tokenType: 'Bearer',
      expiresAtUtc: '2026-11-03T00:15:00Z',
      userId: 'owner-1',
      email: 'owner@example.test',
      roles: ['Viewer'],
    });
  });

  afterEach(() => http.verify());

  it('loads the signed-in user document list with the bearer token', () => {
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    fixture.detectChanges();

    const request = http.expectOne(`${environment.apiBaseUrl}/documents`);
    expect(request.request.method).toBe('GET');
    expect(request.request.headers.get('Authorization')).toBe('Bearer test-document-token');
    request.flush([documentItem]);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Test document');
    expect(fixture.nativeElement.textContent).toContain('test.pdf');
    fixture.destroy();
  });

  it('uploads a PDF and adds the returned document to the list', () => {
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([]);

    component.form.setValue({ title: 'Test document', category: 'Manual test' });
    const file = new File(['%PDF-1.7'], 'test.pdf', { type: 'application/pdf' });
    selectFile(fixture.nativeElement, file);
    component.uploadDocument();

    const request = http.expectOne(`${environment.apiBaseUrl}/documents`);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Authorization')).toBe('Bearer test-document-token');
    expect(request.request.body).toBeInstanceOf(FormData);
    const formData = request.request.body as FormData;
    expect(formData.get('Title')).toBe('Test document');
    expect(formData.get('Category')).toBe('Manual test');
    expect((formData.get('File') as File).name).toBe('test.pdf');
    request.flush(documentItem);
    fixture.detectChanges();

    expect(component.documents()).toEqual([documentItem]);
    expect(component.message()).toBe('Your PDF was uploaded.');
    fixture.destroy();
  });

  it('does not send a file larger than 10 MiB', () => {
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    fixture.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([]);

    const file = new File([new Uint8Array(10 * 1024 * 1024 + 1)], 'large.pdf', {
      type: 'application/pdf',
    });
    selectFile(fixture.nativeElement, file);
    fixture.detectChanges();

    expect(fixture.componentInstance.selectedFile()).toBeNull();
    expect(fixture.componentInstance.error()).toContain('between 1 byte and 10 MiB');
    const uploadButton = fixture.nativeElement.querySelector(
      '.primary-button',
    ) as HTMLButtonElement;
    expect(uploadButton.disabled).toBeTrue();
    fixture.destroy();
  });

  it('downloads the selected document through the authenticated API', () => {
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([documentItem]);

    const createObjectUrl = spyOn(URL, 'createObjectURL').and.returnValue('blob:test-file');
    const revokeObjectUrl = spyOn(URL, 'revokeObjectURL');
    const click = spyOn(HTMLAnchorElement.prototype, 'click');
    component.downloadDocument(documentItem);

    const request = http.expectOne(
      `${environment.apiBaseUrl}/documents/${documentItem.id}/content`,
    );
    expect(request.request.method).toBe('GET');
    expect(request.request.headers.get('Authorization')).toBe('Bearer test-document-token');
    request.flush(new Blob(['test pdf'], { type: 'application/pdf' }));

    expect(createObjectUrl).toHaveBeenCalled();
    expect(click).toHaveBeenCalled();
    expect(revokeObjectUrl).toHaveBeenCalledWith('blob:test-file');
    expect(component.message()).toBe('Downloaded test.pdf.');
    fixture.destroy();
  });

  it('shows a useful error when the document list cannot be loaded', () => {
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    fixture.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/documents`).error(new ProgressEvent('error'));
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).toContain('API is running');
    expect(fixture.nativeElement.textContent).toContain('Check that the API is running');
    fixture.destroy();
  });

  function selectFile(root: HTMLElement, file: File): void {
    const input = root.querySelector<HTMLInputElement>('#document-file');
    if (!input) {
      throw new Error('Document file input was not rendered.');
    }

    Object.defineProperty(input, 'files', { configurable: true, value: [file] });
    input.dispatchEvent(new Event('change'));
  }
});
