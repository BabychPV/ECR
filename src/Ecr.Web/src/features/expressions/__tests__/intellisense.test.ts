import { describe, expect, it } from 'vitest';
import type { ExpressionMetadataDto, TemplateStructureDto } from '@/api/types';
import {
  completionAt,
  suggestionsAt,
  TriggerCharacters,
  type EditorSymbols,
} from '../completion';
import { docMarkdown, escapeMarkdown, hintText, hoverDoc } from '../describe';
import { hoverAt } from '../hover';
import { bracketAt, indexStructure, slotAfter } from '../references';

/**
 * IntelliSense редактора виразів: що пропонується ПІД ЧАС НАБОРУ для кожного
 * тригера і що показує наведення.
 *
 * ⚠ Перевіряються чисті модулі — ті самі, що кличе провайдер Monaco
 * (`monaco.ts` лише перекладає їхні варіанти у формат редактора). Сам Monaco
 * в jsdom не піднімається (`D1-12`); те, що віджет справді відкривається
 * сам, перевірено живим прогоном Playwright на стенді (опис коміту).
 *
 * ⚠ Структура — копія форми стенда (`FTPL01`, таблиці `FT1` фіксована і `FT2`
 * динамічна), а не вигадана: саме на ній контролер бачив, що `[` не дає нічого.
 */

const structureDto: TemplateStructureDto = {
  templateVersionId: 3,
  isEditable: false,
  presentationRevision: 0,
  groupRules: [],
  sheets: [
    {
      id: 1,
      code: 'FSM',
      nameL10n: { values: { en: 'Summary' } },
      ordinal: 1,
      isMandatory: true,
      isVisible: true,
      sheetGroup: null,
      tables: [
        {
          id: 185,
          code: 'FT1',
          nameL10n: { values: { en: 'Fixed table' } },
          ordinal: 1,
          layoutKind: 'Flat',
          rowMode: 'Fixed',
          maxDynamicRows: null,
          columns: [
            column(1, 'CDEC', 'Decimal amount', 'Decimal', 't'),
            column(2, 'CSTR', 'Text value', 'String', null),
          ],
          rows: [row(1, 'R1', 'Row one'), row(2, 'R2', 'Row two'), row(3, 'RTOT', 'Total')],
        },
        {
          id: 186,
          code: 'FT2',
          nameL10n: { values: { en: 'Dynamic table' } },
          ordinal: 2,
          layoutKind: 'Flat',
          rowMode: 'Dynamic',
          maxDynamicRows: null,
          columns: [column(3, 'DQTY', 'Quantity', 'Decimal', null)],
          rows: [],
        },
      ],
    },
  ],
} as unknown as TemplateStructureDto;

const structure = indexStructure(structureDto, (text) => text?.values?.['en'] ?? '');

const templateMetadata: ExpressionMetadataDto = {
  functions: [
    { name: 'ROUND', minArgs: 2, maxArgs: 2, acceptsRange: false, resultType: 'Number', tier: 'Core' },
    { name: 'SUM', minArgs: 1, maxArgs: null, acceptsRange: true, resultType: 'Number', tier: 'Core' },
    { name: 'SUMIF', minArgs: 2, maxArgs: 3, acceptsRange: true, resultType: 'Number', tier: 'Core' },
  ],
  constants: [],
  formulas: [],
  arguments: [],
  headers: [{ name: 'HDAT', unit: null, note: 'Date' }],
};

const methodologyMetadata: ExpressionMetadataDto = {
  functions: [
    { name: 'Round', minArgs: 2, maxArgs: 2, acceptsRange: false, resultType: 'Number', tier: 'Core' },
  ],
  constants: [{ name: 'EF_CO2', unit: 'kg_per_t', note: null }],
  formulas: [{ name: 'E_BASE', unit: 'kg', note: null }],
  arguments: [
    { name: 'FuelConsumption', unit: 't', note: null },
    { name: 'GCV.HSE400', unit: null, note: null },
  ],
  headers: [],
};

/** Формула колонки таблиці `FT1` версії з обраною структурою. */
const inTable: EditorSymbols = {
  dialect: 'Template',
  metadata: templateMetadata,
  structure,
  tableDefId: 185,
  hasTemplateVersion: true,
};

const methodology: EditorSymbols = {
  dialect: 'Methodology',
  metadata: methodologyMetadata,
  hasMethodologyVersion: true,
};

/** Що пропонується, якщо курсор у кінці `text` (або на `|`). */
function labels(text: string, symbols: EditorSymbols): string[] {
  const offset = text.includes('|') ? text.indexOf('|') : text.length;
  const clean = text.replace('|', '');
  return suggestionsAt(clean, offset, symbols)?.items.map((i) => i.hint ?? i.label) ?? [];
}

describe('тригери відкривають перелік самі', () => {
  it('перелік тригерів — квадратна дужка, крапка, собака, знак оклику', () => {
    // ⛔ Без `[` у цьому переліку посилання на колонку підказувалося лише по
    // Ctrl+Space — саме це контролер і виміряв: `[` → нічого.
    expect([...TriggerCharacters].sort()).toEqual(['!', '.', '@', '['].sort());
  });
});

describe('`[` — посилання на структуру шаблону', () => {
  it('після `[` — колонки поточної таблиці першими, далі рядки, таблиці, аркуші', () => {
    const items = labels('[|]', inTable);

    expect(items.slice(0, 2)).toEqual(['CDEC', 'CSTR']);
    expect(items).toEqual(expect.arrayContaining(['R1', 'R2', 'RTOT', 'FT1', 'FT2', 'FSM', 'Period']));

    // ⛔ Жодної функції: всередині `[…]` пишуть код, а не вираз. До цього `[R`
    // пропонував `REGFIELD` і `ROUND`.
    expect(items).not.toContain('ROUND');
  });

  it('набране всередині дужки звужує перелік: `[R` → рядки, а не функції', () => {
    expect(labels('[R|]', inTable)).toEqual(['R1', 'R2', 'RTOT']);
  });

  it('після `[R1].[` — колонки таблиці рядка і підстановки місяця', () => {
    expect(labels('[R1].[|]', inTable)).toEqual(['CDEC', 'CSTR', '{Month}', '{Period}']);
  });

  it('після `[FT1].[` — рядки цієї таблиці; після `[FSM].[` — її таблиці', () => {
    expect(labels('[FT1].[|]', inTable)).toEqual(['R1', 'R2', 'RTOT']);
    expect(labels('[FSM].[|]', inTable)).toEqual(['FT1', 'FT2']);
    expect(labels('[FSM].[FT2].[WHERE x].[|]', inTable)).toEqual(['DQTY', '{Month}', '{Period}']);
  });

  it('динамічна таблиця адресується предикатом, а не ключем рядка', () => {
    expect(labels('[FT2].[|]', inTable)).toEqual(['WHERE']);
  });

  it('усередині аргументу функції теж працює: `SUM([R1].[`', () => {
    expect(labels('SUM([R1].[C|])', inTable)).toEqual(['CDEC', 'CSTR']);
  });

  it('колонка несе тип і одиницю — те, що бачить автор у переліку', () => {
    const cdec = suggestionsAt('[', 1, inTable)?.items.find((i) => i.label === 'CDEC');

    expect(cdec?.detail).toBe('Decimal · t');
    expect(cdec?.description).toBe('Decimal amount');
    expect(cdec?.close).toBe(true);
  });

  it('рядок і таблиця — ланка, після якої відкривається наступна', () => {
    const items = suggestionsAt('[', 1, inTable)?.items ?? [];

    expect(items.find((i) => i.label === 'R1')?.chain).toBe(true);
    expect(items.find((i) => i.label === 'FT1')?.chain).toBe(true);
    expect(items.find((i) => i.label === 'CDEC')?.chain).toBeUndefined();
  });

  it('автозакрита `]` за курсором розпізнається — підстановка її заміняє', () => {
    expect(bracketAt('[]', 1)?.closed).toBe(true);
    expect(bracketAt('[', 1)?.closed).toBe(false);
  });

  it('без версії шаблону — пояснення, а не порожнеча', () => {
    // ⛔ Порожній перелік читається як «підказок тут немає взагалі».
    const items = labels('[|]', { dialect: 'Template', metadata: templateMetadata });

    expect(items[0]).toBe('pickTemplateVersion');
  });

  it('версію обрано, таблицю ні (сторінка /admin/expressions) — таблиці й аркуші', () => {
    const items = labels('[|]', { ...inTable, tableDefId: undefined });

    expect(items).toEqual(['FT1', 'FT2', 'FSM', 'Period']);
  });

  it('структура в дорозі — порожньо, а не «оберіть версію»', () => {
    const items = labels('[|]', { ...inTable, structure: undefined });

    expect(items).toEqual(['Period']);
  });

  it('роль ланки: рядок поточної таблиці сильніший за таблицю з тим самим кодом', () => {
    expect(slotAfter(structure, ['R1'], 185).kind).toBe('column');
    expect(slotAfter(structure, ['FT1'], 185).kind).toBe('row');
    expect(slotAfter(structure, ['Period:-1', 'FT1'], 185).kind).toBe('row');
    expect(slotAfter(structure, ['NOPE'], 185).kind).toBe('none');
  });
});

describe('префікси символів', () => {
  it('`HDR.` у шаблоні — поля шапки версії', () => {
    expect(labels('HDR.', inTable)).toEqual(['HDAT']);
  });

  it('`HDR.` без версії — пояснення', () => {
    expect(labels('HDR.', { dialect: 'Template', metadata: { ...templateMetadata, headers: [] } })).toEqual([
      'pickTemplateVersionHeaders',
    ]);
  });

  it('`@` — аргументи методології, з крапкою в імені', () => {
    expect(labels('@', methodology)).toEqual(['FuelConsumption', 'GCV.HSE400']);
    expect(labels('@GCV.H', methodology)).toEqual(['GCV.HSE400']);
  });

  it('`CST.` — константи, `!` — формули', () => {
    expect(labels('CST.', methodology)).toEqual(['EF_CO2']);
    expect(labels('1 + !', methodology)).toEqual(['E_BASE']);
  });

  it('без версії методології — пояснення, з версією без прив’язки — інше', () => {
    const empty = { ...methodologyMetadata, arguments: [], constants: [] };

    expect(labels('@', { dialect: 'Methodology', metadata: empty })).toEqual(['pickMethodologyVersion']);
    expect(labels('@', { ...methodology, metadata: empty })).toEqual(['noArguments']);
    expect(labels('CST.', { ...methodology, metadata: empty })).toEqual(['noConstants']);
  });

  it('у шаблоні `!` — заперечення: після нього функції, а не формули', () => {
    expect(completionAt('!', 1, 'Template')?.kind).toBe('function');
    expect(completionAt('!', 1, 'Methodology')?.kind).toBe('formula');
  });

  it('слово без тригера — функції (`SU` → `SUM`, `SUMIF`)', () => {
    expect(labels('SU', inTable)).toEqual(['SUM', 'SUMIF']);
  });

  it('крапка в числі перелік не відкриває', () => {
    // ⛔ Крапка — тригер. Без цього `1.` відкривав би перелік функцій сам.
    expect(suggestionsAt('1.', 2, inTable)).toBeNull();
    expect(suggestionsAt('[R1].', 5, inTable)).toBeNull();
  });
});

describe('наведення', () => {
  it('функція — сигнатура, тип результату', () => {
    const info = hoverAt('SUM(1)', 1, inTable);

    expect(info?.symbol.kind).toBe('function');
    expect(info && hoverDoc(info.symbol).title).toBe('SUM(range | number, …)');
  });

  it('колонка в `[R1].[CDEC]` — тип і одиниця; рядок — його таблиця', () => {
    const column = hoverAt('[R1].[CDEC]', 8, inTable);
    expect(column?.symbol.kind).toBe('column');
    expect(column && { from: column.from, to: column.to }).toEqual({ from: 6, to: 10 });

    const doc = column === null ? undefined : hoverDoc(column.symbol);
    expect(doc?.title).toBe('[CDEC]');
    expect(doc?.lines).toEqual(expect.arrayContaining(['Decimal amount']));

    expect(hoverAt('[R1].[CDEC]', 2, inTable)?.symbol.kind).toBe('row');
  });

  it('символи методології — з префіксом', () => {
    expect(hoverAt('CST.EF_CO2 * 2', 6, methodology)?.symbol.kind).toBe('constant');
    expect(hoverAt('@GCV.HSE400', 7, methodology)?.symbol.kind).toBe('argument');
    expect(hoverAt('!E_BASE', 3, methodology)?.symbol.kind).toBe('formula');
    expect(hoverAt('Round(1, 2)', 2, methodology)?.symbol.kind).toBe('function');
  });

  it('невідоме ім’я довідки не отримує', () => {
    // ⛔ Довідка на невідоме ім'я виглядала б як підтвердження, що воно правильне.
    expect(hoverAt('NOPE(1)', 2, inTable)).toBeNull();
    expect(hoverAt('[R1].[XX]', 7, inTable)).toBeNull();
    expect(hoverAt('1 + 2', 2, inTable)).toBeNull();
  });
});

describe('текст довідки', () => {
  it('назва з бази екранується — розмітка з неї не рендериться', () => {
    expect(escapeMarkdown('[x](y) *b*')).toBe('\\[x\\]\\(y\\) \\*b\\*');
    expect(docMarkdown({ title: 'A_B', lines: ['<img>'] })).toBe('**A\\_B**\n\n\\<img\\>');
  });

  it('пояснення бере текст із каталогу рядків', () => {
    expect(hintText('pickTemplateVersion')).toContain('expressions.hint.pickTemplateVersion');
  });
});

function column(
  id: number,
  code: string,
  name: string,
  dataType: string,
  unitSymbol: string | null,
): TemplateStructureDto['sheets'][number]['tables'][number]['columns'][number] {
  return {
    id,
    code,
    headerL10n: { values: { en: name } },
    dataType,
    unitSymbol,
    ordinal: id,
    displayFormat: null,
    formulaDialect: null,
    formulaExpression: null,
    isHidden: false,
    isReadOnly: false,
    isRequired: false,
  };
}

function row(
  ordinal: number,
  rowKey: string,
  label: string,
): TemplateStructureDto['sheets'][number]['tables'][number]['rows'][number] {
  return {
    ordinal,
    rowKey,
    label,
    formulaDialect: null,
    formulaExpression: null,
    isReadOnly: false,
    parentRowKey: null,
    rowKind: 'Item',
  };
}
