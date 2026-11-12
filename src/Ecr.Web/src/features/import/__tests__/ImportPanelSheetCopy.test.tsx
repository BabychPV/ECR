import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ImportPreview } from '@/api/types';
import { ImportPanel } from '@/features/import/ImportPanel';
import { testTheme } from '@/test/render';

/**
 * ✎ B20 (F13 #829, C-3, P3): у групі аркуша з копіями та сама таблиця стоїть на «Vessel 1» і «Vessel 2», і рядок
 * перегляду з лише назвою таблиці («Vessel data») не казав, ЯКИЙ аркуш зміниться чи відхилено. Тепер — «Vessel 2 ·
 * Vessel data» (назву аркуша дає сервер, `sheetNameL10n`, правило `SheetCopyNaming`); аркуш без копій — як доти.
 *
 * ⛔ Червоне без фіксу: `tableOf` брав лише `tableNameL10n` — обидва рядки змін показували «Vessel data», відмова
 * теж; і ключі рядків змін збігались (`T1:R1:C1` на обох аркушах).
 *
 * ⚠ Відповідь прев'ю підмінено на рівні `fetch` (як `ImportPanelReplacesFormula.test.tsx`).
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

async function openPreview(): Promise<HTMLElement> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const { container } = render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ImportPanel documentId={1} periodKey={202610} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  const input = container.querySelector('input[type="file"]');
  if (!(input instanceof HTMLInputElement)) throw new Error('немає поля вибору файлу');

  await userEvent.upload(input, new File(['x'], 'book.xlsx'));

  return screen.findByRole('dialog');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const vesselData = { values: { en: 'Vessel data' } };
const vessel1 = { values: { en: 'Vessel 1' } };
const vessel2 = { values: { en: 'Vessel 2' } };

describe('ImportPanel: аркуш-копія в рядках перегляду (B20)', () => {
  it('зміни й відмова називають свій аркуш-копію; аркуш без копій — лише таблиця', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [
        { rowKey: 'R1', columnCode: 'C1', oldValue: 5, newValue: 7, tableCode: 'T1', tableNameL10n: vesselData, sheetNameL10n: vessel1 },
        { rowKey: 'R1', columnCode: 'C1', oldValue: 50, newValue: 51, tableCode: 'T1', tableNameL10n: vesselData, sheetNameL10n: vessel2 },
        { rowKey: 'R1', columnCode: 'C1', oldValue: 1, newValue: 2, tableCode: 'T9', tableNameL10n: { values: { en: 'Flares' } } },
      ],
      rejected: [
        {
          rowKey: 'R2',
          columnCode: 'C1',
          reasonCode: 'ECR-CELL-0422',
          message: 'diagnostic',
          messageKey: 'err.ECR-CELL-0422.importExpectsNumber',
          excelCell: 'A4',
          tableCode: 'T1',
          tableNameL10n: vesselData,
          sheetNameL10n: vessel2,
        },
      ],
      conflicts: [],
      overwritable: [],
    });

    const dialog = await openPreview();
    const rows = within(dialog).getAllByRole('row');
    const firstCells = rows
      .map((row) => row.querySelector('td')?.textContent ?? null)
      .filter((text): text is string => text !== null);

    // Дві зміни ТІЄЇ САМОЇ таблиці на різних аркушах — розрізнені; таблиця без копій — як доти.
    expect(firstCells).toEqual(['Vessel 1 · Vessel data', 'Vessel 2 · Vessel data', 'Flares', 'Vessel 2 · Vessel data']);

    // Рядок відмови — саме копії «Vessel 2».
    const rejectedRow = rows.find((row) => row.textContent?.includes('A4'));
    expect(rejectedRow?.querySelector('td')?.textContent).toBe('Vessel 2 · Vessel data');
  });
});
