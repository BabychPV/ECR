import { describe, it, expect, vi, afterEach } from 'vitest';
import { screen } from '@testing-library/react';
import SnapshotRowsModal from '@/features/reports/SnapshotRowsModal';
import { resetMissingReports } from '@/shared/i18n';
import { renderWithQuery } from '@/test/render';

/**
 * Макет зрізу на екрані (`R8`): групи, підсумки — і третій стан, коли версія
 * не оголошує ні того, ні того.
 *
 * ⛔ Три випадки навмисно НЕ перетинаються даними: у випадку А підсумків немає
 * зовсім, у Б — груп, у В — нічого. Інакше одна поломка валила б два тести, і
 * падіння перестало б називати причину.
 *
 * ⚠ Каталог тут не вантажиться: `t()` віддає позначений ключ (`⟦ключ⟧`), і
 * перевірка дивиться саме на ключ. Підставляти власні рядки означало б
 * перевіряти переклад, якого в `09-seed.sql` ще немає.
 */

/** Стеля очікування даних — секунди, а не хвилини: дані тут з мока. */
const WaitCeiling = 10_000;

/** Стеля тесту цілком. */
const TestCeiling = 30_000;

/** Те, що показує `t()` без каталогу (`D-138`, `i18n/index.ts`). */
function miss(key: string, params?: string): string {
  return `⟦${key}${params === undefined ? '' : ` (${params})`}⟧`;
}

const columns = [
  { code: 'OutputCode', kind: 'text' },
  { code: 'Value', kind: 'number' },
];

const rows = [
  { rowNo: 1, cells: { OutputCode: 'E_CO2', Value: 12.5 } },
  { rowNo: 2, cells: { OutputCode: 'E_CO2', Value: 7.5 } },
  { rowNo: 3, cells: { OutputCode: 'E_NOX', Value: 3 } },
];

/** Сторінка рядків: спільні колонки й рядки, різний макет. */
function page(layout: Record<string, unknown>): unknown {
  return { columns, rows, nextCursor: null, showGroupHeader: false, ...layout };
}

function stubRows(body: unknown): void {
  vi.stubGlobal('fetch', () =>
    Promise.resolve(
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  );
}

/**
 * Кожен рядок тіла таблиці — одним рядком тексту: ЩО це (заголовок групи,
 * підсумок чи дані) і що в ньому видно.
 *
 * ⚠ Саме послідовність і доводить твердження «рядки під своєю групою»: окремо
 * взяті «заголовок є» і «рядок є» проходять і тоді, коли рядок стоїть не там.
 */
function shape(): string[] {
  return Array.from(document.querySelectorAll('tbody tr')).map((tr) => {
    const cells = Array.from(tr.querySelectorAll('td')).map((td) => td.textContent ?? '');
    const groupHead = tr.querySelector('th[scope="colgroup"]');

    if (groupHead !== null) return `group:${groupHead.textContent ?? ''}`;

    const rowHead = tr.querySelector('th[scope="row"]');

    if (rowHead !== null) return `total:${rowHead.textContent ?? ''}|${cells.join('|')}`;

    return `row:${cells.join('|')}`;
  });
}

/** Чекає ПРИХОДУ даних: до нього таблиці немає взагалі, і будь-яке дзеркальне
 *  твердження було б правдою просто тому, що малювати ще нічого. */
async function shown(): Promise<void> {
  await screen.findByText('E_NOX', undefined, { timeout: WaitCeiling });
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetMissingReports();
});

describe('SnapshotRowsModal: макет зрізу (R8)', () => {
  it(
    'випадок А: групи з сервера — заголовок видно, рядки під своєю групою',
    async () => {
      stubRows(
        page({
          groups: [
            { column: 'OutputCode', value: 'E_CO2', rowCount: 2, totals: [] },
            { column: 'OutputCode', value: 'E_NOX', rowCount: 1, totals: [] },
          ],
          totals: null,
        }),
      );

      renderWithQuery(<SnapshotRowsModal snapshotId={7} onClose={() => undefined} />);
      await shown();

      expect(shape()).toEqual([
        `group:${miss('snapshots.rowsGroup', 'column=OutputCode')}: E_CO2`,
        'row:1|E_CO2|12.5',
        'row:2|E_CO2|7.5',
        `group:${miss('snapshots.rowsGroup', 'column=OutputCode')}: E_NOX`,
        'row:3|E_NOX|3',
      ]);
    },
    TestCeiling,
  );

  it(
    'випадок Б: підсумок названий — видно функцію і колонку, за якою рахований',
    async () => {
      stubRows(
        page({
          groups: null,
          totals: [
            { column: 'Value', fn: 'sum', value: 23 },
            { column: 'OutputCode', fn: 'count', value: 3 },
          ],
        }),
      );

      renderWithQuery(<SnapshotRowsModal snapshotId={7} onClose={() => undefined} />);
      await shown();

      /*
       * ⚠ Позиція порожніх комірок тут значуща, а не випадкова: значення стоїть
       * у КОЛОНЦІ свого підсумку (сума — під `Value`, кількість — під
       * `OutputCode`), і саме це відповідає на «за яким полем рахований».
       */
      expect(shape().filter((line) => line.startsWith('total:'))).toEqual([
        `total:${miss('snapshots.rowsTotalAll')}||${miss('snapshots.rowsFnSum')}: 23`,
        `total:${miss('snapshots.rowsTotalAll')}|${miss('snapshots.rowsFnCount')}: 3|`,
      ]);
    },
    TestCeiling,
  );

  it(
    'випадок В (дзеркало): без груп і підсумків таблиця така сама, як до R8',
    async () => {
      // Полів `groups`/`totals` у відповіді немає ЗОВСІМ — версія макета не
      // оголошує. Це третій стан, а не відмова й не порожній зріз.
      stubRows(page({}));

      renderWithQuery(<SnapshotRowsModal snapshotId={7} onClose={() => undefined} />);
      await shown();

      // Жодного заголовка групи, жодного «Разом: —» — рівно три рядки даних.
      expect(shape()).toEqual(['row:1|E_CO2|12.5', 'row:2|E_CO2|7.5', 'row:3|E_NOX|3']);

      // І жодної зайвої колонки-заголовка: шапка та сама, що була.
      expect(screen.getAllByRole('columnheader').map((th) => th.textContent)).toEqual([
        '#',
        'OutputCode',
        'Value',
      ]);
    },
    TestCeiling,
  );
});
