import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';
import { testTheme } from '@/test/render';

/**
 * Кнопка «Перерахувати» на документі показується за ЧИТАННЯМ (`Document.View`), а не за
 * `Calculation.Recalculate` — те лишається для проєктного/масового перерахунку
 * (`RecalculateDocumentHandler.Permission`).
 *
 * Мутаційний доказ: повернути `can(me, 'Calculation.Recalculate')` у `SheetActions.tsx` —
 * перший тест червоний; зняти перевірку — другий.
 */
function mockMe(permissions: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions,
            simulatedForUserId: null,
            userId: 9,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      throw new Error(`unexpected request: ${url}`);
    }),
  );
}

function renderActions(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SheetActions documentId={1} sheetDefId={2} periodKey={202401} state="Draft" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: право на кнопку Recalculate', () => {
  it('виконавець із читанням (Document.View) бачить кнопку', async () => {
    mockMe(['Document.View']);
    renderActions();
    expect(await screen.findByRole('button', { name: /recalculate/i })).toBeTruthy();
  });

  it('без Document.View кнопки немає, навіть із Calculation.Recalculate', async () => {
    mockMe(['Calculation.Recalculate']);
    renderActions();
    await vi.waitFor(() => expect(vi.mocked(fetch)).toHaveBeenCalled());
    await new Promise((r) => setTimeout(r, 100));
    expect(screen.queryByRole('button', { name: /recalculate/i })).toBeNull();
  });
});
