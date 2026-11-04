import { HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, OnDestroy, OnInit, ViewChild, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PDFDocument } from 'pdf-lib';
import {
  GlobalWorkerOptions,
  getDocument,
  type PDFDocumentLoadingTask,
  type PDFDocumentProxy,
  type RenderTask,
  RenderingCancelledException,
} from 'pdfjs-dist';
import { finalize } from 'rxjs';
import { DocumentItem, DocumentService, DocumentVersionItem } from './document.service';

type EditorTool = 'text' | 'draw' | 'replace' | 'redact';
interface Point {
  x: number;
  y: number;
}

interface SelectionPreview {
  left: number;
  top: number;
  width: number;
  height: number;
}
type PageOperation =
  | { kind: 'text'; point: Point; text: string }
  | { kind: 'draw'; points: Point[] }
  | { kind: 'replace'; x: number; y: number; width: number; height: number; text: string }
  | { kind: 'redact'; x: number; y: number; width: number; height: number };

const maximumFileBytes = 10 * 1024 * 1024;
const maximumPages = 100;

GlobalWorkerOptions.workerSrc = new URL(
  'pdfjs-dist/build/pdf.worker.min.mjs',
  import.meta.url,
).toString();

@Component({
  selector: 'app-document-editor',
  imports: [FormsModule],
  template: `
    <div class="editor-backdrop" role="presentation">
      <section class="editor-dialog" role="dialog" aria-modal="true" aria-labelledby="editor-title">
        <div class="editor-heading">
          <div>
            <p class="eyebrow">DOCUMENT EDITOR</p>
            <h2 id="editor-title">{{ item().title }}</h2>
          </div>
          <button class="secondary-button" type="button" (click)="closed.emit()">Close</button>
        </div>

        @if (error()) {
          <p class="editor-error" role="alert">{{ error() }}</p>
        }
        @if (message()) {
          <p class="editor-message" role="status">{{ message() }}</p>
        }

        <div class="editor-layout">
          <aside class="editor-sidebar">
            <h3>Document details</h3>
            <label for="edit-title">Title</label>
            <input id="edit-title" [(ngModel)]="title" maxlength="200" />
            <label for="edit-description">Description</label>
            <textarea id="edit-description" [(ngModel)]="description" maxlength="2000"></textarea>
            <label for="edit-category">Category</label>
            <input id="edit-category" [(ngModel)]="category" maxlength="100" />
            <button
              class="primary-button"
              type="button"
              [disabled]="savingMetadata() || !title.trim() || !category.trim()"
              (click)="saveMetadata()">
              {{ savingMetadata() ? 'Saving...' : 'Save details' }}
            </button>

            <h3>Replace PDF</h3>
            <p class="editor-hint">Upload a revised PDF to replace the current file.</p>
            <input
              type="file"
              accept=".pdf,application/pdf"
              aria-label="Choose a replacement PDF"
              (change)="onReplacementSelected($event)" />
            <button
              class="secondary-button"
              type="button"
              [disabled]="!replacementFile() || replacing()"
              (click)="replacePdf()">
              {{ replacing() ? 'Replacing...' : 'Replace PDF' }}
            </button>

            <h3>Previous versions</h3>
            @if (versionsLoading()) {
              <p class="editor-hint">Loading versions...</p>
            } @else if (versions().length === 0) {
              <p class="editor-hint">Previous files appear here after replacement.</p>
            } @else {
              <ul class="version-list">
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
          </aside>

          <div class="pdf-editor">
            <p class="flatten-warning">
              Saving page edits creates an image-based PDF. Text search, selection, links, forms,
              signatures, and accessibility features are removed. Review the saved file before
              sharing it. Redaction removes prior versions from TrustVault, but cannot erase
              downloaded copies or external backups.
            </p>
            <div class="tool-row">
              <button type="button" [class.active]="tool() === 'text'" (click)="selectTool('text')">
                Add text
              </button>
              <button type="button" [class.active]="tool() === 'draw'" (click)="selectTool('draw')">
                Draw
              </button>
              <button type="button" [class.active]="tool() === 'replace'" (click)="selectTool('replace')">
                Replace area
              </button>
              <button type="button" [class.active]="tool() === 'redact'" (click)="selectTool('redact')">
                Redact area
              </button>
            </div>
            <p class="editor-hint">
              Replace area covers the selected rectangle in white and adds replacement text.
              Redact area permanently covers the selected rectangle in black in the flattened output.
            </p>
            <label for="editor-text">Text to add or use for replacement</label>
            <input id="editor-text" [(ngModel)]="overlayText" maxlength="300" />

            @if (loading()) {
              <p class="editor-hint">Loading the PDF...</p>
            }
            <div class="page-controls" [hidden]="!pdfReady()">
              <button type="button" (click)="movePage(-1)" [disabled]="pagePosition() === 0">
                Move page left
              </button>
              <span>Page {{ pagePosition() + 1 }} of {{ pageOrder().length }}</span>
              <button
                type="button"
                (click)="movePage(1)"
                [disabled]="pagePosition() >= pageOrder().length - 1">
                Move page right
              </button>
              <button
                type="button"
                class="danger-button"
                (click)="removeCurrentPage()"
                [disabled]="pageOrder().length <= 1">
                Remove page
              </button>
            </div>
            <div class="canvas-wrap" [hidden]="!pdfReady()">
              <canvas
                #pageCanvas
                class="pdf-page"
                (pointerdown)="onPointerDown($event)"
                (pointermove)="onPointerMove($event)"
                (pointerup)="onPointerUp($event)"
                (pointercancel)="onPointerUp($event)">
              </canvas>
              @if (selectionPreview(); as preview) {
                <div
                  class="selection-preview"
                  [style.left.%]="preview.left * 100"
                  [style.top.%]="preview.top * 100"
                  [style.width.%]="preview.width * 100"
                  [style.height.%]="preview.height * 100">
                </div>
              }
            </div>
            <button
              class="primary-button save-pdf-button"
              type="button"
              [hidden]="!pdfReady()"
              [disabled]="savingPdf() || !hasPageChanges()"
              (click)="savePageEdits()">
              {{ savingPdf() ? 'Creating secure PDF...' : 'Save edited PDF' }}
            </button>
          </div>
        </div>
      </section>
    </div>
  `,
  styleUrl: './document-editor.component.scss',
})
export class DocumentEditorComponent implements OnInit, OnDestroy {
  readonly item = input.required<DocumentItem>();
  readonly closed = output<void>();
  readonly saved = output<DocumentItem>();

  @ViewChild('pageCanvas', { static: true })
  private pageCanvas?: ElementRef<HTMLCanvasElement>;

  private readonly documentService = inject(DocumentService);
  private pdf: PDFDocumentProxy | null = null;
  private loadingTask: PDFDocumentLoadingTask | null = null;
  private renderTask: RenderTask | null = null;
  private readonly operations = new Map<number, PageOperation[]>();
  private drawingPoints: Point[] = [];
  private selectionStart: Point | null = null;
  private renderSequence = 0;

  title = '';
  description = '';
  category = '';
  overlayText = '';
  readonly tool = signal<EditorTool>('text');
  readonly loading = signal(true);
  readonly pdfReady = signal(false);
  readonly savingMetadata = signal(false);
  readonly savingPdf = signal(false);
  readonly replacing = signal(false);
  readonly replacementFile = signal<File | null>(null);
  readonly pageOrder = signal<number[]>([]);
  readonly pagePosition = signal(0);
  readonly selectionPreview = signal<SelectionPreview | null>(null);
  readonly versions = signal<DocumentVersionItem[]>([]);
  readonly versionsLoading = signal(true);
  readonly downloadingVersion = signal<number | null>(null);
  readonly retryingVersion = signal<number | null>(null);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    this.title = this.item().title;
    this.description = this.item().description ?? '';
    this.category = this.item().category;
    this.loadVersions();
    this.documentService.download(this.item().id).subscribe({
      next: async (blob) => {
        try {
          const bytes = new Uint8Array(await blob.arrayBuffer());
          const loadingTask = getDocument({ data: bytes.slice() });
          this.loadingTask = loadingTask;
          const pdf = await loadingTask.promise;
          if (pdf.numPages > maximumPages) {
            await loadingTask.destroy();
            this.error.set(`The editor supports up to ${maximumPages} pages at a time.`);
            this.loading.set(false);
            return;
          }

          this.pdf = pdf;
          this.pageOrder.set(Array.from({ length: pdf.numPages }, (_, index) => index));
          this.pdfReady.set(true);
          this.loading.set(false);
          await this.renderCurrentPage();
        } catch {
          this.error.set('This PDF could not be opened for editing. Try another PDF.');
          this.loading.set(false);
        }
      },
      error: (error: unknown) => {
        this.error.set(this.describeError(error, 'open the PDF'));
        this.loading.set(false);
      },
    });
  }

  ngOnDestroy(): void {
    this.renderTask?.cancel();
    void this.loadingTask?.destroy();
  }

  selectTool(tool: EditorTool): void {
    this.tool.set(tool);
    this.error.set(null);
  }

  async saveMetadata(): Promise<void> {
    if (!this.title.trim() || !this.category.trim() || this.savingMetadata()) {
      return;
    }

    this.savingMetadata.set(true);
    this.error.set(null);
    this.documentService
      .updateMetadata(this.item().id, {
        title: this.title.trim(),
        description: this.description.trim(),
        category: this.category.trim(),
      })
      .pipe(finalize(() => this.savingMetadata.set(false)))
      .subscribe({
        next: (updated) => {
          this.saved.emit(updated);
          this.message.set('Document details were saved.');
        },
        error: (error: unknown) => this.error.set(this.describeError(error, 'save document details')),
      });
  }

  onReplacementSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    this.replacementFile.set(null);
    this.error.set(null);
    if (!file) {
      return;
    }
    if (!file.name.toLowerCase().endsWith('.pdf') || file.size > maximumFileBytes) {
      this.error.set('Choose a PDF file up to 10 MiB.');
      input.value = '';
      return;
    }
    this.replacementFile.set(file);
  }

  replacePdf(): void {
    const file = this.replacementFile();
    if (!file || this.replacing()) {
      return;
    }
    this.replacing.set(true);
    this.error.set(null);
    this.documentService
      .replaceContent(this.item().id, file)
      .pipe(finalize(() => this.replacing.set(false)))
      .subscribe({
        next: (updated) => {
          this.replacementFile.set(null);
          this.saved.emit(updated);
          this.message.set('The PDF was replaced. The previous version is available below.');
          this.loadVersions();
          this.loadCurrentPdf();
          const input = globalThis.document.querySelector<HTMLInputElement>(
            'input[aria-label="Choose a replacement PDF"]',
          );
          if (input) {
            input.value = '';
          }
        },
        error: (error: unknown) => this.error.set(this.describeError(error, 'replace the PDF')),
      });
  }

  movePage(direction: -1 | 1): void {
    const pages = [...this.pageOrder()];
    const position = this.pagePosition();
    const target = position + direction;
    if (target < 0 || target >= pages.length) {
      return;
    }
    [pages[position], pages[target]] = [pages[target], pages[position]];
    this.pageOrder.set(pages);
    this.pagePosition.set(target);
    void this.renderCurrentPage();
  }

  removeCurrentPage(): void {
    const pages = [...this.pageOrder()];
    if (pages.length <= 1) {
      return;
    }
    pages.splice(this.pagePosition(), 1);
    this.pageOrder.set(pages);
    this.pagePosition.set(Math.min(this.pagePosition(), pages.length - 1));
    void this.renderCurrentPage();
  }

  onPointerDown(event: PointerEvent): void {
    if (!this.pdfReady() || event.button !== 0) {
      return;
    }
    const point = this.getPoint(event);
    if (!point) {
      return;
    }
    if (this.tool() === 'text') {
      const text = this.overlayText.trim();
      if (!text) {
        this.error.set('Enter text before choosing where to add it.');
        return;
      }
      this.addOperation({ kind: 'text', point, text });
      return;
    }
    this.selectionStart = point;
    this.drawingPoints = [point];
    this.pageCanvas?.nativeElement.setPointerCapture(event.pointerId);
  }

  onPointerMove(event: PointerEvent): void {
    if (!this.selectionStart) {
      return;
    }
    const point = this.getPoint(event);
    if (!point) {
      return;
    }
    if (this.tool() === 'draw') {
      this.drawingPoints.push(point);
      this.drawDraft();
    } else {
      this.selectionPreview.set({
        left: Math.min(this.selectionStart.x, point.x),
        top: Math.min(this.selectionStart.y, point.y),
        width: Math.abs(point.x - this.selectionStart.x),
        height: Math.abs(point.y - this.selectionStart.y),
      });
    }
  }

  onPointerUp(event: PointerEvent): void {
    if (!this.selectionStart) {
      return;
    }
    const end = this.getPoint(event);
    const start = this.selectionStart;
    this.selectionStart = null;
    this.selectionPreview.set(null);
    if (!end) {
      return;
    }

    const tool = this.tool();
    if (tool === 'draw') {
      if (this.drawingPoints.length > 1) {
        this.addOperation({ kind: 'draw', points: [...this.drawingPoints] });
      }
      this.drawingPoints = [];
      return;
    }

    const rectangle = {
      x: Math.min(start.x, end.x),
      y: Math.min(start.y, end.y),
      width: Math.abs(end.x - start.x),
      height: Math.abs(end.y - start.y),
    };
    if (rectangle.width < 0.01 || rectangle.height < 0.01) {
      void this.renderCurrentPage();
      return;
    }
    if (tool === 'redact') {
      this.addOperation({ kind: 'redact', ...rectangle });
    } else if (tool === 'replace') {
      const text = this.overlayText.trim();
      if (!text) {
        this.error.set('Enter replacement text before selecting an area.');
        void this.renderCurrentPage();
        return;
      }
      this.addOperation({ kind: 'replace', ...rectangle, text });
    }
  }

  async savePageEdits(): Promise<void> {
    if (!this.pdf || this.savingPdf()) {
      return;
    }
    this.savingPdf.set(true);
    this.error.set(null);
    try {
      const output = await PDFDocument.create();
      for (const pageIndex of this.pageOrder()) {
        const page = await this.pdf.getPage(pageIndex + 1);
        const baseViewport = page.getViewport({ scale: 1 });
        const renderScale = Math.min(
          1.5,
          1800 / Math.max(baseViewport.width, baseViewport.height),
        );
        const viewport = page.getViewport({ scale: renderScale });
        const canvas = globalThis.document.createElement('canvas');
        canvas.width = Math.ceil(viewport.width);
        canvas.height = Math.ceil(viewport.height);
        const context = canvas.getContext('2d');
        if (!context) {
          throw new Error('A canvas context could not be created.');
        }
        await page.render({ canvas, canvasContext: context, viewport }).promise;
        this.applyOperations(context, canvas.width, canvas.height, pageIndex);
        const imageBytes = this.jpegBytes(canvas);
        const image = await output.embedJpg(imageBytes);
        const outputPage = output.addPage([baseViewport.width, baseViewport.height]);
        outputPage.drawImage(image, {
          x: 0,
          y: 0,
          width: baseViewport.width,
          height: baseViewport.height,
        });
      }

      const savedBytes = await output.save();
      if (savedBytes.byteLength > maximumFileBytes) {
        throw new Error('The edited PDF exceeds the 10 MiB file limit.');
      }
      const fileBuffer = new ArrayBuffer(savedBytes.byteLength);
      new Uint8Array(fileBuffer).set(savedBytes);
      const file = new File([fileBuffer], this.item().originalFileName, {
        type: 'application/pdf',
      });
      const purgePreviousVersions = this.pageOrder().some((pageIndex) =>
        (this.operations.get(pageIndex) ?? []).some((operation) => operation.kind === 'redact'),
      );
      this.documentService
        .replaceContent(this.item().id, file, purgePreviousVersions)
        .pipe(finalize(() => this.savingPdf.set(false)))
        .subscribe({
          next: (updated) => {
            this.operations.clear();
            this.pageOrder.set(Array.from({ length: this.pdf?.numPages ?? 0 }, (_, index) => index));
            this.pagePosition.set(0);
            this.saved.emit(updated);
            this.message.set('Edited PDF saved as a flattened image-based file.');
            this.loadVersions();
            void this.reloadPdf(savedBytes);
          },
          error: (error: unknown) =>
            this.error.set(this.describeError(error, 'save the edited PDF')),
        });
    } catch (error) {
      this.savingPdf.set(false);
      this.error.set(
        error instanceof Error ? error.message : 'The edited PDF could not be created.',
      );
    }
  }

  hasPageChanges(): boolean {
    const originalCount = this.pdf?.numPages ?? 0;
    return this.operations.size > 0
      || this.pageOrder().length !== originalCount
      || this.pageOrder().some((page, index) => page !== index);
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
        next: (blob) => this.downloadBlob(blob, version.originalFileName),
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

  private async reloadPdf(bytes: Uint8Array): Promise<void> {
    this.renderTask?.cancel();
    await this.loadingTask?.destroy();
    this.loadingTask = getDocument({ data: bytes.slice() });
    this.pdf = await this.loadingTask.promise;
    this.operations.clear();
    this.pageOrder.set(Array.from({ length: this.pdf.numPages }, (_, index) => index));
    this.pagePosition.set(0);
    this.pdfReady.set(true);
    await this.renderCurrentPage();
  }

  private loadCurrentPdf(): void {
    this.documentService.download(this.item().id).subscribe({
      next: async (blob) => {
        try {
          await this.reloadPdf(new Uint8Array(await blob.arrayBuffer()));
        } catch {
          this.error.set('The new PDF was saved, but could not be reopened in the editor.');
        }
      },
      error: (error: unknown) =>
        this.error.set(this.describeError(error, 'reload the replaced PDF')),
    });
  }

  private async renderCurrentPage(): Promise<void> {
    const pdf = this.pdf;
    const canvas = this.pageCanvas?.nativeElement;
    const pageIndex = this.pageOrder()[this.pagePosition()];
    if (!pdf || !canvas || pageIndex === undefined) {
      return;
    }
    this.renderTask?.cancel();
    if (this.renderTask) {
      try {
        await this.renderTask.promise;
      } catch (error) {
        if (!(error instanceof RenderingCancelledException)) {
          throw error;
        }
      }
    }

    const sequence = ++this.renderSequence;
    const page = await pdf.getPage(pageIndex + 1);
    const viewport = page.getViewport({ scale: 1.4 });
    canvas.width = Math.ceil(viewport.width);
    canvas.height = Math.ceil(viewport.height);
    const context = canvas.getContext('2d');
    if (!context) {
      this.error.set('The PDF page could not be displayed.');
      return;
    }
    const task = page.render({ canvas, canvasContext: context, viewport });
    this.renderTask = task;
    try {
      await task.promise;
    } catch (error) {
      if (!(error instanceof RenderingCancelledException)) {
        throw error;
      }
      return;
    } finally {
      if (this.renderTask === task) {
        this.renderTask = null;
      }
    }
    if (sequence === this.renderSequence) {
      this.applyOperations(context, canvas.width, canvas.height, pageIndex);
    }
  }

  private addOperation(operation: PageOperation): void {
    const pageIndex = this.pageOrder()[this.pagePosition()];
    if (pageIndex === undefined) {
      return;
    }
    const current = this.operations.get(pageIndex) ?? [];
    this.operations.set(pageIndex, [...current, operation]);
    this.error.set(null);
    void this.renderCurrentPage();
  }

  private getPoint(event: PointerEvent): Point | null {
    const canvas = this.pageCanvas?.nativeElement;
    if (!canvas) {
      return null;
    }
    const bounds = canvas.getBoundingClientRect();
    return {
      x: Math.min(1, Math.max(0, (event.clientX - bounds.left) / bounds.width)),
      y: Math.min(1, Math.max(0, (event.clientY - bounds.top) / bounds.height)),
    };
  }

  private drawDraft(): void {
    const canvas = this.pageCanvas?.nativeElement;
    const context = canvas?.getContext('2d');
    if (!canvas || !context || this.drawingPoints.length < 2) {
      return;
    }
    const previous = this.drawingPoints[this.drawingPoints.length - 2];
    const current = this.drawingPoints[this.drawingPoints.length - 1];
    context.strokeStyle = '#d12b2b';
    context.lineWidth = Math.max(3, canvas.width * 0.004);
    context.lineCap = 'round';
    context.beginPath();
    context.moveTo(previous.x * canvas.width, previous.y * canvas.height);
    context.lineTo(current.x * canvas.width, current.y * canvas.height);
    context.stroke();
  }

  private applyOperations(
    context: CanvasRenderingContext2D,
    width: number,
    height: number,
    pageIndex: number,
  ): void {
    for (const operation of this.operations.get(pageIndex) ?? []) {
      if (operation.kind === 'text') {
        context.fillStyle = '#14253d';
        context.font = `bold ${Math.max(14, width * 0.025)}px sans-serif`;
        context.fillText(operation.text, operation.point.x * width, operation.point.y * height);
      } else if (operation.kind === 'draw') {
        if (operation.points.length < 2) {
          continue;
        }
        context.strokeStyle = '#d12b2b';
        context.lineWidth = Math.max(3, width * 0.004);
        context.lineCap = 'round';
        context.beginPath();
        context.moveTo(operation.points[0].x * width, operation.points[0].y * height);
        for (const point of operation.points.slice(1)) {
          context.lineTo(point.x * width, point.y * height);
        }
        context.stroke();
      } else {
        const x = operation.x * width;
        const y = operation.y * height;
        const boxWidth = operation.width * width;
        const boxHeight = operation.height * height;
        context.fillStyle = operation.kind === 'redact' ? '#000000' : '#ffffff';
        context.fillRect(x, y, boxWidth, boxHeight);
        if (operation.kind === 'replace') {
          context.fillStyle = '#14253d';
          context.font = `bold ${Math.max(14, width * 0.025)}px sans-serif`;
          const lines = this.wrapText(context, operation.text, boxWidth);
          lines.forEach((line, index) =>
            context.fillText(line, x + 3, y + Math.min(boxHeight, (index + 1) * width * 0.032)),
          );
        }
      }
    }
  }

  private wrapText(context: CanvasRenderingContext2D, text: string, maximumWidth: number): string[] {
    const lines: string[] = [];
    for (const paragraph of text.split(/\r?\n/)) {
      let line = '';
      for (const word of paragraph.split(/\s+/)) {
        const candidate = line ? `${line} ${word}` : word;
        if (line && context.measureText(candidate).width > maximumWidth - 6) {
          lines.push(line);
          line = word;
        } else {
          line = candidate;
        }
      }
      if (line) {
        lines.push(line);
      }
    }
    return lines;
  }

  private jpegBytes(canvas: HTMLCanvasElement): Uint8Array {
    const dataUrl = canvas.toDataURL('image/jpeg', 0.92);
    const encoded = dataUrl.slice(dataUrl.indexOf(',') + 1);
    return Uint8Array.from(atob(encoded), (character) => character.charCodeAt(0));
  }

  private downloadBlob(blob: Blob, fileName: string): void {
    const url = URL.createObjectURL(blob);
    const link = globalThis.document.createElement('a');
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
  }

  private describeError(error: unknown, action: string): string {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 401) {
        return 'Your sign-in has expired. Sign in again and retry.';
      }
      if (error.status === 403 || error.status === 404) {
        return 'You no longer have permission to edit this document.';
      }
      if (error.status === 413) {
        return 'The PDF is too large. Choose a PDF up to 10 MiB.';
      }
      if (error.status === 400) {
        return 'The PDF or document details were not accepted.';
      }
    }
    return `Unable to ${action}. Try again.`;
  }
}
