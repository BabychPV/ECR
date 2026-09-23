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
  /**
   * `U-06`: дріб не лишається голим.
   *
   * ⛔ Що саме доводиться. Тут стояв `toBe('2 / 3')` — і він лишався б
   * ЗЕЛЕНИМ при рівно тій ваді, про яку звіт: число правильне, а що воно
   * рахує, не сказано ніде. Тому перевіряється не саме число, а те, що
   * підпис береться з каталогу за ключем `document.tablesFilled` і несе
   * ОБИДВА числа. Каталог у компонентних тестах порожній, тож `t()`
   * навмисно повертає `⟦ключ (параметри)⟧` — саме в цьому вигляді ключ і
   * параметри видно в DOM.
   *
   * ⚠ Що текст за цим ключем справді СЛОВА, а не знову дріб, доводить
   * сусідній `sheet-fill-summary.label.test.ts` — по самому `09-seed.sql`.
   */
  it('показує підпис із каталогу (ключ + обидва числа), а не голий дріб', async () => {
    respond([
      table({ tableDefId: 1, filledCells: 4, inputCells: 4 }),
      table({ tableDefId: 2, filledCells: 3, inputCells: 4 }),
      table({ tableDefId: 3, filledCells: 0, inputCells: 0 }),
    ]);

    show();

    await waitFor(() => {
      const text = screen.getByTestId('sheet-fill-count').textContent ?? '';

      expect(text).toContain('document.tablesFilled');
      expect(text).toContain('filled=2');
      expect(text).toContain('total=3');
    });

    // ⛔ І прямо: голого дробу «2 / 3» на екрані більше немає. Без цього
    // рядка тест лишився б зеленим, якби підпис приписали ПОРУЧ із дробом,
    // а сам дріб залишили — тобто вада «два показники, один без пояснення»
    // проїхала б.
    expect(screen.getByTestId('sheet-fill-count').textContent?.trim()).not.toBe('2 / 3');
  });

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
      // ⚠ Числа читаються з параметрів підпису (`U-06`): каталог у тестах
      // порожній, тож `t()` віддає `⟦ключ (filled=2, total=3)⟧`.
      const text = screen.getByTestId('sheet-fill-count').textContent ?? '';

      expect(text).toContain('filled=2');
      expect(text).toContain('total=3');
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
