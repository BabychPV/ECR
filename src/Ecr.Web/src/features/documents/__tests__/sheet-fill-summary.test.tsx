import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetFillSummary } from '@/features/documents/SheetFillSummary';
import { summarize, type TableStatus } from '@/features/documents/api';
import { testTheme } from '@/test/render';

/**
 * Споживач `GET /api/v1/documents/{id}/tables/status` (`BE-10`).
 *
 * ⛔ Перевіряється рівно те, чим на клієнті найлегше збрехати: «перевірку не
 * запускали» не має виглядати як «порушень немає». На сервері цей поділ несе
 * `null` у `errorCount`; тут доводиться, що клієнт його НЕ втрачає —
 * `errorCount: null` дає ВІДСУТНІСТЬ крапки, а не зелений стан, тоді як
 * `errorCount: 0` (перевірили, чисто) — теж відсутність, але вже інша за
 * змістом, і `errorCount: 1` — крапку.
 */
function respond(tables: TableStatus[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();

      if (!url.includes('/api/v1/documents/7/tables/status')) {
        throw new Error(`Немає мока для ${url}`);
      }

      return new Response(JSON.stringify(tables), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <SheetFillSummary documentId={7} periodKey={202601} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function table(patch: Partial<TableStatus>): TableStatus {
  return {
    tableDefId: 1,
    sheetCode: 'S1',
    filledCells: 0,
    inputCells: 4,
    errorCount: null,
    warningCount: null,
    ...patch,
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetFillSummary', () => {
  it('рахує заповненими лише таблиці, у яких закриті всі вхідні комірки', async () => {
    respond([
      table({ tableDefId: 1, filledCells: 4, inputCells: 4 }),
      table({ tableDefId: 2, filledCells: 3, inputCells: 4 }),

      // Таблиця без жодної вхідної комірки — усе формульне. Заповнювати
      // нічого, тож вона заповнена: інакше документ ніколи не дійшов би до
      // 100 %.
      table({ tableDefId: 3, filledCells: 0, inputCells: 0 }),
    ]);

    show();

    await waitFor(() => {
      expect(screen.getByTestId('sheet-fill-count').textContent).toBe('2 / 3');
    });
  });

  it('не показує крапку помилки, доки документ не перевіряли', async () => {
    respond([table({ errorCount: null, warningCount: null })]);

    show();

    await waitFor(() => {
      expect(screen.getByTestId('sheet-fill-summary')).toBeTruthy();
    });

    expect(screen.queryByTestId('sheet-fill-error-dot')).toBeNull();
  });

  it('показує крапку помилки, коли перевірка знайшла порушення', async () => {
    respond([table({ errorCount: 1, warningCount: 0 })]);

    show();

    await waitFor(() => {
      expect(screen.getByTestId('sheet-fill-error-dot')).toBeTruthy();
    });
  });

  it('відрізняє «перевірили, чисто» від «не перевіряли»', () => {
    // ⛔ Саме тут і ховається неправда, якщо `null` звести до нуля: обидва
    // випадки дали б `hasErrors === false`, і клієнт більше не мав би чим
    // відрізнити сірий стан від зеленого.
    expect(summarize([table({ errorCount: 0, warningCount: 0 })]).hasErrors).toBe(false);
    expect(summarize([table({ errorCount: null, warningCount: null })]).hasErrors).toBeNull();
  });
});
