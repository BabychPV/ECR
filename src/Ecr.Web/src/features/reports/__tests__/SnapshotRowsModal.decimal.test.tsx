import { describe, it, expect, vi, afterEach } from 'vitest';
import { screen } from '@testing-library/react';
import SnapshotRowsModal from '@/features/reports/SnapshotRowsModal';
import { resetMissingReports } from '@/shared/i18n';
import { renderWithQuery } from '@/test/render';

/**
 * Числові комірки зрізу після `e470777a`: сервер віддає `decimal` РЯДКОМ, бо
 * `JSON.parse` беззворотно губить 16-й знак.
 *
 * ⛔ Гілка `typeof value === 'number'` у `cellText` на рядок не спрацьовує, і
 * наслідок мовчазний: на екрані регуляторної звітності лишається сире
 * `12345678901234567.8901234567` — без роздільників розрядів і з хвостом нулів
 * масштабу колонки (`1.0000000000` замість `1`).
 *
 * ⚠ Число підібране так, щоб `double` його ГАРАНТОВАНО зіпсував:
 * `String(Number('12345678901234567.8901234567'))` дає `'12345678901234568'` —
 * дробова частина зникає цілком, а остання цифра цілої змінюється. На вході
 * виду `0.4535923700000001` `String(Number(…))` повертає те саме, і мутація
 * «пропустити через `Number`» лишилася б зеленою.
 */

/** Стеля очікування даних — секунди, а не хвилини: дані тут з мока. */
const WaitCeiling = 10_000;

/** Стеля тесту цілком. */
const TestCeiling = 30_000;

/** Десяткове з 27 значущими цифрами: `decimal(28,16)` таке несе, `double` — ні. */
const Exact = '12345678901234567.8901234567';

/** Те, як `Exact` мусить виглядати на екрані (локаль продукту — `en`). */
const Shown = '12,345,678,901,234,567.8901234567';

/** Те, у що `double` перетворює `Exact` ще до будь-якого форматування. */
const Lossy = '12,345,678,901,234,568';

/*
 * ⚠ `kind` тут — не оздоблення: саме він, а не вигляд значення, вмикає числове
 * форматування. `OutputCode` навмисно несе `'007'` — рядок, який
 * `normalizeDecimal` цілком законно зводить до `'7'`.
 */
const columns = [
  { code: 'OutputCode', kind: 'text', name: 'OutputCode' },
  { code: 'Value', kind: 'number', name: 'Value' },
  { code: 'PeriodKey', kind: 'number', name: 'PeriodKey' },
  { code: 'SourceId', kind: 'number', name: 'SourceId' },
];

const rows = [
  { rowNo: 1, cells: { OutputCode: '007', Value: Exact, PeriodKey: 202603, SourceId: '12345678' } },
  {
    rowNo: 2,
    cells: { OutputCode: 'E_NOX', Value: '5.0000000000', PeriodKey: 202603, SourceId: 12345678 },
  },
  {
    rowNo: 3,
    cells: { OutputCode: 'E_SO2', Value: 1234.5, PeriodKey: 202603, SourceId: null },
  },
];

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

/** Кожен рядок тіла — своїми клітинками в порядку колонок. */
function cells(): string[][] {
  return Array.from(document.querySelectorAll('tbody tr')).map((tr) =>
    Array.from(tr.querySelectorAll('td')).map((td) => td.textContent ?? ''),
  );
}

/** Чекає ПРИХОДУ даних: до нього таблиці немає, і будь-яке твердження порожнє. */
async function shown(): Promise<void> {
  await screen.findByText('E_NOX', undefined, { timeout: WaitCeiling });
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetMissingReports();
});

describe('Середовище: Intl приймає рядок', () => {
  it('Intl.NumberFormat.format форматує десятковий РЯДОК, не лише number', () => {
    /*
     * ⛔ Це не перевірка платформи «про всяк випадок», а підстава для
     * приведення типу в `SnapshotRowsModal.tsx`: `tsconfig.json` стоїть на
     * `lib: ES2022`, де перевантаження `format(string)` ще немає, тож
     * компілятор цю можливість підтвердити не може. Якщо ICU колись перестане
     * приймати рядок, впаде саме цей рядок — з назвою причини.
     */
    const intl = new Intl.NumberFormat('en', { maximumFractionDigits: 10 });
    const format = intl.format as unknown as (value: string) => string;

    expect(format(Exact)).toBe(Shown);

    // І доказ, що вхід справді нетривіальний: через `number` те саме значення
    // втрачає і всю дробову частину, і останню цифру цілої.
    expect(intl.format(Number(Exact))).toBe(Lossy);
  });
});

describe('SnapshotRowsModal: десяткове приходить рядком', () => {
  it(
    'випадок А: 27 значущих цифр доходять до екрана без втрати знаків',
    async () => {
      stubRows({ columns, rows, nextCursor: null, showGroupHeader: false });

      renderWithQuery(<SnapshotRowsModal snapshotId={7} onClose={() => undefined} />);
      await shown();

      /*
       * ⛔ Мутаційний доказ №1: у `cellText` приберіть гілку
       * `kind === NumberKind && typeof value === 'string'` — комірка покаже
       * сире `12345678901234567.8901234567`.
       * ⛔ Мутаційний доказ №2: замініть `formatDecimal(canonical)` на
       * `formatNumber(Number(canonical), CellNumberOptions)` — покаже
       * `12,345,678,901,234,568`.
       */
      expect(cells()[0]).toEqual(['1', '007', Shown, '202603', '12345678']);

      expect(screen.queryByText(Exact)).toBeNull();
      expect(screen.queryByText(Lossy)).toBeNull();
    },
    TestCeiling,
  );

  it(
    'хвіст нулів масштабу колонки не їде на екран',
    async () => {
      stubRows({ columns, rows, nextCursor: null, showGroupHeader: false });

      renderWithQuery(<SnapshotRowsModal snapshotId={7} onClose={() => undefined} />);
      await shown();

      // `decimal(28,16)` несе масштаб КОЛОНКИ, а не значення: та сама одиниця
      // приходить як `'5'` або `'5.0000000000'`. На екрані це одне число.
      expect(cells()[1]?.[2]).toBe('5');
    },
    TestCeiling,
  );

  it(
    'випадок В: ідентифікатори роздільників розрядів НЕ отримують',
    async () => {
      stubRows({ columns, rows, nextCursor: null, showGroupHeader: false });

      renderWithQuery(<SnapshotRowsModal snapshotId={7} onClose={() => undefined} />);
      await shown();

      /*
       * ⛔ Мутаційний доказ: приберіть `if (IdentifierCode.test(code)) return
       * String(value);` — `PeriodKey` стане `202,603`, а `SourceId` —
       * `12,345,678`, тобто ключ періоду й ключ джерела почнуть виглядати як
       * величини.
       *
       * ⚠ Обидва рядки значущі: у першому `SourceId` приходить РЯДКОМ (як
       * тепер їде `decimal`), у другому — числом. Правило про ідентифікатори
       * стоїть ПЕРЕД обома гілками типу саме тому, що тип значення його не
       * визначає.
       */
      expect(cells()[0]?.slice(3)).toEqual(['202603', '12345678']);
      expect(cells()[1]?.slice(3)).toEqual(['202603', '12345678']);
    },
    TestCeiling,
  );

  it(
    'дзеркало (Г): текстова колонка числового поводження НЕ отримує',
    async () => {
      stubRows({ columns, rows, nextCursor: null, showGroupHeader: false });

      renderWithQuery(<SnapshotRowsModal snapshotId={7} onClose={() => undefined} />);
      await shown();

      /*
       * ⛔ Мутаційний доказ: приберіть умову `kind === NumberKind` (лишивши
       * `typeof value === 'string'`) — `'007'` перетвориться на `7`, і рядок
       * падає. Колонка не є числовою лише тому, що її значення схоже на число.
       */
      expect(cells()[0]?.[1]).toBe('007');
    },
    TestCeiling,
  );

  it(
    'дзеркало (Г): справжній `number` форматується як раніше',
    async () => {
      stubRows({ columns, rows, nextCursor: null, showGroupHeader: false });

      renderWithQuery(<SnapshotRowsModal snapshotId={7} onClose={() => undefined} />);
      await shown();

      // Гілка `typeof value === 'number'` лишилася незмінною: роздільник
      // розрядів є, дробова частина на місці.
      expect(cells()[2]?.[2]).toBe('1,234.5');

      // А порожня комірка — далі прочерк, а не `0` і не порожнеча.
      expect(cells()[2]?.[4]).toBe('—');
    },
    TestCeiling,
  );

  it(
    'підсумок числової колонки форматується так само, як її комірки',
    async () => {
      stubRows({
        columns,
        rows,
        nextCursor: null,
        showGroupHeader: false,
        groups: null,
        totals: [
          { column: 'Value', fn: 'sum', value: Exact },
          { column: 'OutputCode', fn: 'count', value: 3 },
        ],
      });

      renderWithQuery(<SnapshotRowsModal snapshotId={7} onClose={() => undefined} />);
      await shown();

      /*
       * ⛔ Підсумок іде тією самою дорогою, що й комірка (`valueNode` →
       * `cellText`), і це не дубль попереднього твердження: сума по всьому
       * зрізу — єдине число, яке читає регулятор, і показати його сирим рядком
       * коштувало б дорожче за будь-яку окрему комірку.
       */
      const totals = Array.from(document.querySelectorAll('tbody tr'))
        .filter((tr) => tr.querySelector('th[scope="row"]') !== null)
        .map((tr) => Array.from(tr.querySelectorAll('td')).map((td) => td.textContent ?? ''));

      expect(totals[0]?.[1]).toContain(Shown);
      expect(totals[0]?.[1]).not.toContain(Lossy);
    },
    TestCeiling,
  );
});
