import { describe, expect, it } from 'vitest';
import type { PermitTypeColumn } from '@/features/permits/api/types';
import {
  MonthGroupCode,
  buildColumn,
  buildMonthColumns,
  buildNewTable,
  codeFromName,
  draftFromColumn,
  emptyDraftColumn,
  keyColumnNames,
  maxTableCodeLength,
  newTableCode,
  tableRegistryCode,
  uniqueCode,
  validateColumns,
} from '../ops';

/**
 * Мутаційні докази для чистих операцій діалогів (PM-08c, споживач — майстер «Add a table» і
 * діалоги стовпця/серії). Кожен блок падає, якщо зламати те, що описує його назва.
 */

function lookupColumn(overrides: Partial<PermitTypeColumn> = {}): PermitTypeColumn {
  return {
    code: 'Substance',
    nameL10n: { en: 'Substance', ru: 'Вещество' },
    dataType: 'Lookup',
    unitId: null,
    required: true,
    key: true,
    group: 'Main',
    precision: null,
    scale: null,
    listScope: 'SharedRegistry',
    listRegistryCode: 'SUBSTANCES',
    siblingTableCode: null,
    choices: null,
    order: 1,
    ...overrides,
  };
}

describe('codeFromName', () => {
  it('стовпець: слова через підкреслення, регістр збережено', () => {
    expect(codeFromName('Water body', 'snake', 'Column')).toBe('Water_body');
    expect(codeFromName('  Annual volume (10³ m³) ', 'snake', 'Column')).toBe('Annual_volume_10_m');
  });

  it('таблиця: PascalCase', () => {
    expect(codeFromName('Monitoring points', 'pascal', 'Table')).toBe('MonitoringPoints');
  });

  it('назва без латиниці дає запасний код, а не порожній', () => {
    expect(codeFromName('Вода', 'snake', 'Column')).toBe('Column');
    expect(codeFromName('', 'pascal', 'Table')).toBe('Table');
  });

  it('перший символ — літера: цифра отримує префікс', () => {
    expect(codeFromName('2026 plan', 'snake', 'Column')).toBe('Column_2026_plan');
  });

  it('не довший за ліміт і не закінчується підкресленням', () => {
    const code = codeFromName(`${'a'.repeat(10)} ${'b'.repeat(80)}`, 'snake', 'Column', 12);
    expect(code.length).toBeLessThanOrEqual(12);
    expect(code.endsWith('_')).toBe(false);
    expect(code).toMatch(/^[A-Za-z][A-Za-z0-9_]*$/);
  });
});

describe('uniqueCode', () => {
  it('вільний код лишається як є', () => {
    expect(uniqueCode('Name', ['Other'])).toBe('Name');
  });

  it('колізія без урахування регістру дає суфікс _2, далі _3', () => {
    expect(uniqueCode('Name', ['name'])).toBe('Name_2');
    expect(uniqueCode('Name', ['Name', 'NAME_2'])).toBe('Name_3');
  });

  it('суфікс не виштовхує код за ліміт', () => {
    const code = uniqueCode('abcdefgh', ['abcdefgh'], 8);
    expect(code.length).toBeLessThanOrEqual(8);
    expect(code).toBe('abcdef_2');
  });
});

describe('код таблиці й реєстру', () => {
  it('реєстр — PRM_<тип>_<таблиця>', () => {
    expect(tableRegistryCode('WaterDischarge', 'Limits')).toBe('PRM_WaterDischarge_Limits');
  });

  it('код таблиці вміщається в 64 разом із кодом реєстру', () => {
    const typeCode = 'T'.repeat(40);
    const code = newTableCode(typeCode, 'A very long table name that exceeds everything', []);
    expect(tableRegistryCode(typeCode, code).length).toBeLessThanOrEqual(64);
    expect(maxTableCodeLength(typeCode)).toBe(64 - 'PRM_'.length - 40 - 1);
  });

  it('повторна назва таблиці отримує унікальний код', () => {
    expect(newTableCode('Air', 'Limits', ['limits'])).toBe('Limits_2');
  });
});

describe('validateColumns', () => {
  it('порожня назва — перший хибний рядок', () => {
    const columns = [
      emptyDraftColumn({ name: 'A', key: true }),
      emptyDraftColumn({ name: '   ' }),
    ];
    expect(validateColumns(columns)).toEqual({ kind: 'unnamed', index: 1 });
  });

  it('без ключа — відмова noKey', () => {
    expect(validateColumns([emptyDraftColumn({ name: 'A' })])).toEqual({ kind: 'noKey' });
  });

  it('з ключем і назвами — без проблем', () => {
    expect(validateColumns([emptyDraftColumn({ name: 'A', key: true })])).toBeNull();
  });

  it('ключові назви для рядка «Unique row»', () => {
    const columns = [
      emptyDraftColumn({ name: ' Water body ', key: true }),
      emptyDraftColumn({ name: 'Volume' }),
      emptyDraftColumn({ name: 'Period', key: true }),
    ];
    expect(keyColumnNames(columns)).toEqual(['Water body', 'Period']);
  });
});

describe('buildColumn', () => {
  it('новий стовпець: код із назви, ключ стає обов’язковим', () => {
    const column = buildColumn(
      emptyDraftColumn({ name: 'Water body', key: true, required: false }),
      1,
      new Set(),
    );
    expect(column).toMatchObject({ code: 'Water_body', key: true, required: true, order: 1, dataType: 'String' });
    expect(column.nameL10n).toEqual({ en: 'Water body' });
  });

  it('одиниця лишається лише в числових типах', () => {
    const text = buildColumn(emptyDraftColumn({ name: 'A', unitId: 5 }), 1, new Set());
    const number = buildColumn(emptyDraftColumn({ name: 'B', dataType: 'Decimal', unitId: 5 }), 2, new Set());
    expect(text.unitId).toBeNull();
    expect(number.unitId).toBe(5);
  });

  it('коди в межах таблиці унікальні, PERMIT зарезервовано', () => {
    const table = buildNewTable({
      typeCode: 'Air',
      nameEn: 'Limits',
      nameRu: '',
      existingTableCodes: [],
      columns: [
        emptyDraftColumn({ name: 'Value', key: true }),
        emptyDraftColumn({ name: 'value' }),
        emptyDraftColumn({ name: 'Permit' }),
      ],
    });
    expect(table.columns.map((c) => c.code)).toEqual(['Value', 'value_2', 'Permit_2']);
  });

  it('копія зберігає код, групу, список і точність; перейменування скидає старі мови', () => {
    const copied = buildColumn(draftFromColumn(lookupColumn()), 3, new Set());
    expect(copied).toMatchObject({
      code: 'Substance',
      group: 'Main',
      listScope: 'SharedRegistry',
      listRegistryCode: 'SUBSTANCES',
      order: 3,
    });
    expect(copied.nameL10n).toEqual({ en: 'Substance', ru: 'Вещество' });

    const renamed = buildColumn({ ...draftFromColumn(lookupColumn()), name: 'Pollutant' }, 1, new Set());
    expect(renamed.nameL10n).toEqual({ en: 'Pollutant' });
  });

  it('список «лише для типу» копіюється без коду прихованого реєстру', () => {
    const column = buildColumn(
      draftFromColumn(
        lookupColumn({ listScope: 'TypeOnly', listRegistryCode: 'PRM_Old_T_C', choices: ['A', 'B'] }),
      ),
      1,
      new Set(),
    );
    expect(column.listScope).toBe('TypeOnly');
    expect(column.listRegistryCode).toBeNull();
    expect(column.choices).toEqual(['A', 'B']);
  });

  it('точність без Decimal зникає, масштаб не лишається без точності', () => {
    const decimal: PermitTypeColumn = {
      ...lookupColumn(),
      dataType: 'Decimal',
      precision: 18,
      scale: 4,
      listScope: null,
      listRegistryCode: null,
    };
    expect(buildColumn(draftFromColumn(decimal), 1, new Set())).toMatchObject({ precision: 18, scale: 4 });

    const retyped = buildColumn({ ...draftFromColumn(decimal), dataType: 'Int' }, 1, new Set());
    expect(retyped.precision).toBeNull();
    expect(retyped.scale).toBeNull();
  });
});

describe('buildNewTable', () => {
  it('збирає таблицю: код, назви EN/RU, порядок, порожні перевірки', () => {
    const table = buildNewTable({
      typeCode: 'Air',
      nameEn: ' Monitoring points ',
      nameRu: ' Точки ',
      existingTableCodes: ['A', 'B'],
      columns: [emptyDraftColumn({ name: 'Point', key: true })],
    });
    expect(table).toMatchObject({
      code: 'MonitoringPoints',
      nameL10n: { en: 'Monitoring points', ru: 'Точки' },
      order: 3,
      rules: [],
    });
    expect(table.columns).toHaveLength(1);
  });

  it('порожня російська назва не потрапляє в запит', () => {
    const table = buildNewTable({
      typeCode: 'Air',
      nameEn: 'Limits',
      nameRu: '  ',
      existingTableCodes: [],
      columns: [emptyDraftColumn({ name: 'A', key: true })],
    });
    expect(table.nameL10n).toEqual({ en: 'Limits' });
  });
});

describe('buildMonthColumns', () => {
  const series = buildMonthColumns({ unitId: 7, group: MonthGroupCode, existingCodes: [], startOrder: 4 });

  it('рівно 12 стовпців від січня до грудня', () => {
    expect(series).toHaveLength(12);
    expect(series[0]?.code).toBe('January');
    expect(series[11]?.code).toBe('December');
    expect(series.map((c) => c.order)).toEqual([4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]);
  });

  it('усі числові, необов’язкові, не ключові, з однією групою й одиницею', () => {
    for (const column of series) {
      expect(column).toMatchObject({
        dataType: 'Decimal',
        required: false,
        key: false,
        group: MonthGroupCode,
        unitId: 7,
      });
      expect(Object.keys(column.nameL10n).sort()).toEqual(['en', 'kz', 'ru']);
    }
  });

  it('другий виклик не дублює коди: суфікс _2', () => {
    const again = buildMonthColumns({
      unitId: null,
      group: MonthGroupCode,
      existingCodes: series.map((c) => c.code),
      startOrder: 16,
    });
    expect(again[0]?.code).toBe('January_2');
    expect(new Set([...series, ...again].map((c) => c.code.toLowerCase())).size).toBe(24);
    expect(again[0]?.unitId).toBeNull();
  });

  it('PERMIT і наявний у таблиці May не повторюються', () => {
    const withMay = buildMonthColumns({ unitId: null, group: 'G', existingCodes: ['may'], startOrder: 1 });
    expect(withMay[4]?.code).toBe('May_2');
  });
});
