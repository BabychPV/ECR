import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { renderWithMantine } from '@/test/render';

/**
 * Три межі `DataTable`, які екрани обходили власним кодом:
 *
 *   1. число форматувалося в БУДЬ-ЯКІЙ колонці, навіть без `num` — ідентифікатор
 *      `1234` ставав `1,234`;
 *   2. `num` склеював вирівнювання з округленням до трьох знаків — множник
 *      `0.4535923700` ставав `0.454`, і відмовитися від цього було нічим;
 *   3. початковий порядок доводилося пресортовувати в екрані, і шапка про нього
 *      мовчала (`aria-sort="none"` на впорядкованій колонці).
 *
 * ⛔ Кожне твердження нижче має мутацію в `DataTable.tsx`, на якій воно падає;
 * мутації названі поруч. Мутації екранів (`render: (x) => x.id` замість
 * `String(x.id)`) тут не годяться: результат `render` іде на екран повз
 * форматування набору, тож такі правки не зачіпають шляху, який перевіряється.
 */

interface Row {
  readonly key: string;
  readonly id: number;
  readonly group: string;
  readonly factor: string;
  readonly count: number;
}

function row(key: string, id: number, group: string, factor: string, count = 0): Row {
  return { key, id, group, factor, count };
}

function renderedOrder(): string[] {
  return Array.from(document.querySelectorAll('tbody tr')).map(
    (tr) => tr.getAttribute('data-row-key') ?? '',
  );
}

function headerOf(label: string): HTMLElement {
  const th = screen.getByRole('button', { name: label }).closest('th');

  expect(th, `колонки «${label}» немає в шапці`).not.toBeNull();
  return th as HTMLElement;
}

afterEach(cleanup);

describe('Межа 1: число форматується лише у величини (`num`)', () => {
  it('ідентифікатор 1234 у колонці без `num` показується як 1234, не 1,234', () => {
    /*
     * ⚠ Саме чотири цифри: на `7` роздільник розрядів не з'являється, і
     * твердження було б зеленим і без фіксу.
     *
     * ⛔ Мутаційний доказ: у `Cell` поверніть безумовне
     * `if (typeof raw === 'number') return <>{formatNumber(raw)}</>;` — клітинка
     * покаже `1,234`, і обидва рядки нижче падають.
     */
    renderWithMantine(
      <DataTable<Row>
        columns={[{ key: 'id', label: 'Id' }]}
        rows={[row('a', 1234, 'G', '1')]}
        rowKey={(r) => r.key}
      />,
    );

    expect(screen.getByText('1234')).toBeDefined();
    expect(screen.queryByText('1,234')).toBeNull();
  });

  it('дзеркало: лічильник, що сказав `num`, роздільники розрядів зберігає', () => {
    /*
     * ⛔ Мутаційний доказ: замініть `magnitude ? formatNumber(raw) : String(raw)`
     * на самий `String(raw)` — `1,234` зникне, і рядок падає (разом із
     * «дзеркалом (Г)» у `DataTable.decimal.test.tsx`). Тобто фікс не
     * «прибрав форматування чисел», а прив'язав його до `num`.
     */
    renderWithMantine(
      <DataTable<Row>
        columns={[{ key: 'count', label: 'Count', num: true }]}
        rows={[row('a', 1, 'G', '1', 1234)]}
        rowKey={(r) => r.key}
      />,
    );

    expect(screen.getByText('1,234')).toBeDefined();
  });
});

describe('Межа 2: `exact` — числова колонка без округлення', () => {
  /**
   * ⛔ 20 значущих цифр — `double` їх гарантовано псує. Заміряно (Node 24):
   *     String(Number('1234.5678901234567891'))  // '1234.567890123457'
   *     new Intl.NumberFormat('en').format(Number(...))  // '1,234.568'
   * Тобто будь-який шлях через `number` або через `Intl` дав би інший текст.
   */
  const Twenty = '1234.5678901234567891';

  /** Множник одиниці з масштабом колонки: `Number(...)` дає `0.45359237`. */
  const Factor = '0.4535923700';

  const columns: readonly DataTableColumn<Row>[] = [
    { key: 'factor', label: 'Factor', num: true, exact: true },
  ];

  it('рядок сервера показується повністю, включно з масштабом колонки', () => {
    /*
     * ⛔ Мутаційний доказ: у `Cell` приберіть гілку
     * `if (magnitude && column.exact === true)` — клітинка піде через
     * `formatDecimal(raw, undefined, CellFractionCeiling)` і покаже `0.454` та
     * `1,234.568`; обидва `getByText` падають.
     */
    renderWithMantine(
      <DataTable<Row>
        columns={columns}
        rows={[row('a', 1, 'G', Factor), row('b', 2, 'G', Twenty)]}
        rowKey={(r) => r.key}
      />,
    );

    expect(screen.getByText(Factor)).toBeDefined();
    expect(screen.getByText(Twenty)).toBeDefined();

    // І доказ, що вхід нетривіальний: через `number` обидва значення змінюються.
    expect(String(Number(Twenty))).toBe('1234.567890123457');
    expect(String(Number(Factor))).toBe('0.45359237');
  });

  it('`exact` зберігає від `num` вирівнювання праворуч і моноширинність', () => {
    renderWithMantine(
      <DataTable<Row>
        columns={columns}
        rows={[row('a', 1, 'G', Factor)]}
        rowKey={(r) => r.key}
      />,
    );

    const td = screen.getByText(Factor).closest('td');

    expect(td?.style.textAlign || td?.getAttribute('align')).toBe('right');
  });

  it('дзеркало: звичайна `num`-колонка округлює, як і раніше (дефолт не змінено)', () => {
    /*
     * ⛔ Мутаційний доказ: зробіть гілку `exact` безумовною для `num`
     * (`if (magnitude)` замість `if (magnitude && column.exact === true)`) —
     * `0.454` зникне, і рядок падає. Дефолт у три знаки — відкрите рішення
     * людини, і `exact` мусить бути відмовою ОДНІЄЇ колонки, а не всіх.
     */
    renderWithMantine(
      <DataTable<Row>
        columns={[{ key: 'factor', label: 'Factor', num: true }]}
        rows={[row('a', 1, 'G', Factor), row('b', 2, 'G', Twenty)]}
        rowKey={(r) => r.key}
      />,
    );

    expect(screen.getByText('0.454')).toBeDefined();
    expect(screen.getByText('1,234.568')).toBeDefined();
    expect(screen.queryByText(Factor)).toBeNull();
  });
});

describe('Межа 3: `defaultSort` — порядок і `aria-sort` на старті', () => {
  /**
   * ⚠ Порядок сервера НЕ збігається з жодним очікуваним: інакше «відсортовано»
   * і «як прийшло» виглядали б однаково.
   */
  const rows: readonly Row[] = [
    row('b2', 1, 'B', '2'),
    row('a9', 2, 'A', '9'),
    row('b1', 3, 'B', '10'),
    row('a1', 4, 'A', '0.5'),
  ];

  const columns: readonly DataTableColumn<Row>[] = [
    { key: 'group', label: 'Group' },
    { key: 'factor', label: 'Factor', num: true },
  ];

  it('таблиця відкривається відсортованою, і шапка про це каже', () => {
    /*
     * ⛔ Мутаційний доказ: `useState<SortState | null>(null)` замість
     * `useState(defaultSort ?? null)` — порядок лишиться серверним
     * (`b2, a9, b1, a1`), `aria-sort` — `none`; падають обидва твердження.
     *
     * ⚠ Числовий порядок `0.5, 2, 9, 10` не збігається з текстовим
     * (`0.5, 10, 2, 9`): `defaultSort` іде тим самим компаратором, що й клац.
     */
    renderWithMantine(
      <DataTable<Row>
        columns={columns}
        rows={rows}
        rowKey={(r) => r.key}
        defaultSort={{ key: 'factor', direction: 'asc' }}
      />,
    );

    expect(renderedOrder()).toEqual(['a1', 'b2', 'a9', 'b1']);
    expect(headerOf('Factor').getAttribute('aria-sort')).toBe('ascending');
    expect(headerOf('Group').getAttribute('aria-sort')).toBe('none');
  });

  it('спадання на старті — теж правда в шапці', () => {
    renderWithMantine(
      <DataTable<Row>
        columns={columns}
        rows={rows}
        rowKey={(r) => r.key}
        defaultSort={{ key: 'factor', direction: 'desc' }}
      />,
    );

    expect(renderedOrder()).toEqual(['b1', 'a9', 'b2', 'a1']);
    expect(headerOf('Factor').getAttribute('aria-sort')).toBe('descending');
  });

  it('клац продовжує цикл ВІД початкового стану; третій — порядок сервера', () => {
    renderWithMantine(
      <DataTable<Row>
        columns={columns}
        rows={rows}
        rowKey={(r) => r.key}
        defaultSort={{ key: 'factor', direction: 'asc' }}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Factor' }));
    expect(headerOf('Factor').getAttribute('aria-sort')).toBe('descending');
    expect(renderedOrder()).toEqual(['b1', 'a9', 'b2', 'a1']);

    fireEvent.click(screen.getByRole('button', { name: 'Factor' }));
    expect(headerOf('Factor').getAttribute('aria-sort')).toBe('none');
    expect(renderedOrder()).toEqual(['b2', 'a9', 'b1', 'a1']);
  });

  it('кортеж у `sortValue`: група, усередині неї — друге поле', () => {
    /*
     * ⛔ Мутаційний доказ: у кортежній гілці `compareKeys` обмежте цикл першим
     * елементом (`index < 1` замість `index < Math.max(…)`) — усередині групи
     * лишиться порядок сервера (`a9` перед `a1`, `b2` перед `b1`), і рядок
     * падає. Разом із ним падає `UnitsPage.kitTable` «рядки йдуть за
     * розмірністю, усередині неї — за кодом».
     */
    renderWithMantine(
      <DataTable<Row>
        columns={[
          { key: 'group', label: 'Group', sortValue: (r) => [r.group, r.key] },
          { key: 'factor', label: 'Factor', num: true },
        ]}
        rows={rows}
        rowKey={(r) => r.key}
        defaultSort={{ key: 'group', direction: 'asc' }}
      />,
    );

    expect(renderedOrder()).toEqual(['a1', 'a9', 'b1', 'b2']);
    expect(headerOf('Group').getAttribute('aria-sort')).toBe('ascending');
  });
});
