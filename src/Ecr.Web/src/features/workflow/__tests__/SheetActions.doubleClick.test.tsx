import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * Аудит-пас 5: швидкий подвійний клік на «Перерахувати» ставив у чергу ДВА
 * однакових перерахунки замість одного.
 */
/**
 * ⛔ `Button.loading` (і похідний від нього `disabled`) оновлюється лише на
 * НАСТУПНОМУ рендері React — швидкий подвійний клік (не дві окремі дії
 * користувача, а один фізичний подвійний клік) встигає викликати
 * `recalculate.mutate()` двічі ДО того, як перший рендер із `loading: true`
 * встигає заблокувати кнопку.
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

let recalculateCalls = 0;

function mockFetch(): void {
  recalculateCalls = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(JSON.stringify(CurrentUser), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/recalculate')) {
        recalculateCalls += 1;

        return new Response(JSON.stringify({ jobId: `job-${recalculateCalls}` }), {
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
        <SheetActions documentId={1} sheetDefId={2} periodKey={202601} state="Draft" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: захист від подвійного кліку на «Перерахувати»', () => {
  it('швидкий подвійний клік ставить у чергу рівно ОДИН перерахунок', async () => {
    mockFetch();

    show();

    const button = await screen.findByRole('button', { name: /recalculate/i });

    // ⛔ `fireEvent.click` НЕ чекає на перерендер React між викликами (на
    // відміну від `userEvent.click`, який фактично серіалізує клік і
    // послідовне очікування — тому НЕ відтворює справжню гонитву й не ловив
    // би цей дефект). Реальний швидкий подвійний клік викликає обробник
    // двічі ДО того, як `recalculate.isPending`/`loading` встигає
    // перерендеритись у `true` і заблокувати кнопку — саме це тут і
    // відтворено: два виклики `fireEvent.click` без очікування між ними.
    fireEvent.click(button);
    fireEvent.click(button);

    await waitFor(() => {
      expect(recalculateCalls).toBe(1);
    });
  });
});
