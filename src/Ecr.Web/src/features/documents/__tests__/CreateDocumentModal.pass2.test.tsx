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
 *        унікальний за `(ProjectId, BusinessKey)`, `docs/tz/01-context.md`), тож майстер ПОПЕРЕДЖАЄ: «у проєкту вже
 *        є документ — відкрити його або створити ще один»;
 *  п. 5 — Esc / ✕ / Cancel посеред майстра мовчки губили введене: тепер «Discard what you entered?» усередині
 *        сторінки (Discard / Keep editing), а порожній майстер закривається одразу.
 */

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

let posted = 0;

function mockServer(options: { existing: { id: number; businessKey: string }[] }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.endsWith('/api/v1/documents') && init?.method === 'POST') {
        posted += 1;

        return jsonResponse({ documentId: 77 }, 201);
      }

      // Перелік документів проєкту (наявний `listDocuments`): `GET /api/v1/documents?limit=50&projectId=1`.
      if (url.includes('/api/v1/documents?')) {
        expect(url).toContain('projectId=1');

        return jsonResponse({
          items: options.existing.map((doc) => ({ ...doc, projectId: 1, nameL10n: null })),
          nextCursor: null,
        });
      }

      if (url.includes('/document-template')) {
        return jsonResponse({
          templateVersionId: 5, templateCode: 'GEN-UPSTREAM', version: '2.3.0', groupRules: [],
          sheets: [{ id: 9, code: 'AIR', nameL10n: { values: { en: 'Air emissions' } }, sheetGroup: null, isMandatory: false }],
        });
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

describe('Pass2 п. 4: у проєкту вже є документ — попередження перед створенням', () => {
  it('Pass2: проєкт із документом — банер «вже є» з дією «Open», і вона відкриває наявний, нічого не створюючи', async () => {
    mockServer({ existing: [{ id: 41, businessKey: 'P1-V1-0008' }] });
    show();
    await pickProject();

    const banner = await screen.findByTestId('create-existing-documents');
    expect(banner.textContent).toContain('⟦documents.createExistsTitle⟧');
    // Банер називає наявний документ його ключем.
    expect(banner.textContent).toContain('⟦documents.createExistsText (key=P1-V1-0008)⟧');
    // Один документ — «All documents» не потрібна.
    expect(screen.queryByRole('button', { name: '⟦documents.createExistsShowAll⟧' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: '⟦documents.createExistsOpen (key=P1-V1-0008)⟧' }));
    await waitFor(() => expect((globalThis as { __where?: string }).__where).toBe('/documents/41'));
    expect(screen.getByTestId('host-state').textContent).toBe('closed');
    // ⛔ Відкрити наявний — не «відкинути введене?»: питання не з'являється.
    expect(screen.queryByText('⟦documents.createDiscardTitle⟧')).toBeNull();
    expect(posted).toBe(0);
  });

  it('Pass2: «створити ще один» лишається можливим — Next не заблоковано, підсумок каже, що документ уже є', async () => {
    mockServer({ existing: [{ id: 41, businessKey: 'P1-V1-0008' }, { id: 42, businessKey: 'P1-V1-0009' }] });
    show();
    await pickProject();

    await screen.findByTestId('create-existing-documents');
    expect(screen.getByRole('button', { name: '⟦documents.createExistsShowAll⟧' })).toBeTruthy();
    expect(next().disabled).toBe(false);

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

    // Запит переліку пройшов, а банера немає.
    await waitFor(() =>
      expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('/api/v1/documents?'))).toBe(true),
    );
    expect(screen.queryByTestId('create-existing-documents')).toBeNull();
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
