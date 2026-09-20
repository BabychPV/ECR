import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { renderWithMantine } from '@/test/render';

/**
 * Десяткове в `DataTable` після `e470777a`: сервер віддає `decimal` РЯДКОМ, бо
 * `JSON.parse` беззворотно губить 16-й знак.
 *
 * ⛔ Наслідків два, і обидва мовчазні. Порівняння `typeof a === 'number'` у
 * `compareKeys` більше не спрацьовує — десяткова колонка йде через
 * `Intl.Collator`, тобто сортується як ТЕКСТ (`'10' < '9'`). Показ
 * `typeof raw === 'number'` у `Cell` так само не спрацьовує — у клітинці
 * лишається сире `9007199254740993.5` без роздільників розрядів.
 *
 * ⚠ Числа тут підібрані так, щоб `double` їх ГАРАНТОВАНО зіпсував: інакше
 * мутація «пропустити рядок через `Number`» лишилася б зеленою й нічого не
 * довела б (`String(Number('0.4535923700000001')) === '0.4535923700000001'`).
 */

/** Рядок переліку: `amount` приходить рядком, `count` — справжнім числом. */
interface Reading {
  readonly id: string;
  readonly code: string;
  readonly amount: string;
  readonly count: number;
}

/**
 * ⛔ `9007199254740993.5` обране навмисно: це `2^53 + 1` із половиною, тобто
 * перше місце, де IEEE-754 уже не має чим розрізнити сусідні значення.
 * `Number('9007199254740993.5')` дає `9007199254740994` — а `String(...)` від
 * нього повертає `'9007199254740994'`, тобто ані показ, ані порівняння через
 * `number` цього входу не переживають.
 */
const Exact = '9007199254740993.5';

/** Те, у що `double` перетворює `Exact` ще до будь-якого форматування. */
const Lossy = '9,007,199,254,740,994';

/** Те, як `Exact` мусить виглядати на екрані (локаль продукту — `en`). */
const Shown = '9,007,199,254,740,993.5';

const columns: readonly DataTableColumn<Reading>[] = [
  { key: 'code', label: 'Код' },
  { key: 'amount', label: 'Обсяг', num: true },
  { key: 'count', label: 'Кількість', num: true },
];

/**
 * ⚠ Порядок навмисно НЕ збігається з жодним очікуваним: масив приходить «як
 * віддав сервер», і будь-яке сортування мусить його змінити.
 *
 * ⚠ `'007'` у текстовій колонці — не декорація: це рядок, який
 * `normalizeDecimal` цілком законно зводить до `'7'`. Він тут, щоб числове
 * поводження не поїхало на колонку, якій воно не належить.
 */
const rows: readonly Reading[] = [
  { id: 'a', code: '007', amount: '9', count: 7 },
  { id: 'b', code: '010', amount: '10', count: 12 },
  { id: 'c', code: '009', amount: '9.5000000000', count: 3 },
];

function renderedOrder(): string[] {
  return Array.from(document.querySelectorAll('tbody tr')).map(
    (row) => row.getAttribute('data-row-key') ?? '',
  );
}

function sortBy(label: string): void {
  fireEvent.click(screen.getByRole('button', { name: label }));
}

afterEach(cleanup);

describe('Середовище: Intl приймає рядок', () => {
  it('Intl.NumberFormat.format форматує десятковий РЯДОК, не лише number', () => {
    /*
     * ⛔ Це не перевірка платформи «про всяк випадок», а підстава для
     * приведення типу в `DataTable.tsx`: `tsconfig.json` стоїть на `lib:
     * ES2022`, де перевантаження `format(string)` ще немає, тож компілятор про
     * цю можливість не знає й підтвердити її не може. Якщо ICU колись перестане
     * приймати рядок, впаде саме цей рядок — з назвою причини, а не десяток
     * тверджень про розмітку.
     */
    const intl = new Intl.NumberFormat('en');
    const format = intl.format as unknown as (value: string) => string;

    expect(format(Exact)).toBe(Shown);

    // І доказ, що вхід справді нетривіальний: через `number` те саме значення
    // втрачає і дробову частину, і останній знак цілої.
    expect(intl.format(Number(Exact))).toBe(Lossy);
  });
});

describe('Показ: десяткове з рядка доходить до екрана без втрати знаків', () => {
  it('випадок А: числова колонка форматується локаллю, а не лишається сирою', () => {
    renderWithMantine(
      <DataTable<Reading>
        columns={columns}
        rows={[{ id: 'a', code: 'X', amount: Exact, count: 1 }]}
        rowKey={(row) => row.id}
      />,
    );

    /*
     * ⛔ Мутаційний доказ №1: у `Cell` приберіть гілку `column.num === true`
     * разом із `formatDecimal` — клітинка покаже сире `9007199254740993.5`.
     * ⛔ Мутаційний доказ №2: замініть `formatDecimal(canonical)` на
     * `formatNumber(Number(canonical))` — покаже `9,007,199,254,740,994`.
     */
    expect(screen.getByText(Shown)).toBeDefined();
    expect(screen.queryByText(Exact)).toBeNull();
    expect(screen.queryByText(Lossy)).toBeNull();
  });

  it('хвіст нулів масштабу колонки не їде на екран', () => {
    renderWithMantine(
      <DataTable<Reading>
        columns={columns}
        rows={[{ id: 'a', code: 'X', amount: '5.0000000000', count: 1 }]}
        rowKey={(row) => row.id}
      />,
    );

    // `decimal(28,16)` несе масштаб КОЛОНКИ, а не значення: та сама одиниця
    // приходить як `'5'` або `'5.0000000000'` залежно від того, звідки її
    // прочитали. На екрані це одне й те саме число.
    expect(screen.getByText('5')).toBeDefined();
    expect(screen.queryByText('5.0000000000')).toBeNull();
  });

  it('дзеркало (Г): текстова колонка числового поводження НЕ отримує', () => {
    renderWithMantine(
      <DataTable<Reading> columns={columns} rows={rows} rowKey={(row) => row.id} />,
    );

    /*
     * ⛔ Мутаційний доказ: приберіть умову `column.num === true` в `Cell` —
     * `'007'` перетвориться на `7`, і цей рядок падає. Колонка кодів не є
     * числовою лише тому, що її значення схоже на число.
     */
    expect(screen.getByText('007')).toBeDefined();
    expect(screen.getByText('009')).toBeDefined();
  });

  it('дзеркало (Г): колонка справжніх `number` форматується як раніше', () => {
    renderWithMantine(
      <DataTable<Reading>
        columns={columns}
        rows={[{ id: 'a', code: 'X', amount: '1', count: 1234567 }]}
        rowKey={(row) => row.id}
      />,
    );

    // Гілка `typeof raw === 'number'` лишилася першою і незмінною.
    expect(screen.getByText('1,234,567')).toBeDefined();
  });
});

describe('Сортування: десяткова колонка — числом, а не текстом', () => {
  it('випадок Б: за спаданням `9` стоїть ПІСЛЯ `10`', () => {
    renderWithMantine(
      <DataTable<Reading> columns={columns} rows={rows} rowKey={(row) => row.id} />,
    );

    sortBy('Обсяг');
    sortBy('Обсяг');

    /*
     * ⛔ Мутаційний доказ: у `compareKeys` приберіть блок `if (numeric)` (або
     * передайте `false` замість `column.num === true`) — порівняння піде через
     * `Intl.Collator`, і за спаданням вийде `['c', 'a', 'b']`: `'9.5'` > `'9'`
     * > `'10'` як текст. Саме так виглядала колонка після `e470777a`.
     *
     * ⚠ `'9.5000000000'` проти `'9'` перевіряє й другу половину: числовий
     * порядок не можна отримати порівнянням довжин або `Intl.Collator` із
     * `numeric: true` (той порівнює `5` з `25` посегментно й ставить `1.5`
     * нижче за `1.25`).
     */
    expect(renderedOrder()).toEqual(['b', 'c', 'a']);
  });

  it('за зростанням порядок дзеркальний', () => {
    renderWithMantine(
      <DataTable<Reading> columns={columns} rows={rows} rowKey={(row) => row.id} />,
    );

    sortBy('Обсяг');

    expect(renderedOrder()).toEqual(['a', 'c', 'b']);
  });

  it('останній знак вирішує порядок: через `number` ці два значення РІВНІ', () => {
    /*
     * ⛔ Це найтонше твердження файлу, і воно єдине ловить мутацію «порівняти
     * через `Number`». Заміряно тут-таки (Node 24, V8):
     *     Number('9007199254740993') === Number('9007199254740992')  // true
     * Обидва дають `9007199254740992` — `2^53`, перше ціле, після якого
     * IEEE-754 уже не має чим розрізнити сусідів. Отже компаратор на `double`
     * поверне `0`, стабільне сортування лишить порядок сервера, і рядок
     * нижче впаде. `bigint` у `compareDecimals` різницю бачить.
     *
     * ⚠ Порядок рядків тут НАВМИСНО зворотний до очікуваного: інакше «нічого
     * не переставилося» й «переставилося правильно» виглядали б однаково.
     */
    renderWithMantine(
      <DataTable<Reading>
        columns={columns}
        rows={[
          { id: 'big', code: 'X', amount: '9007199254740993', count: 1 },
          { id: 'small', code: 'Y', amount: '9007199254740992', count: 1 },
        ]}
        rowKey={(row) => row.id}
      />,
    );

    sortBy('Обсяг');
    expect(renderedOrder()).toEqual(['small', 'big']);

    sortBy('Обсяг');
    expect(renderedOrder()).toEqual(['big', 'small']);

    // І на екрані ці два числа теж різні — через `number` обидва показалися б
    // як `9,007,199,254,740,992`.
    expect(screen.getByText('9,007,199,254,740,993')).toBeDefined();
    expect(screen.getByText('9,007,199,254,740,992')).toBeDefined();
  });

  it('дзеркало (Г): колонка справжніх `number` сортується як раніше', () => {
    renderWithMantine(
      <DataTable<Reading> columns={columns} rows={rows} rowKey={(row) => row.id} />,
    );

    sortBy('Кількість');

    // 3, 7, 12 — числовий порядок. Текстовий дав би `12, 3, 7`.
    expect(renderedOrder()).toEqual(['c', 'a', 'b']);
  });

  it('дзеркало (Г): текстова колонка лишається за колатором', () => {
    renderWithMantine(
      <DataTable<Reading> columns={columns} rows={rows} rowKey={(row) => row.id} />,
    );

    sortBy('Код');

    // `'007' < '009' < '010'` — і як текст, і як число; тут важливо, що
    // числова гілка сюди НЕ втрутилася й не зняла провідні нулі.
    expect(renderedOrder()).toEqual(['a', 'c', 'b']);
  });

  it('значення, яке десятковим не є, з числової колонки не зникає', () => {
    renderWithMantine(
      <DataTable<Reading>
        columns={columns}
        rows={[
          { id: 'junk', code: 'Y', amount: 'н/д', count: 1 },
          { id: 'num', code: 'X', amount: '2', count: 1 },
        ]}
        rowKey={(row) => row.id}
      />,
    );

    sortBy('Обсяг');

    /*
     * ⛔ Мутаційний доказ: у `compareKeys` поверніть `order ?? 0` замість
     * відкату на колатор — пара «число, не-число» дасть `0`, стабільне
     * сортування лишить порядок сервера (`['junk', 'num']`), і рядок падає.
     * `compareDecimals` віддає `null` саме щоб таке значення не випало з
     * порядку, а не щоб його ховати.
     */
    expect(renderedOrder()).toEqual(['num', 'junk']);
    expect(screen.getByText('н/д')).toBeDefined();
  });
});
