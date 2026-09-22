import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import SnapshotRowsModal from '@/features/reports/SnapshotRowsModal';
import { resetMissingReports } from '@/shared/i18n';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { renderWithMantine, renderWithQuery } from '@/test/render';
import { formatDecimal } from '../number';

/**
 * ОДНЕ правило подання десяткового рядка — ОДНЕ місце.
 *
 * ⛔ Навіщо окремий файл, коли в кожного споживача вже є свій набір: до цього
 * `formatDecimal` існував у ТРЬОХ примірниках — канонічний у
 * `shared/format/number.ts` і дві копії в `DataTable.tsx` та
 * `SnapshotRowsModal.tsx`. Набори споживачів не ловили розходження між ними ані
 * до зведення, ані після: кожен перевіряв СВІЙ екран, а копія, що поїхала, на
 * своєму екрані виглядає бездоганно. Ловить її лише твердження, яке дивиться на
 * обидва екрани одразу — тобто це.
 *
 * ⚠ Стелі дробової частини в двох екранів РІЗНІ й були різними до зведення:
 * перелік — три знаки (дефолт `Intl`), зріз звітності — десять (`D15-09`).
 * Різниця збережена аргументом `maxFractionDigits`, і вона тут зафіксована
 * явно (`описано нижче`) — щоб наступна зміна стелі була рішенням, а не
 * випадковістю.
 */

/**
 * Значення, однакове для ОБОХ стель: дробових знаків три, тобто і межа 3, і
 * межа 10 показують його цілком. Саме на такому вході обидва екрани зобов'язані
 * дати той самий рядок — і саме його ламає будь-яка зміна канонічної функції.
 *
 * ⛔ Двадцять значущих цифр узято не для округлості: `double` на цьому вході
 * викидає дробову частину ЦІЛКОМ і змінює останню цифру цілої —
 * `String(Number('12345678901234567.125')) === '12345678901234568'` (заміряно
 * першим твердженням файлу, а не взято на віру). На шістнадцяти знаках
 * `String(Number(…))` повертає той самий рядок, і мутація «пустити через
 * `Number`» лишилася б зеленою.
 */
const Same = '12345678901234567.125';
const SameThroughDouble = '12,345,678,901,234,568';
const SameShown = '12,345,678,901,234,567.125';

/**
 * Значення, на якому стелі РОЗХОДЯТЬСЯ: шістнадцять дробових знаків.
 * Перелік ріже до трьох, зріз — до десяти.
 */
const Deep = '1234.1234567890123456';
const DeepInGrid = '1,234.123';
const DeepInSnapshot = '1,234.123456789';

/** Нерозривні пробіли ICU → звичайний: інакше падіння нечитабельне. */
function norm(text: string | null): string | null {
  return text === null ? null : text.replace(/[   ]/g, ' ');
}

/* ── перелік (`DataTable`) ─────────────────────────────────────────────────── */

interface Reading {
  readonly id: string;
  readonly amount: string;
}

const gridColumns: readonly DataTableColumn<Reading>[] = [{ key: 'amount', label: 'Обсяг', num: true }];

/** Те, що показує числова клітинка переліку для заданого рядка. */
function shownInGrid(value: string): string {
  renderWithMantine(
    <DataTable<Reading>
      columns={gridColumns}
      rows={[{ id: 'a', amount: value }]}
      rowKey={(row) => row.id}
    />,
  );

  const cell = document.querySelector('tbody tr td');

  return norm(cell?.textContent ?? '') ?? '';
}

/* ── зріз звітності (`SnapshotRowsModal`) ──────────────────────────────────── */

/** Стеля очікування даних — дані з мока, тож секунди, а не хвилини. */
const WaitCeiling = 10_000;

/** Стеля тесту цілком: модалка тягне за собою запит і повне дерево Mantine. */
const TestCeiling = 30_000;

const snapshotColumns = [
  { code: 'Marker', kind: 'text', name: 'Marker' },
  { code: 'Value', kind: 'number', name: 'Value' },
];

/** Те, що показує числова клітинка зрізу для заданого рядка. */
async function shownInSnapshot(value: string): Promise<string> {
  vi.stubGlobal('fetch', () =>
    Promise.resolve(
      new Response(
        JSON.stringify({
          columns: snapshotColumns,
          rows: [{ rowNo: 1, cells: { Marker: 'E_NOX', Value: value } }],
          nextCursor: null,
          showGroupHeader: false,
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    ),
  );

  renderWithQuery(<SnapshotRowsModal snapshotId={7} onClose={() => undefined} />);
  await screen.findByText('E_NOX', undefined, { timeout: WaitCeiling });

  // Порядок клітинок рядка: номер, `Marker`, `Value`.
  const cells = Array.from(document.querySelectorAll('tbody tr td'));

  return norm(cells[2]?.textContent ?? '') ?? '';
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetMissingReports();
});

describe('замір: чого саме коштує прохід через double', () => {
  it('Number(...) на цьому вході ТАКИ псує значення', () => {
    /*
     * ⛔ Без цього твердження решта файлу доводила б менше, ніж обіцяє: мутація
     * «повернути `Number` на шлях показу» мусить ЩОСЬ ламати, і ось
     * підтвердження, що на цьому вході вона ламає — дробова частина зникає
     * цілком, остання цифра цілої змінюється.
     */
    expect(String(Number(Same))).toBe('12345678901234568');
    expect(String(Number(Deep))).toBe('1234.1234567890124');
  });
});

describe('копій formatDecimal більше немає: обидва екрани дають той самий рядок', () => {
  it(
    'те саме значення в переліку і в зрізі звітності показане однаково',
    async () => {
      const inGrid = shownInGrid(Same);
      cleanup();

      const inSnapshot = await shownInSnapshot(Same);

      /*
       * ⛔ Це головне твердження файлу, і воно не про конкретний рядок, а про
       * ОДНЕ джерело: доки обидва екрани ходять у `shared/format/number.ts`,
       * вони не можуть розійтися. Мутуйте `MaxIntlFractionDigits` у канонічній
       * функції на `0` — падають обидва рядки нижче, і разом із ними набори
       * обох споживачів. Саме це й доводить, що копій не лишилося: копія
       * пережила б мутацію канону непоміченою.
       */
      expect(inGrid).toBe(inSnapshot);
      expect(inGrid).toBe(SameShown);

      // І вхід справді нетривіальний: через `number` обидва дали б оце.
      expect(inGrid).not.toBe(SameThroughDouble);
    },
    TestCeiling,
  );

  it('канонічна функція дає рівно те саме, що обидва екрани', () => {
    /*
     * ⚠ Проміжна ланка теж перевіряється: якби споживач додав власну обгортку
     * над каноном (округлення, заміну роздільника), твердження вище лишилося б
     * зеленим, поки обгортки збігаються. Це — ні.
     */
    expect(norm(formatDecimal(Same, undefined, 3))).toBe(SameShown);
    expect(norm(formatDecimal(Same, undefined, 10))).toBe(SameShown);
  });
});

describe('різниця стель збережена — і названа, а не мовчазна', () => {
  it(
    'глибокий дріб: перелік ріже до трьох знаків, зріз — до десяти',
    async () => {
      /*
       * ⛔ Це НЕ дефект і не наслідок зведення копій: так було й до нього
       * (`new Intl.NumberFormat(locale)` без опцій у `DataTable` — дефолт три
       * знаки; `maximumFractionDigits: 10` у `SnapshotRowsModal` — `D15-09`).
       * Твердження стоїть тут, щоб наступна зміна будь-якої з двох стель була
       * видимим рішенням: подання на екрані регуляторної звітності не можна
       * змінити мовчки.
       */
      const inGrid = shownInGrid(Deep);
      cleanup();

      const inSnapshot = await shownInSnapshot(Deep);

      expect(inGrid).toBe(DeepInGrid);
      expect(inSnapshot).toBe(DeepInSnapshot);
      expect(inGrid).not.toBe(inSnapshot);
    },
    TestCeiling,
  );

  it('стеля — аргумент канонічної функції, а не друга реалізація', () => {
    expect(norm(formatDecimal(Deep, undefined, 3))).toBe(DeepInGrid);
    expect(norm(formatDecimal(Deep, undefined, 10))).toBe(DeepInSnapshot);

    // Без стелі канон показує все, що є у значенні, — і це його дефолт.
    expect(norm(formatDecimal(Deep))).toBe('1,234.1234567890123456');
  });
});
