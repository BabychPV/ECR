import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ImportPreview } from '@/api/types';
import { ImportPanel } from '../ImportPanel';
import { testTheme } from '@/test/render';
import { showDone } from '@/shared/ui/notify';

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

/**
 * Діалог перегляду імпорту (`V-10`).
 *
 * ⚠ Відповідь прев'ю підмінено на рівні `fetch`: предмет — те, що діалог
 * ПОКАЗУЄ з готової відповіді сервера, а не те, як сервер її будує (це —
 * `ImportRoundTripScenarios` на справжньому SQL Server).
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

describe('ImportPanel: перелік змін і відмов', () => {
  it('рядок зміни називає ТАБЛИЦЮ — ключі R1/C1 однакові в десятках таблиць', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [
        {
          rowKey: 'R1',
          columnCode: 'C1',
          oldValue: '5',
          newValue: '6',
          tableCode: 'T2',
          tableNameL10n: { values: { en: 'Water intake' } },
        },
      ],
      rejected: [],
      conflicts: [],
    });

    await openPreview();

    const table = screen.getByRole('table');
    expect(within(table).getByRole('columnheader', { name: '⟦import.table⟧' })).toBeTruthy();
    expect(within(table).getByRole('cell', { name: 'Water intake' })).toBeTruthy();
  });

  it('без назви таблиці показує її код', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [{ rowKey: 'R1', columnCode: 'C1', oldValue: null, newValue: '6', tableCode: 'T2' }],
      rejected: [],
      conflicts: [],
    });

    await openPreview();

    expect(within(screen.getByRole('table')).getByRole('cell', { name: 'T2' })).toBeTruthy();
  });

  it('V-10: причина відмови — з каталогу за messageKey, а не серверне речення', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [],
      rejected: [
        {
          rowKey: 'RTOT',
          columnCode: 'CRO',
          reasonCode: 'ECR-CELL-4221',
          message: 'Комірка обчислюється системою: значення з файлу не застосовується.',
          messageKey: 'err.ECR-CELL-4221.importCalculated',
          tableCode: 'FT1',
        },
        {
          rowKey: 'RTOT',
          columnCode: 'CDEC',
          reasonCode: 'ECR-ACCS-0403',
          message: 'Правило доступу: лише читання.',
          messageKey: 'deny.RowReadOnly',
          tableCode: 'FT1',
        },
      ],
      conflicts: [],
    });

    await openPreview();

    const table = screen.getByRole('table');
    expect(within(table).getByText('⟦err.ECR-CELL-4221.importCalculated⟧ (ECR-CELL-4221)')).toBeTruthy();
    expect(within(table).getByText('⟦deny.RowReadOnly⟧ (ECR-ACCS-0403)')).toBeTruthy();
    expect(within(table).queryByText(/Правило доступу|обчислюється системою/)).toBeNull();
  });

  it('V-10: значення поза рядками таблиці показано адресою комірки книги', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [],
      rejected: [
        {
          rowKey: '—',
          columnCode: 'C2',
          reasonCode: 'ECR-ROW-0404',
          message: 'Значення B3 стоїть поза рядками таблиці: імпорт рядків не створює.',
          messageKey: 'err.ECR-ROW-0404.importOutsideRows',
          tableCode: 'T1',
          excelCell: 'B3',
        },
      ],
      conflicts: [],
    });

    await openPreview();

    const table = screen.getByRole('table');
    expect(within(table).getByRole('cell', { name: 'B3' })).toBeTruthy();
    expect(within(table).getByText('⟦err.ECR-ROW-0404.importOutsideRows⟧ (ECR-ROW-0404)')).toBeTruthy();

    // Відмова блокує застосування — і діалог НЕ каже «файл збігається з аркушем».
    expect(screen.queryByText('⟦import.noChanges⟧')).toBeNull();
    expect(screen.getByRole('button', { name: '⟦import.apply⟧' }).hasAttribute('disabled')).toBe(true);
  });
});

describe('ImportPanel: четвертий раунд (F-01, F-06)', () => {
  it('F-06: відмова типу показана текстом каталогу, Apply вимкнено', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [],
      rejected: [
        {
          rowKey: 'R1',
          columnCode: 'A',
          reasonCode: 'ECR-CELL-0422',
          message: 'The value does not match the column type (err.ECR-CELL-0422.expectsNumber).',
          messageKey: 'err.ECR-CELL-0422.importExpectsNumber',
          tableCode: 'T1',
        },
      ],
      conflicts: [],
    });

    await openPreview();

    const table = screen.getByRole('table');
    expect(within(table).getByText('⟦err.ECR-CELL-0422.importExpectsNumber⟧ (ECR-CELL-0422)')).toBeTruthy();
    expect(screen.getByRole('button', { name: '⟦import.apply⟧' }).hasAttribute('disabled')).toBe(true);
  });

  it('F-01: 202 з jobId — «у фоні, дивись My tasks», а не «застосовано»', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);

        if (url.includes('/import/preview')) {
          return new Response(
            JSON.stringify({
              previewToken: 'tok',
              changes: [{ rowKey: 'R1', columnCode: 'A', oldValue: null, newValue: 1, tableCode: 'T1' }],
              rejected: [],
              conflicts: [],
            } satisfies ImportPreview),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          );
        }

        if (url.includes('/import/apply')) {
          return new Response(JSON.stringify({ jobId: 'IExcelImportJob-1' }), {
            status: 202,
            headers: { 'Content-Type': 'application/json' },
          });
        }

        throw new Error(`неочікуваний запит у тесті: ${url}`);
      }),
    );

    await openPreview();
    await userEvent.click(screen.getByRole('button', { name: '⟦import.apply⟧' }));

    // ⛔ Мутація: прибрати гілку `isQueued` — тут буде `⟦import.applied⟧`.
    await vi.waitFor(() => expect(vi.mocked(showDone)).toHaveBeenCalledWith('⟦import.queued⟧'));
    expect(vi.mocked(showDone)).not.toHaveBeenCalledWith('⟦import.applied⟧');
  });
});
