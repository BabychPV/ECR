import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';
import { testTheme } from '@/test/render';

/**
 * Нюанс «Recalculate»: формули аркуша читають сусідні аркуші того самого
 * документа (Q-331). Раніше це пояснював Mantine `Tooltip` — він не давав
 * кнопці `aria-describedby`, тож читач озвучував лише «Recalculate».
 *
 * ⛔ Мутаційний доказ: повернути `Tooltip` замість `Hint` у `SheetActions.tsx`
 * — обидва твердження про опис червоні.
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

function mockFetch(): void {
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

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: підказка «Recalculate» доступна з клавіатури', () => {
  it('кнопка має опис без наведення, і фокус табуляцією показує підказку', async () => {
    mockFetch();
    const user = userEvent.setup();

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <SheetActions documentId={1} sheetDefId={2} periodKey={202401} state="Draft" />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const button = await screen.findByRole('button', {
      name: /recalculate/i,
      description: /workflow\.recalculateHint/,
    });

    // Кнопка сама в порядку табуляції — зайвого `tabindex` `Hint` не додає.
    expect(button.hasAttribute('tabindex')).toBe(false);

    await user.tab();
    expect(document.activeElement).toBe(button);
    expect((await screen.findByRole('tooltip')).textContent).toMatch(/workflow\.recalculateHint/);
  });
});
