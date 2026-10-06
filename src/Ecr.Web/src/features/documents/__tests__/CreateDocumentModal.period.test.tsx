import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { newestOpenPeriodKey } from '@/features/documents/newDocumentPeriod';
import { testTheme } from '@/test/render';

/**
 * A2-05 (повтор A1-16): форма «Новий документ» не мала вибору періоду, і документ відкривався в
 * ПОТОЧНОМУ періоді проєкту (`isCurrent`, 202609), коли відкритий уже 202610. Тепер у формі є
 * `Select` відкритих періодів; типово — найновіший відкритий; його ж несе адреса документа.
 */

const periods = [
  { id: 1, periodKey: 202608, state: 'Closed', isCurrent: false },
  { id: 2, periodKey: 202609, state: 'Open', isCurrent: true },
  { id: 3, periodKey: 202610, state: 'Open', isCurrent: false },
  { id: 4, periodKey: 202611, state: 'Planned', isCurrent: false },
];

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

let posted: Record<string, unknown> | null = null;

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }

      if (url.endsWith('/api/v1/documents') && init?.method === 'POST') {
        posted = JSON.parse(String(init.body)) as Record<string, unknown>;

        return jsonResponse({ documentId: 77 }, 201);
      }

      if (url.includes('/document-template')) {
        return jsonResponse({
          templateVersionId: 5, templateCode: 'AIR', version: '1.0', groupRules: [],
          sheets: [{ id: 9, code: 'GEN', nameL10n: { values: { en: 'General' } }, sheetGroup: null, isMandatory: false }],
        });
      }

      if (url.includes('/projects/1/periods')) {
        return jsonResponse({ projectId: 1, periods, currentPeriodMode: 'Auto', periodKind: 'Monthly' });
      }

      if (url.includes('/api/v1/projects')) {
        return jsonResponse({ items: [{ id: 1, code: 'PRJ', status: 'Active' }], nextCursor: null, totalCount: 1 });
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

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="*" element={<><Where /><CreateDocumentModal opened onClose={() => {}} /></>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

async function pickProjectAndSheet(): Promise<void> {
  fireEvent.click(await screen.findByLabelText('⟦documents.project⟧'));
  fireEvent.click(await screen.findByRole('option', { name: 'PRJ' }));
  fireEvent.click(await screen.findByLabelText('General (GEN)'));
}

afterEach(() => {
  vi.unstubAllGlobals();
  posted = null;
  delete (globalThis as { __where?: string }).__where;
});

describe('A2-05: період у формі створення документа', () => {
  it('newestOpenPeriodKey: найновіший ВІДКРИТИЙ, а не поточний і не запланований', () => {
    expect(newestOpenPeriodKey(periods)).toBe(202610);
    expect(newestOpenPeriodKey([{ periodKey: 202601, state: 'Closed' }])).toBeNull();
    expect(newestOpenPeriodKey(undefined)).toBeNull();
  });

  it('Select показує лише відкриті періоди, типово — найновіший', async () => {
    mockServer();
    show();
    await pickProjectAndSheet();

    const select = (await screen.findByLabelText('⟦documents.period⟧')) as HTMLInputElement;
    expect(select.value).toBe('202610');

    fireEvent.click(select);
    const options = (await screen.findAllByRole('option')).map((option) => option.textContent);
    expect(options).toEqual(['202610', '202609']);
  });

  it('створення відкриває документ у вибраному періоді (типово 202610)', async () => {
    mockServer();
    show();
    await pickProjectAndSheet();
    await screen.findByLabelText('⟦documents.period⟧');

    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));

    await waitFor(() => expect((globalThis as { __where?: string }).__where).toBe('/documents/77?periodKey=202610'));
    expect(posted).not.toBeNull();
  });

  it('вибір людини перекриває типовий', async () => {
    mockServer();
    show();
    await pickProjectAndSheet();

    fireEvent.click(await screen.findByLabelText('⟦documents.period⟧'));
    fireEvent.click(await screen.findByRole('option', { name: '202609' }));
    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));

    await waitFor(() => expect((globalThis as { __where?: string }).__where).toBe('/documents/77?periodKey=202609'));
  });
});
