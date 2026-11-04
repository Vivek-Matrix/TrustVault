import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { By } from '@angular/platform-browser';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { AuthSessionStore } from '../auth/auth-session.store';
import { bearerTokenInterceptor } from '../auth/bearer-token.interceptor';
import { DocumentsPanelComponent } from './documents-panel.component';
import { DocumentTextEditorComponent } from './document-text-editor.component';
import { DocumentGrantItem, DocumentItem } from './document.service';

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
    isOwner: true,
    permission: null,
    scanStatus: 'CLEAN',
    scanAttemptCount: 1,
    lastScanAtUtc: '2026-11-03T00:00:00Z',
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

  it('loads and displays document audit events from the Audits icon button', () => {
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    fixture.detectChanges();

    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([documentItem]);
    fixture.detectChanges();

    const auditsButton = fixture.nativeElement.querySelector(
      '.audits-button',
    ) as HTMLButtonElement;
    expect(auditsButton.getAttribute('aria-label')).toBe('Audits');
    expect(auditsButton.textContent.trim()).toBe('◷');
    auditsButton.click();

    const request = http.expectOne(
      `${environment.apiBaseUrl}/documents/${documentItem.id}/audits`,
    );
    expect(request.request.method).toBe('GET');
    request.flush([
      {
        id: 'audit-1',
        actorEmail: 'owner@example.test',
        activity: 'CREATED',
        result: 'SUCCESS',
        timestampUtc: '2026-11-03T10:15:00Z',
      },
    ]);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Document activity');
    expect(fixture.nativeElement.textContent).toContain('Created');
    expect(fixture.nativeElement.textContent).toContain('owner@example.test');
    expect(fixture.nativeElement.textContent).toContain('Successful');
    expect(fixture.nativeElement.querySelector('.audit-item time')?.getAttribute('datetime'))
      .toBe('2026-11-03T10:15:00Z');
    fixture.destroy();
  });

  it('warns before removing a document and removes it after confirmation', () => {
    const confirmSpy = spyOn(globalThis, 'confirm').and.returnValue(true);
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    fixture.detectChanges();

    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([documentItem]);
    fixture.detectChanges();
    const removeButton = fixture.nativeElement.querySelector(
      '.remove-button',
    ) as HTMLButtonElement;
    removeButton.click();

    expect(confirmSpy).toHaveBeenCalledOnceWith(
      'Remove "Test document"? This will delete the document and revoke all access grants, so everyone you shared it with will lose access. This cannot be undone.',
    );
    const request = http.expectOne(
      `${environment.apiBaseUrl}/documents/${documentItem.id}`,
    );
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(fixture.componentInstance.documents()).toEqual([]);
    expect(fixture.componentInstance.message()).toContain('shared access was revoked');
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
    expect(component.message()).toBe('test.pdf was uploaded and passed the malware scan.');
    fixture.destroy();
  });

  it('blocks actions and offers a retry for an owner when scanning failed', () => {
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    fixture.detectChanges();
    const failedDocument = {
      ...documentItem,
      scanStatus: 'SCAN_FAILED' as const,
      scanAttemptCount: 1,
    };
    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([failedDocument]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.download-button').disabled).toBeTrue();
    expect(fixture.nativeElement.querySelector('.edit-button')).toBeNull();
    const retryButton = fixture.nativeElement.querySelector(
      '.retry-scan-button',
    ) as HTMLButtonElement;
    expect(retryButton).not.toBeNull();
    retryButton.click();

    const retryRequest = http.expectOne(
      `${environment.apiBaseUrl}/documents/${failedDocument.id}/scan/retry`,
    );
    expect(retryRequest.request.method).toBe('POST');
    retryRequest.flush({
      scanStatus: 'CLEAN',
      scanAttemptCount: 2,
      lastScanAtUtc: '2026-11-04T00:00:00Z',
    });
    fixture.detectChanges();

    expect(fixture.componentInstance.documents()[0].scanStatus).toBe('CLEAN');
    expect(fixture.nativeElement.querySelector('.download-button').disabled).toBeFalse();
    fixture.destroy();
  });

  it('accepts TXT and Word files for upload and offers the text editor', () => {
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([]);

    const fileInput = fixture.nativeElement.querySelector(
      '#document-file',
    ) as HTMLInputElement;
    expect(fileInput.accept).toContain('.txt');
    expect(fileInput.accept).toContain('.doc');
    expect(fileInput.accept).toContain('.docx');

    for (const file of [
      new File(['plain text'], 'notes.txt', { type: 'text/plain' }),
      new File(['word data'], 'report.doc', { type: 'application/msword' }),
      new File(['word data'], 'report.docx', {
        type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
      }),
    ]) {
      selectFile(fixture.nativeElement, file);
      expect(component.selectedFile()).toBe(file);
      expect(component.error()).toBeNull();
    }

    component.documents.set([
      { ...documentItem, originalFileName: 'notes.txt' },
      { ...documentItem, id: 'document-2', originalFileName: 'report.docx' },
    ]);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.edit-button').length).toBe(2);
    fixture.destroy();
  });

  it('edits TXT content and keeps the file actions grouped in the document row', async () => {
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    fixture.detectChanges();
    const textDocument: DocumentItem = {
      ...documentItem,
      originalFileName: 'notes.txt',
      sizeBytes: 16,
    };
    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([textDocument]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.document-actions')).not.toBeNull();
    (fixture.nativeElement.querySelector('.edit-button') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();

    http
      .expectOne(`${environment.apiBaseUrl}/documents/${textDocument.id}/editable-text`)
      .flush({ text: 'Original text' });
    http
      .expectOne(`${environment.apiBaseUrl}/documents/${textDocument.id}/versions`)
      .flush([]);
    await fixture.whenStable();
    fixture.detectChanges();

    const textEditor = fixture.debugElement.query(
      By.directive(DocumentTextEditorComponent),
    ).componentInstance as DocumentTextEditorComponent;
    expect(textEditor.text()).withContext(
      `loading=${textEditor.loading()} error=${textEditor.error()}`,
    ).toBe('Original text');
    const textArea = fixture.nativeElement.querySelector(
      '#document-text-content',
    ) as HTMLTextAreaElement;
    expect(textArea.value).toBe('Original text');
    textArea.value = 'Updated text';
    textArea.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    (fixture.nativeElement.querySelector(
      '.text-editor-dialog .primary-button',
    ) as HTMLButtonElement).click();

    const updateRequest = http.expectOne(
      `${environment.apiBaseUrl}/documents/${textDocument.id}/editable-text`,
    );
    expect(updateRequest.request.method).toBe('PUT');
    expect(updateRequest.request.body).toEqual({ text: 'Updated text' });
    const updatedDocument = { ...textDocument, sizeBytes: 12 };
    updateRequest.flush(updatedDocument);
    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([updatedDocument]);
    http
      .expectOne(`${environment.apiBaseUrl}/documents/${textDocument.id}/versions`)
      .flush([]);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('notes.txt');
    expect(fixture.nativeElement.textContent).toContain('The text file was saved');
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

  it('grants access by email and reloads the active grants', () => {
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    fixture.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([documentItem]);
    fixture.detectChanges();

    const manageButton = fixture.nativeElement.querySelector(
      '.access-button',
    ) as HTMLButtonElement;
    manageButton.click();
    const grantsRequest = http.expectOne(
      `${environment.apiBaseUrl}/documents/${documentItem.id}/grants`,
    );
    expect(grantsRequest.request.method).toBe('GET');
    grantsRequest.flush([]);
    fixture.detectChanges();

    const emailInput = fixture.nativeElement.querySelector(
      '.grant-form input[name="email"]',
    ) as HTMLInputElement;
    emailInput.value = 'recipient@example.test';
    emailInput.dispatchEvent(new Event('input', { bubbles: true }));
    const permissionSelect = fixture.nativeElement.querySelector(
      '.grant-form select[name="permission"]',
    ) as HTMLSelectElement;
    expect(Array.from(permissionSelect.options).map((option) => option.value)).toEqual([
      'READ',
      'EDIT',
    ]);
    permissionSelect.value = 'READ';
    const grantForm = fixture.nativeElement.querySelector('.grant-form') as HTMLFormElement;
    grantForm.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));

    const grantRequest = http.expectOne(
      `${environment.apiBaseUrl}/documents/${documentItem.id}/grants`,
    );
    expect(grantRequest.request.method).toBe('POST');
    expect(grantRequest.request.body).toEqual({
      email: 'recipient@example.test',
      permission: 'READ',
    });
    const grant: DocumentGrantItem = {
      userId: 'recipient-1',
      email: 'recipient@example.test',
      permission: 'READ',
      grantedAtUtc: '2026-11-03T00:00:00Z',
    };
    grantRequest.flush(grant);

    const refreshedGrants = http.expectOne(
      `${environment.apiBaseUrl}/documents/${documentItem.id}/grants`,
    );
    refreshedGrants.flush([grant]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('recipient@example.test');
    expect(fixture.nativeElement.querySelector('.revoke-button')).not.toBeNull();
    expect(fixture.componentInstance.message()).toBe('Document access was saved.');
    fixture.destroy();
  });

  it('confirms and revokes an active share, then refreshes documents and grants', () => {
    const fixture = TestBed.createComponent(DocumentsPanelComponent);
    fixture.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([documentItem]);
    fixture.detectChanges();

    fixture.nativeElement.querySelector('.access-button').click();
    http
      .expectOne(`${environment.apiBaseUrl}/documents/${documentItem.id}/grants`)
      .flush([]);

    const grant: DocumentGrantItem = {
      userId: 'recipient-1',
      email: 'recipient@example.test',
      permission: 'EDIT',
      grantedAtUtc: '2026-11-03T00:00:00Z',
    };
    fixture.componentInstance.grants.set([grant]);
    fixture.detectChanges();
    spyOn(globalThis, 'confirm').and.returnValue(true);
    (fixture.nativeElement.querySelector('.revoke-button') as HTMLButtonElement).click();

    const revokeRequest = http.expectOne(
      `${environment.apiBaseUrl}/documents/${documentItem.id}/grants/${grant.userId}`,
    );
    expect(revokeRequest.request.method).toBe('DELETE');
    revokeRequest.flush(null);

    http.expectOne(`${environment.apiBaseUrl}/documents`).flush([documentItem]);
    http
      .expectOne(`${environment.apiBaseUrl}/documents/${documentItem.id}/grants`)
      .flush([]);
    fixture.detectChanges();
    expect(fixture.componentInstance.message()).toBe(
      'Access for recipient@example.test was revoked.',
    );
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
