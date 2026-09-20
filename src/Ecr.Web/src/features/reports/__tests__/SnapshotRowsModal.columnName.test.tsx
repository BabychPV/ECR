import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import SnapshotRowsModal from '@/features/reports/SnapshotRowsModal';
import { testTheme } from '@/test/render';

/**
 * `R9`: шапка показує ПІДПИС колонки, а не її код.
 *
 * ⛔ Підпис приходить готовим (`SnapshotColumn.name`) — сервер розгортає ланцюг
 * «мова запиту → en → код» сам (`ReportColumnNames`) і порожнього не віддає.
 * Власного фолбеку на клієнті бути не повинно: друга копія того самого правила
 * розійшлася б із першою, і шапка на екрані перестала б збігатися з шапкою
 * книги XLSX, яку читає регулятор.
 *
 * ⚠ Тому фікстура тут така, де код і підпис РІЗНІ: на фікстурі, де вони
 * збігаються, цей тест був би зеленим і до зміни, і після неї.
 */

const columns = [
  { code: 'OutputCode', kind: 'text', name: 'Показник' },
  { code: 'Value', kind: 'number', name: 'Значення, т' },
];

const rows = [{ rowNo: 1, cells: { OutputCode: 'E_CO2', Value: 12.5 } }];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.includes('/rows')) {
        return json({ columns, rows, nextCursor: null, groups: null, totals: null });
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        {/* ⚠ Модалка відкрита саме тим, що `snapshotId !== null` — окремого
            пропа `opened` вона не має (перша редакція цього файлу його
            передавала: `vitest` це проковтнув, а `tsc` назвав). */}
        <SnapshotRowsModal snapshotId={7} onClose={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SnapshotRowsModal: підпис колонки замість коду (R9)', () => {
  it('у шапці — назва з сервера, а коду колонки в ній немає', async () => {
    mockServer();
    show();

    const table = await waitFor(() => screen.getByRole('table'), { timeout: 10_000 });

    const headers = within(table)
      .getAllByRole('columnheader')
      .map((cell) => cell.textContent);

    expect(headers).toEqual(['#', 'Показник', 'Значення, т']);

    /*
     * ⚠ Код колонки не зник з екрана зовсім — він лишається ключем комірок
     * (`SnapshotRow.Cells`), і саме значення `E_CO2` під шапкою це доводить.
     * Твердження вужче: код не стоїть ТАМ, де має стояти підпис.
     */
    expect(headers).not.toContain('OutputCode');
    expect(within(table).getByText('E_CO2')).toBeDefined();
  }, 30_000);
});
