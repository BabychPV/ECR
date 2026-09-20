import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * `BE-31`: кнопку «Recall» показує ВІДПОВІДЬ СЕРВЕРА (`GET …/recall`), а не
 * здогад клієнта: «автор подання» і «жоден крок не підписано» з `/api/v1/me`
 * не виводяться.
 */

const CurrentUser = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: [],
  simulatedForUserId: null,
  userId: 9,
  userName: 'author',
};

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

let recallBody: unknown = null;
let availabilityAsked = 0;

function mockFetch(canRecall: boolean): void {
  recallBody = null;
  availabilityAsked = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) return json(CurrentUser);

      if (url.includes('/recall') && init?.method === 'POST') {
        recallBody = JSON.parse(String(init.body));
        return new Response(null, { status: 204 });
      }

      if (url.includes('/recall?sheetDefId=42&periodKey=202601')) {
        availabilityAsked += 1;
        return json({ canRecall });
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

// ⚠ Каталогу в тестах немає: `t()` віддає ключ у дужках-маркерах, тож назва
// кнопки — `workflow.recall` плюс, можливо, один символ маркера. НЕ `recalculate`.
const RecallButton = /recall\W?$/i;

function show(state: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SheetActions documentId={1} sheetDefId={42} periodKey={202601} state={state} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: відкликання подання автором (BE-31)', () => {
  it('сервер дозволив — кнопка є, причина йде в тілі POST …/recall', async () => {
    mockFetch(true);
    show('Submitted');

    fireEvent.click(await screen.findByRole('button', { name: RecallButton }));

    fireEvent.change(await screen.findByRole('textbox'), { target: { value: 'wrong month' } });
    const confirm = screen.getAllByRole('button', { name: RecallButton }).at(-1);
    fireEvent.click(confirm as HTMLElement);

    await waitFor(() => {
      expect(recallBody).toEqual({ sheetDefId: 42, periodKey: 202601, reason: 'wrong month' });
    });
  });

  it('сервер відмовив — кнопки немає', async () => {
    mockFetch(false);
    show('Submitted');

    await waitFor(() => {
      expect(availabilityAsked).toBe(1);
    });
    expect(screen.queryByRole('button', { name: RecallButton })).toBeNull();
  });

  it('на чернетці сервер навіть не питається', async () => {
    mockFetch(true);
    show('Draft');

    await waitFor(() => {
      expect(fetch).toHaveBeenCalled();
    });
    expect(availabilityAsked).toBe(0);
    expect(screen.queryByRole('button', { name: RecallButton })).toBeNull();
  });
});
