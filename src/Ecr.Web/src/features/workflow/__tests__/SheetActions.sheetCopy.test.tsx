import { describe, it, expect, vi, afterEach } from 'vitest';
import { cleanup, render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * RS: копія аркуша подається, повертається й відкликається САМЕ копією — тіло запиту несе `sheetCopyId`
 * (контракт: необов'язкове поле; без нього сервер працює з оригіналом). Оригінал поля не шле.
 */

const CurrentUser = {
  denies: [],
  grants: { 'Project:7': 'Submit' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: [],
  simulatedForUserId: null,
  userId: 9,
  userName: 'author',
};

const Summary = {
  businessKey: 'DOC-1',
  createdAt: '2026-01-01T00:00:00Z',
  id: 1,
  nameL10n: null,
  projectId: 7,
  sheetCount: 1,
  sheetStates: { S1: 'Draft', 'S1#77': 'Draft' },
};

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

let submitBodies: Record<string, unknown>[] = [];
let requestedUrls: string[] = [];

function mockFetch(): void {
  submitBodies = [];
  requestedUrls = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      requestedUrls.push(url);

      if (url.includes('/api/v1/me')) return json(CurrentUser);

      if (url.includes('/api/v1/documents/1/submit') && init?.method === 'POST') {
        submitBodies.push(JSON.parse(String(init.body)) as Record<string, unknown>);

        return new Response(null, { status: 204 });
      }

      if (url.includes('/recall?')) return json({ canRecall: true });

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

function show(sheetCopyId: number | null, state = 'Draft'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', 1, 202601], Summary);

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SheetActions documentId={1} sheetDefId={42} sheetCopyId={sheetCopyId} periodKey={202601} state={state} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('SheetActions: копія аркуша (sheetCopyId у запитах)', () => {
  it('подання на вкладці копії шле sheetCopyId у тілі POST …/submit', async () => {
    mockFetch();
    show(77);

    fireEvent.click(await screen.findByRole('button', { name: /workflow\.submitPeriod/ }));

    await waitFor(() => {
      expect(submitBodies).toHaveLength(1);
    });
    expect(submitBodies[0]).toMatchObject({ sheetDefId: 42, periodKey: 202601, sheetCopyId: 77 });
  });

  it('подання оригіналу не шле sheetCopyId взагалі', async () => {
    mockFetch();
    show(null);

    fireEvent.click(await screen.findByRole('button', { name: /workflow\.submitPeriod/ }));

    await waitFor(() => {
      expect(submitBodies).toHaveLength(1);
    });
    expect(submitBodies[0]).not.toHaveProperty('sheetCopyId');
  });

  it('доступність відкликання копії питає сервер з sheetCopyId у запиті', async () => {
    mockFetch();
    show(77, 'Submitted');

    await waitFor(() => {
      expect(requestedUrls.some((url) => url.includes('/recall?') && url.includes('sheetCopyId=77'))).toBe(true);
    });
  });

  it('ціль подання копії питається з sheetCopyId, а оригіналу — без нього', async () => {
    mockFetch();
    show(77);

    await waitFor(() => {
      expect(requestedUrls.some((url) => url.includes('/submission-target?') && url.includes('sheetCopyId=77'))).toBe(true);
    });
    cleanup();
    vi.unstubAllGlobals();

    mockFetch();
    show(null);

    await waitFor(() => {
      expect(requestedUrls.some((url) => url.includes('/submission-target?'))).toBe(true);
    });
    expect(requestedUrls.filter((url) => url.includes('sheetCopyId'))).toEqual([]);
  });
});
