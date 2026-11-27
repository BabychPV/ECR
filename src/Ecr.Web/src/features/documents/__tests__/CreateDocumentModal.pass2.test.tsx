import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState, type JSX } from 'react';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { testTheme } from '@/test/render';

/**
 * Pass2 (людський прохід 07.11, P3) — майстер «New document»:
 *  п. 4 — той самий проєкт удруге давав другий документ без жодного слова. Дублі дозволені задумом (документ
 *        унікальний за `(ProjectId, BusinessKey)`, `docs/tz/01-context.md`), тож майстер ПОПЕРЕДЖАЄ.
 *        ✎ B74 (24.11): проєкт тримає документи кількох шаблонів — попередження лише про документ ОБРАНОГО шаблону,
 *        на кроці Template, без «Next — створити ще один»;
 *  п. 5 — Esc / ✕ / Cancel посеред майстра мовчки губили введене: тепер «Discard what you entered?» усередині
 *        сторінки (Discard / Keep editing), а порожній майстер закривається одразу.
 */

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

let posted = 0;

/** Документ проєкту 1 — разом із кодом шаблону, за яким його заведено. */
interface ExistingDoc {
  id: number;
  businessKey: string;
  templateCode?: string;
}

/** Шаблони проєкту 1: код → id; `documentCount` рахується з `existing` (як `GET /projects/{id}/templates`). */
const TEMPLATE_IDS: Record<string, number> = { 'GEN-UPSTREAM': 4, LAND: 6 };

function mockServer(options: {
  existing: ExistingDoc[];
  templates?: string[];
  /** Шаблони з прапорцем «один документ на проєкт» (B151). */
  single?: string[];
  /** Документи, яких людина не бачить: рахуються в `documentCount`, але не віддаються переліком. */
  hidden?: ExistingDoc[];
}): void {
  const templates = options.templates ?? ['GEN-UPSTREAM'];
  const hidden = options.hidden ?? [];
  const codeOf = (doc: ExistingDoc): string => doc.templateCode ?? 'GEN-UPSTREAM';

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.endsWith('/api/v1/documents') && init?.method === 'POST') {
        posted += 1;

        return jsonResponse({ documentId: 77 }, 201);
      }

      // Перелік документів проєкту (наявний `listDocuments`): `GET /api/v1/documents?limit=50&projectId=1[&templateCode=…]`
      // — сервер фільтрує за кодом шаблону (B109), без коду віддає всі документи проєкту.
      if (url.includes('/api/v1/documents?')) {
        expect(url).toContain('projectId=1');
        const code = new URL(url, 'http://x').searchParams.get('templateCode');

        return jsonResponse({
          items: options.existing
            .filter((doc) => code === null || codeOf(doc) === code)
            .map(({ templateCode: _code, ...doc }) => ({ ...doc, projectId: 1, nameL10n: null })),
          nextCursor: null,
        });
      }

      if (url.includes('/document-template')) {
        const id = Number(new URL(url, 'http://x').searchParams.get('templateId'));
        const code = Object.keys(TEMPLATE_IDS).find((key) => TEMPLATE_IDS[key] === id) ?? 'GEN-UPSTREAM';

        return jsonResponse({
          templateVersionId: 5, templateCode: code, version: '2.3.0', groupRules: [],
          sheets: [{ id: 9, code: 'AIR', nameL10n: { values: { en: 'Air emissions' } }, sheetGroup: null, isMandatory: false }],
        });
      }

      // ✎ B74 P4: шаблони проєкту; єдиний — майстер обирає його сам.
      if (url.includes('/api/v1/projects/1/templates')) {
        return jsonResponse(
          templates.map((code, index) => ({
            projectTemplateId: index + 1, templateId: TEMPLATE_IDS[code], templateCode: code, templateNameL10n: { values: {} },
            currentTemplateVersionId: 5, version: '2.3.0', layout: 'PerMonthCopies', isPrimary: index === 0,
            documentCount: [...options.existing, ...hidden].filter((doc) => codeOf(doc) === code).length,
            singleDocumentPerProject: (options.single ?? []).includes(code),
          })),
        );
      }

      if (url.includes('/api/v1/projects')) {
        return jsonResponse({ items: [{ id: 1, code: 'P07131100', status: 'Active' }], nextCursor: null, totalCount: 1 });
      }

      return jsonResponse(null);
    }),
  );
}

function Where(): null {
  const location = useLocation();
  (globalThis as { __where?: string }).__where = location.pathname + location.search;

  return null;
}

/** Сторінка-господар: кнопка відкриває майстер, `onClose` знімає `opened` — як `DocumentsPage`. */
function Host(): JSX.Element {
  const [open, setOpen] = useState(true);

  return (
    <>
      <span data-testid="host-state">{open ? 'open' : 'closed'}</span>
      <CreateDocumentModal opened={open} onClose={() => setOpen(false)} />
    </>
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Routes>
            <Route path="*" element={<><Where /><Host /></>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const next = (): HTMLButtonElement => screen.getByRole('button', { name: '⟦wizard.next⟧' });

async function pickProject(): Promise<void> {
  fireEvent.click(await screen.findByLabelText(/documents\.project/, { selector: 'input' }));
  fireEvent.click(await screen.findByRole('option', { name: 'P07131100' }));
  await waitFor(() => expect(next().disabled).toBe(false));
}

afterEach(() => {
  vi.unstubAllGlobals();
  posted = 0;
  delete (globalThis as { __where?: string }).__where;
});

/** Project → Template; `code` — шаблон, який обрати (без нього — єдиний шаблон, обраний сам). */
async function toTemplateStep(code?: string, options: { expectNext?: boolean } = {}): Promise<void> {
  fireEvent.click(next());
  await screen.findByText('⟦documents.createTemplateStep⟧');

  if (code !== undefined) {
    fireEvent.click(await screen.findByLabelText(/documents\.template/, { selector: 'input' }));
    fireEvent.click(await screen.findByRole('option', { name: code }));
  }

  // Склад шаблону прочитано (рядок версії) — далі Next або доступний, або свідомо вимкнений.
  await screen.findByText(/ · v2\.3\.0$/);
  if (options.expectNext !== false) await waitFor(() => expect(next().disabled).toBe(false));
}

const listRequests = (): string[] =>
  vi.mocked(fetch).mock.calls.map(([url]) => String(url)).filter((url) => url.includes('/api/v1/documents?'));

/*
 * ✎ B74 (людина 24.11, live: «чому я бачу це і для чого?»): проєкт «рік · об'єкт» тримає документи КІЛЬКОХ шаблонів
 * (D-576), тож «у проєкту вже є документ» на кроці Project — шум. Попередження — лише коли документ є в ОБРАНОГО
 * шаблону цього проєкту (сервер: `409 ECR-DOC-4092 documentExistsForTemplate` для шаблону «один документ на проєкт»),
 * на кроці Template, без запрошення «Next — створити ще один».
 */
describe('B74: документ цього шаблону в проєкті вже є — попередження на кроці Template', () => {
  it('B74: у проєкту є документ ІНШОГО шаблону — банера немає ні на Project, ні на Template, ні в підсумку', async () => {
    mockServer({ existing: [{ id: 41, businessKey: 'LAND-P1-0001', templateCode: 'LAND' }], templates: ['LAND', 'GEN-UPSTREAM'] });
    show();
    await pickProject();

    // Крок Project: документи проєкту не мають значення, поки шаблон не обрано.
    await waitFor(() =>
      expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('/api/v1/projects/1/templates'))).toBe(true),
    );
    expect(screen.queryByTestId('create-existing-documents')).toBeNull();

    await toTemplateStep('GEN-UPSTREAM');
    expect(screen.queryByTestId('create-existing-documents')).toBeNull();

    fireEvent.click(next()); // → Sheets
    await screen.findByText('⟦documents.createSheetsHint⟧');
    fireEvent.click(next()); // → Review
    await screen.findByTestId('wizard-summary');
    expect(screen.queryByText(/documents\.createExistsReview/)).toBeNull();
    // Документ іншого шаблону ніде не названо.
    expect(document.body.textContent).not.toContain('LAND-P1-0001');
  });

  it('B74: документ того самого шаблону — на кроці Project банера немає, на Template — є, з «Open», без «Next — ще один»', async () => {
    mockServer({ existing: [{ id: 41, businessKey: 'P1-V1-0008' }] });
    show();
    await pickProject();
    expect(screen.queryByTestId('create-existing-documents')).toBeNull();

    await toTemplateStep();
    const banner = await screen.findByTestId('create-existing-documents');
    // Перелік питали саме за кодом обраного шаблону.
    expect(listRequests().some((url) => url.includes('templateCode=GEN-UPSTREAM'))).toBe(true);
    expect(banner.textContent).toContain('⟦documents.createExistsTitle⟧');
    // Банер називає наявний документ його ключем.
    expect(banner.textContent).toContain('⟦documents.createExistsText (key=P1-V1-0008)⟧');
    // Один документ — «All documents» не потрібна.
    expect(screen.queryByRole('button', { name: '⟦documents.createExistsShowAll⟧' })).toBeNull();
    // Шаблон без прапорця «один документ на проєкт»: другий документ сервер дозволяє — Next доступний.
    expect(next().disabled).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: '⟦documents.createExistsOpen (key=P1-V1-0008)⟧' }));
    await waitFor(() => expect((globalThis as { __where?: string }).__where).toBe('/documents/41'));
    expect(screen.getByTestId('host-state').textContent).toBe('closed');
    // ⛔ Відкрити наявний — не «відкинути введене?»: питання не з'являється.
    expect(screen.queryByText('⟦documents.createDiscardTitle⟧')).toBeNull();
    expect(posted).toBe(0);
  });

  it('B74: шаблон «один документ на проєкт» із документом — Next вимкнено, причина в банері, «Open» відкриває наявний', async () => {
    mockServer({ existing: [{ id: 41, businessKey: 'P1-V1-0008' }], single: ['GEN-UPSTREAM'] });
    show();
    await pickProject();

    await toTemplateStep(undefined, { expectNext: false });
    const banner = await screen.findByTestId('create-existing-documents');
    // Причина — що шаблон дозволяє лише один документ; не «створіть ще один».
    expect(banner.textContent).toContain('⟦documents.createExistsSingleText (key=P1-V1-0008)⟧');
    expect(banner.textContent).not.toContain('documents.createExistsText');
    expect(next().disabled).toBe(true);

    fireEvent.click(screen.getByRole('button', { name: '⟦documents.createExistsOpen (key=P1-V1-0008)⟧' }));
    await waitFor(() => expect((globalThis as { __where?: string }).__where).toBe('/documents/41'));
    expect(posted).toBe(0);
  });

  it('B74: шаблон «один документ на проєкт», документ людині невидимий — Next вимкнено, текст без ключа', async () => {
    mockServer({ existing: [], hidden: [{ id: 50, businessKey: 'P1-V1-0099' }], single: ['GEN-UPSTREAM'] });
    show();
    await pickProject();

    await toTemplateStep(undefined, { expectNext: false });
    const banner = await screen.findByTestId('create-existing-documents');
    expect(banner.textContent).toContain('⟦documents.createExistsSingleHidden⟧');
    expect(document.body.textContent).not.toContain('P1-V1-0099');
    expect(next().disabled).toBe(true);
  });

  it('B74: прапорцевий шаблон без документа в проєкті — Next доступний, банера немає', async () => {
    mockServer({ existing: [{ id: 40, businessKey: 'LAND-P1-0001', templateCode: 'LAND' }], templates: ['LAND', 'GEN-UPSTREAM'], single: ['GEN-UPSTREAM'] });
    show();
    await pickProject();

    await toTemplateStep('GEN-UPSTREAM');
    expect(screen.queryByTestId('create-existing-documents')).toBeNull();
    expect(next().disabled).toBe(false);
  });

  it('B74: два документи шаблону — «All documents», підсумок каже про наявний; створення вирішує сервер (B151)', async () => {
    mockServer({
      existing: [
        { id: 40, businessKey: 'LAND-P1-0001', templateCode: 'LAND' },
        { id: 41, businessKey: 'P1-V1-0008' },
        { id: 42, businessKey: 'P1-V1-0009' },
      ],
      templates: ['LAND', 'GEN-UPSTREAM'],
    });
    show();
    await pickProject();
    expect(screen.queryByTestId('create-existing-documents')).toBeNull();

    await toTemplateStep('GEN-UPSTREAM');
    const banner = await screen.findByTestId('create-existing-documents');
    // Перший документ САМЕ цього шаблону, а не перший документ проєкту (LAND).
    expect(banner.textContent).toContain('⟦documents.createExistsText (key=P1-V1-0008)⟧');
    expect(banner.textContent).not.toContain('LAND-P1-0001');
    expect(screen.getByRole('button', { name: '⟦documents.createExistsShowAll⟧' })).toBeTruthy();

    // ⚠ Next не блокується: «один документ на проєкт» — прапорець шаблону (B151), якого клієнт не бачить; шаблон без
    // прапорця сервер дозволяє вдруге, з прапорцем — відмовляє `409 ECR-DOC-4092` банером у кроці Review.
    fireEvent.click(next()); // → Sheets
    await screen.findByText('⟦documents.createSheetsHint⟧');
    fireEvent.click(next()); // → Review
    await screen.findByTestId('wizard-summary');
    expect(screen.getByText(/⟦documents\.createExistsReview \(key=P1-V1-0008\)⟧/)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '⟦documents.createApply⟧' }));
    await waitFor(() => expect(posted).toBe(1));
    await waitFor(() => expect((globalThis as { __where?: string }).__where).toBe('/documents/77'));
  });

  it('Pass2: проєкт без документів — жодного попередження', async () => {
    mockServer({ existing: [] });
    show();
    await pickProject();
    await toTemplateStep();

    // `documentCount: 0` обраного шаблону — перелік і не питається, банера немає.
    expect(screen.queryByTestId('create-existing-documents')).toBeNull();
    expect(listRequests()).toEqual([]);
  });
});

describe('Pass2 п. 5: вихід із введеним — «Discard what you entered?»', () => {
  it('Pass2: Esc після вибору проєкту питає; «Keep editing» лишає майстер із введеним', async () => {
    mockServer({ existing: [] });
    show();
    await pickProject();

    fireEvent.keyDown(screen.getAllByRole('dialog')[0]!, { key: 'Escape' });
    expect(await screen.findByText('⟦documents.createDiscardTitle⟧')).toBeTruthy();
    expect(screen.getByTestId('host-state').textContent).toBe('open');

    fireEvent.click(screen.getByRole('button', { name: '⟦documents.createKeepEditing⟧' }));
    await waitFor(() => expect(screen.queryByText('⟦documents.createDiscardTitle⟧')).toBeNull());
    expect(screen.getByTestId('host-state').textContent).toBe('open');
    // Вибір проєкту зберігся: Next доступний без повторного вибору.
    expect(next().disabled).toBe(false);
  });

  it('Pass2: Cancel після вибору проєкту питає; «Discard» закриває майстер', async () => {
    mockServer({ existing: [] });
    show();
    await pickProject();

    fireEvent.click(screen.getByTestId('wizard-cancel'));
    expect(await screen.findByText('⟦documents.createDiscardTitle⟧')).toBeTruthy();
    expect(screen.getByTestId('host-state').textContent).toBe('open');

    fireEvent.click(screen.getByRole('button', { name: '⟦documents.createDiscardVerb⟧' }));
    await waitFor(() => expect(screen.getByTestId('host-state').textContent).toBe('closed'));
  });

  it('Pass2: порожній майстер закривається одразу, без питання', async () => {
    mockServer({ existing: [] });
    show();
    await screen.findByLabelText(/documents\.project/, { selector: 'input' });

    fireEvent.click(screen.getByTestId('wizard-cancel'));
    await waitFor(() => expect(screen.getByTestId('host-state').textContent).toBe('closed'));
    expect(screen.queryByText('⟦documents.createDiscardTitle⟧')).toBeNull();
  });
});
