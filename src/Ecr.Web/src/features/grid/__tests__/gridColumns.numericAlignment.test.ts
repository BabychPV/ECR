import { describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { declared, gridCascade, type El } from './cssCascade';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';
import { TotalsRowKey, columnTotals } from '../gridTotals';

/**
 * Вирівнювання числа й доступність повного значення (`U-05`).
 *
 * ⛔ Заміряно в браузері: `.rgCell { text-align: left }` — числа стояли
 * ліворуч, тобто порядок величини в стовпці не читався взагалі; а комірка
 * `C1` мала `scrollWidth 176px` проти `clientWidth 140px`, тобто значення
 * було видно не повністю, і ознака цього — три крапки.
 *
 * ⚠ Тут два різні твердження, і обидва потрібні: `gridColumns` дає КЛАС (за
 * `dataType` із сервера, не за виглядом значення), а таблиця стилів —
 * ПРАВИЛО, яке має перемогти правило пакета. Зелений перший без другого
 * означав би клас, що нічого не робить.
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Колонка 1',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function slice(columns: ColumnDto[], cells: Record<string, unknown> = {}): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns,
    rows: [
      {
        rowKey: 'R1',
        ordinal: 1,
        rowKind: 'Item',
        label: null,
        rowVersion: '0x01',
        cells,
        isOrphaned: false,
      },
    ],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

type Props = Record<string, unknown>;

function built(data: TableSliceDto, saveErrorByCell = new Map<string, string>()) {
  return gridColumns(data, false, NoLocalFlags, {}, noRequiredInput, saveErrorByCell);
}

/** Властивості комірки рядка `R1` (або рядка підсумків) у колонці `code`. */
function cellProps(
  columns: ReturnType<typeof gridColumns>,
  code: string,
  model: Record<string, unknown> = { __rowKey: 'R1' },
): Props {
  const found = columns.find((c) => c.prop === code);
  if (found === undefined || typeof found.cellProperties !== 'function') {
    throw new Error(`Немає колонки ${code} або в неї немає cellProperties`);
  }

  return found.cellProperties({ model, prop: code } as never) as Props;
}

/** Властивості ЗАГОЛОВКА колонки `code`; `null` — їх немає зовсім. */
function headerProps(columns: ReturnType<typeof gridColumns>, code: string): Props | null {
  const found = columns.find((c) => c.prop === code);
  if (found === undefined) throw new Error(`Немає колонки ${code}`);

  return typeof found.columnProperties === 'function'
    ? ((found.columnProperties({} as never) ?? {}) as Props)
    : null;
}

describe('gridColumns — числова колонка вирівнюється праворуч (U-05)', () => {
  it('клас дає ТИП колонки з сервера, а не вигляд значення', () => {
    /*
     * ⛔ Мутаційна межа: у текстовій колонці лежить число, у числовій —
     * текст, який `coerce` лишив як є до відповіді `ECR-CELL-0422`. Здогад
     * за виглядом значення вирівняв би ці дві комірки навпаки.
     */
    const data = slice(
      [column({ code: 'C1', dataType: 'Decimal' }), column({ code: 'C2', dataType: 'String' })],
      { C1: 'н/д', C2: '4242' },
    );
    const columns = built(data);

    expect(String(cellProps(columns, 'C1')['class'] ?? '')).toContain('ecr-cell-numeric');
    expect(String(cellProps(columns, 'C2')['class'] ?? '')).not.toContain('ecr-cell-numeric');
  });

  it.each(['Decimal', 'Int', 'Formula', 'Calculated'] as const)(
    'колонка «%s» — праворуч',
    (dataType) => {
      const columns = built(slice([column({ dataType })]));

      expect(String(cellProps(columns, 'C1')['class'] ?? '')).toContain('ecr-cell-numeric');
    },
  );

  it.each(['String', 'Date', 'Bool', 'Lookup'] as const)('колонка «%s» — ліворуч', (dataType) => {
    const columns = built(slice([column({ dataType })]));

    expect(String(cellProps(columns, 'C1')['class'] ?? '')).not.toContain('ecr-cell-numeric');
  });

  it('стан комірки не втрачає вирівнювання, а вирівнювання — стану', () => {
    // Комірка лише для читання: клас стану і клас вирівнювання стоять поруч.
    const data = slice([column({ isReadOnly: true })]);
    const props = cellProps(built(data), 'C1');

    expect(String(props['class'] ?? '')).toContain('ecr-cell--read-only');
    expect(String(props['class'] ?? '')).toContain('ecr-cell-numeric');
  });

  it('рядок підсумків вирівняний так само, як колонка під ним', () => {
    const data = slice([column()], { C1: '1234.5' });
    const columns = built(data);
    const props = cellProps(columns, 'C1', { __rowKey: TotalsRowKey });

    expect(props['data-grid-totals']).toBe('cell');
    expect(String(props['class'] ?? '')).toContain('ecr-cell-numeric');
    // Дзеркало: підсумки для цієї колонки взагалі рахуються — інакше
    // твердження вище перевіряло б рядок, якого користувач не бачить.
    expect(columnTotals(data).has('C1')).toBe(true);
  });

  it('заголовок числової колонки — теж праворуч, і підказка вимоги не губиться', () => {
    const plain = headerProps(built(slice([column()])), 'C1');

    expect(String(plain?.['class'] ?? '')).toBe('ecr-header-numeric');

    const required = headerProps(built(slice([column({ isRequired: true })])), 'C1');

    expect(String(required?.['class'] ?? '')).toBe('ecr-header-numeric');
    expect(String(required?.['title'] ?? '')).not.toBe('');

    // Дзеркало: текстова колонка без вимоги не отримує властивостей зовсім.
    expect(headerProps(built(slice([column({ dataType: 'String' })])), 'C1')).toBeNull();
  });
});

describe('gridColumns — повне значення доступне, коли комірка вужча за нього (U-05)', () => {
  it('підказка числової комірки несе значення повністю', () => {
    const long = '1234.1234567890123456';
    const columns = built(slice([column({ scale: 16 })], { C1: long }));
    const title = String(cellProps(columns, 'C1', { __rowKey: 'R1', C1: long })['title'] ?? '');

    // ⚠ Порівняння по ЦИФРАХ: у підказці стоїть подача з роздільниками
    // локалі, і дослівний рядок прив'язав би тест до `Intl`, а не до суті —
    // «жодної цифри не загубилося».
    expect(title.replace(/[^\d]/g, '')).toBe(long.replace(/[^\d]/g, ''));
  });

  it('⛔ підказка ПОМИЛКИ збереження не витісняється значенням', () => {
    /*
     * `title` на елементі один. Дві підказки на ньому означали б, що одну з
     * них не видно ніколи, — тому причина відмови сервера має першість, а
     * повне значення такої комірки лишається в рядку формули.
     */
    const saveErrors = new Map([['R1:C1', 'Колонка «C1» очікує число.']]);
    const columns = built(slice([column()], { C1: '1234.5' }), saveErrors);
    const props = cellProps(columns, 'C1', { __rowKey: 'R1', C1: '1234.5' });

    expect(props['title']).toBe('Колонка «C1» очікує число.');
  });

  it('порожня комірка підказки не отримує', () => {
    const columns = built(slice([column()], { C1: null }));

    expect(cellProps(columns, 'C1', { __rowKey: 'R1', C1: null })['title']).toBeUndefined();
  });
});

describe('Таблиця стилів: клас вирівнювання справді перемагає пакет (U-05)', () => {
  const rules = gridCascade();
  const root: El = { tag: 'html', isRoot: true, attrs: { 'data-mantine-color-scheme': 'light' } };

  function chain(tail: El[]): El[] {
    return [root, { tag: 'revo-grid', attrs: { theme: 'compact' } }, ...tail];
  }

  it('комірка з класом — праворуч; без класу — як було, ліворуч', () => {
    const numeric = chain([
      { tag: 'revogr-data' },
      { tag: 'div', classes: ['rgRow'] },
      { tag: 'div', classes: ['rgCell', 'ecr-cell-numeric'] },
    ]);
    const plain = chain([
      { tag: 'revogr-data' },
      { tag: 'div', classes: ['rgRow'] },
      { tag: 'div', classes: ['rgCell'] },
    ]);

    expect(declared(rules, numeric, 'text-align')).toBe('right');

    // ⚠ Дзеркало точне: без класу комірка ВЛАСНОГО правила не має
    // взагалі — ліве вирівнювання вона УСПАДКОВУЄ від `revogr-data` пакета.
    expect(declared(rules, plain, 'text-align')).toBeNull();
    expect(declared(rules, chain([{ tag: 'revogr-data' }]), 'text-align')).toBe('left');
  });

  it('заголовок із класом — праворуч', () => {
    const header = chain([
      { tag: 'revogr-header' },
      { tag: 'div', classes: ['header-rgRow'] },
      { tag: 'div', classes: ['rgHeaderCell', 'ecr-header-numeric'] },
    ]);

    expect(declared(rules, header, 'text-align')).toBe('right');
    // ⚠ `.rgHeaderCell` — `display: flex`, тож самого `text-align` замало.
    expect(declared(rules, header, 'justify-content')).toBe('flex-end');
  });
});
