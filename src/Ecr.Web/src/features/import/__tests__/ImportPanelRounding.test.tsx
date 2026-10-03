import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ImportPreview } from '@/api/types';
import { ImportPanel } from '../ImportPanel';
import { testTheme } from '@/test/render';

/**
 * ФВ-9.16b: імпорт `.xlsx` округлює число до `Scale` колонки — і перегляд
 * КАЖЕ про це: лічильник, позначка «округлено з …» у рядку зміни й окремий
 * перелік округлених комірок, а відмова точності — текстом каталогу.
 *
 * ⚠ Відповідь прев'ю підмінено на рівні `fetch`: предмет — те, що діалог
 * показує з готової відповіді сервера (як будує її сервер —
 * `ImportDiffBuilderRoundingMarkTests`).
 */

function mockPreview(preview: ImportPreview): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/import/preview')) {
        return new Response(JSON.stringify(preview), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

async function openPreview(): Promise<void> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const { container } = render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ImportPanel documentId={1} periodKey={202609} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  const input = container.querySelector('input[type="file"]');
  if (!(input instanceof HTMLInputElement)) throw new Error('немає поля вибору файлу');

  await userEvent.upload(input, new File(['x'], 'book.xlsx'));
  await screen.findByRole('dialog');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ImportPanel: округлення до Scale колонки (ФВ-9.16b)', () => {
  it('округлена зміна позначена числом із файлу і є в окремому переліку', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [
        { rowKey: 'R1', columnCode: 'C1', oldValue: null, newValue: 2.35, roundedFrom: 2.345, tableCode: 'T1' },
        { rowKey: 'R2', columnCode: 'C1', oldValue: '1', newValue: 7, roundedFrom: null, tableCode: 'T1' },
      ],
      rejected: [],
      conflicts: [],
    });

    await openPreview();

    expect(screen.getByText('⟦import.rounded (count=1)⟧')).toBeTruthy();

    // Перелік округлених — лише округлена комірка, з числом із файлу й записаним.
    const list = screen.getByRole('table', { name: '⟦import.roundedTitle⟧' });
    const rows = within(list).getAllByRole('row');
    expect(rows).toHaveLength(2);
    expect(within(list).getByRole('columnheader', { name: '⟦import.inFile⟧' })).toBeTruthy();
    expect(within(rows[1]!).getByRole('cell', { name: '2.345' })).toBeTruthy();
    expect(within(rows[1]!).getByRole('cell', { name: '2.35' })).toBeTruthy();
    expect(within(list).queryByRole('cell', { name: 'R2' })).toBeNull();

    // Позначка в рядку загального переліку — текстом, лише на округленій.
    expect(screen.getAllByText('⟦import.roundedMark (value=2.345)⟧')).toHaveLength(1);

    // Округлення — не відмова: застосувати можна.
    expect(screen.getByRole('button', { name: '⟦import.apply⟧' }).hasAttribute('disabled')).toBe(false);
  });

  it('без округлених змін — ні лічильника, ні переліку, ні позначки (і для плану без поля)', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [{ rowKey: 'R1', columnCode: 'C1', oldValue: null, newValue: 5, tableCode: 'T1' }],
      rejected: [],
      conflicts: [],
    });

    await openPreview();

    expect(screen.queryByText(/import\.rounded/)).toBeNull();
    expect(screen.getAllByRole('table')).toHaveLength(1);
  });

  it('число, що після округлення не вміщується в Precision, — відмова текстом каталогу', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [],
      rejected: [
        {
          rowKey: 'R1',
          columnCode: 'C1',
          reasonCode: 'ECR-CELL-0422',
          message: 'The number does not fit the column precision (4, scale 2) after rounding to the column scale.',
          messageKey: 'err.ECR-CELL-0422.importPrecision',
          tableCode: 'T1',
        },
      ],
      conflicts: [],
    });

    await openPreview();

    const table = screen.getByRole('table');
    expect(within(table).getByText('⟦err.ECR-CELL-0422.importPrecision⟧')).toBeTruthy();
    expect(within(table).queryByText(/does not fit/)).toBeNull();
    expect(screen.getByRole('button', { name: '⟦import.apply⟧' }).hasAttribute('disabled')).toBe(true);
  });
});
