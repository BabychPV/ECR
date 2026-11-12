import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ImportPreview, ImportRejection } from '@/api/types';
import { ImportPanel } from '../ImportPanel';
import { testTheme } from '@/test/render';

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

/**
 * ✎ B27 (P2, F13 #829): «перезаписати рядок» на аркуші-копії. Та сама таблиця T1 з тим самим ключем R1 стоїть на
 * «Vessel 1» (екземпляр 729) і «Vessel 2» (730), обидва рядки — конфлікти «змінено після експорту».
 *
 * ⛔ Червоне без фіксу: рядки групувались лише за таблицею й ключем — два конфлікти зливались в ОДИН прапорець
 * «T1 · R1», а позначка надсилала `{ tableCode, rowKey }` без екземпляра, і сервер перезаписував обидві копії.
 * Тепер — окремий прапорець на кожну копію, і позначка несе `tableInstanceId`.
 *
 * ⚠ Сервер підмінено на рівні `fetch` (як `ImportPanelOverwrite.test.tsx`); що сервер робить із `tableInstanceId` —
 * `ExcelImportOverwriteSheetCopyTests`.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(preview: ImportPreview): unknown[] {
  const applied: unknown[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/import/preview')) return json(preview);
      if (url.includes('/import/apply')) {
        applied.push(JSON.parse(String(init?.body)));
        return json({ appliedCells: 1, rowVersions: {}, validation: [], recalculationJobId: null });
      }
      if (url.includes('/calculation-results')) return json([]);

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );

  return applied;
}

async function openPreview(): Promise<void> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const { container } = render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ImportPanel documentId={7} periodKey={202609} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  const input = container.querySelector('input[type="file"]');
  if (!(input instanceof HTMLInputElement)) throw new Error('немає поля вибору файлу');

  await userEvent.upload(input, new File(['x'], 'book.xlsx'));
  await screen.findByRole('dialog');
}

const vesselData = { values: { en: 'Vessel data' } };

function conflict(tableInstanceId: number, sheet: string, excelCell: string): ImportRejection {
  return {
    rowKey: 'R1',
    columnCode: 'A',
    reasonCode: 'ECR-CELL-0409',
    message: 'diag',
    messageKey: 'err.ECR-CELL-0409.importRowChangedSinceExport',
    tableCode: 'T1',
    tableNameL10n: vesselData,
    sheetNameL10n: { values: { en: sheet } },
    tableInstanceId,
    excelCell,
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('ImportPanel: перезапис рядка на аркуші-копії (B27)', () => {
  it('кожна копія — свій прапорець зі своїми значеннями; позначка копії несе її екземпляр', async () => {
    const applied = mockServer({
      previewToken: 'tok',
      changes: [],
      rejected: [conflict(729, 'Vessel 1', 'A3'), conflict(730, 'Vessel 2', 'A3')],
      conflicts: [],
      overwritable: [
        {
          rowKey: 'R1', columnCode: 'A', oldValue: 7, newValue: 5, tableCode: 'T1',
          tableNameL10n: vesselData, sheetNameL10n: { values: { en: 'Vessel 1' } }, tableInstanceId: 729,
        },
        {
          rowKey: 'R1', columnCode: 'A', oldValue: 70, newValue: 51, tableCode: 'T1',
          tableNameL10n: vesselData, sheetNameL10n: { values: { en: 'Vessel 2' } }, tableInstanceId: 730,
        },
      ],
    });

    await openPreview();

    // Свої значення кожної копії, а не значення одного аркуша під обома.
    const copy = within(screen.getByRole('table', { name: 'Vessel 2 · Vessel data · R1' }));
    expect(copy.getByRole('cell', { name: '70' })).toBeTruthy();
    expect(copy.getByRole('cell', { name: '51' })).toBeTruthy();
    const original = within(screen.getByRole('table', { name: 'Vessel 1 · Vessel data · R1' }));
    expect(original.getByRole('cell', { name: '7' })).toBeTruthy();

    await userEvent.click(screen.getByRole('checkbox', { name: /⟦import\.overwriteRow⟧ — Vessel 2 · Vessel data · R1/ }));
    // Оригінал лишає чуже — явною згодою.
    await userEvent.click(screen.getByRole('checkbox', { name: '⟦import.overwriteSkip⟧' }));
    await userEvent.click(screen.getByRole('button', { name: '⟦import.apply⟧' }));

    await waitFor(() => expect(applied).toHaveLength(1));
    expect(applied[0]).toEqual({
      previewToken: 'tok',
      overwriteRows: [{ tableCode: 'T1', rowKey: 'R1', tableInstanceId: 730 }],
    });
  });
});
