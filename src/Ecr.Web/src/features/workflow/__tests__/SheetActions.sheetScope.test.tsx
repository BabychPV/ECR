import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * Q-328: директива паритету зі старою системою, прогалина 2 (Q-327 → Q-328).
 * До цього пакета кнопка «Recalculate» на екрані аркуша слала лише
 * `periodKey` — сервер перераховував увесь документ незалежно від того,
 * який аркуш був відкритий. Тест доводить МУТАЦІЄЮ, що кнопка тепер справді
 * передає `sheetDefId` цього аркуша в тілі запиту.
 */

const CurrentUser = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Calculation.Recalculate'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'tester',
};

const SheetDefId = 42;

let recalculateBody: unknown = null;

function mockFetch(): void {
  recalculateBody = null;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(JSON.stringify(CurrentUser), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/recalculate')) {
        recalculateBody = init?.body ? JSON.parse(String(init.body)) : null;

        return new Response(JSON.stringify({ jobId: 'job-1' }), {
          status: 202,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/jobs/')) {
        return new Response(
          JSON.stringify({ jobId: 'job-1', state: 'Running', percent: 10, message: null, error: null }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SheetActions documentId={1} sheetDefId={SheetDefId} periodKey={202601} state="Draft" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: кнопка «Перерахувати» звужує перерахунок до свого аркуша (Q-328)', () => {
  it('шле sheetDefId цього аркуша в тілі POST /documents/{id}/recalculate', async () => {
    mockFetch();

    show();

    const button = await screen.findByRole('button', { name: /recalculate/i });
    fireEvent.click(button);

    await waitFor(() => {
      expect(recalculateBody).not.toBeNull();
    });

    // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ: тіло запиту несе САМЕ той аркуш, на екрані якого
    // стоїть кнопка — не `undefined`, не якийсь інший.
    expect(recalculateBody).toMatchObject({ periodKey: 202601, sheetDefId: SheetDefId });
  });
});
