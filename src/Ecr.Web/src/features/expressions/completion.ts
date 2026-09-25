import type {
  ExpressionDialect,
  ExpressionFunctionDto,
  ExpressionMetadataDto,
  ExpressionSymbolDto,
} from '@/api/types';
import {
  bracketAt,
  slotAfter,
  type BracketContext,
  type ColumnSymbol,
  type StructureIndex,
  type TableSymbol,
} from './references';

/**
 * Що саме доповнюється в цій позиції — і чим.
 *
 * ⛔ Чиста функція, окремо від Monaco, і не заради чистоти. Рендер важкого
 * компонента в jsdom іде хвилинами (`D1-12`), і перевірка автодоповнення через
 * редактор була б тестом, який вимкнули б першим. Тут перевіряється рівно те,
 * що ламається: ЯКИЙ префікс розпізнано і ЩО він пропонує.
 */

/** Вид символу, який доповнюється. */
export type CompletionKind =
  | 'constant'
  | 'formula'
  | 'argument'
  | 'header'
  | 'function'
  | 'column'
  | 'row'
  | 'table'
  | 'sheet'
  | 'keyword'
  | 'hint';

/** Що розпізнано в позиції: символ за префіксом, функція або ланка `[…]`. */
export type ContextKind = 'constant' | 'formula' | 'argument' | 'header' | 'function' | 'cell';

/**
 * Пояснення замість порожнього переліку.
 *
 * ⛔ Порожній перелік після `[` чи `CST.` читається як «підказок у цьому
 * редакторі немає», хоча насправді бракує КОНТЕКСТУ — обраної версії. Ключ, а
 * не текст: модуль чистий і не знає каталогу рядків; локалізує напис місце
 * реєстрації провайдера (`monaco.ts`).
 */
export type HintKey =
  | 'pickTemplateVersion'
  | 'pickTemplateVersionHeaders'
  | 'noHeaders'
  | 'pickMethodologyVersion'
  | 'noConstants'
  | 'noFormulas'
  | 'noArguments';

/** Один варіант підстановки. */
export interface CompletionItem {
  /** Що підставити. */
  readonly insert: string;
  /** Що показати в переліку. */
  readonly label: string;
  /** Вид — визначає піктограму і сортування. */
  readonly kind: CompletionKind;
  /** Права колонка переліку: одиниця, тип результату. */
  readonly detail?: string | undefined;
  /** Пояснення під переліком. */
  readonly documentation?: string | undefined;
  /**
   * Ярус функції — з СЕРВЕРА (`DialectCatalog`).
   *
   * ⛔ `Extension` означає «чинний рушій цього не вміє», і показати таку
   * функцію без позначки означало б запросити написати вираз, якого немає з
   * чим звіряти. Текст позначки цей модуль не складає навмисно: він чистий і
   * перевіряється без каталогу рядків, а локалізує напис місце реєстрації
   * провайдера.
   *
   * ⚠ У версії з `NumericMode = Legacy` таких функцій у переліку немає
   * взагалі — їх не віддає сервер (`ECR-CALC-0433`).
   */
  readonly tier?: string | undefined;
  /** Друга колонка підпису: людська назва колонки, рядка, таблиці. */
  readonly description?: string | undefined;
  /**
   * Ланка посилання, після якої йде наступна (`[R1].[…`): підстановка дописує
   * `].[` і знову відкриває перелік — уже колонок.
   */
  readonly chain?: boolean | undefined;
  /** Чи закривати ланку `]` (для `WHERE ` — ні: далі пишуть умову). */
  readonly close?: boolean | undefined;
  /** Порядок у переліку: групи за роллю, усередині — порядок структури. */
  readonly sort?: string | undefined;
  /** Для функції — її опис із сервера, щоб підказка могла показати довідку. */
  readonly fn?: ExpressionFunctionDto | undefined;
  /** Для `hint` — яке пояснення показати. */
  readonly hint?: HintKey | undefined;
}

/**
 * Усе, з чого складаються підказки редактора, на мить запиту.
 *
 * ⚠ Функція-джерело повертає це щоразу наново (`monaco.ts`): метадані і
 * структура приходять пізніше за реєстрацію провайдера і змінюються разом із
 * обраною версією.
 */
export interface EditorSymbols {
  readonly dialect?: ExpressionDialect | undefined;
  readonly metadata: ExpressionMetadataDto | undefined;
  /** Структура версії шаблону; `undefined` — версії немає або вона ще в дорозі. */
  readonly structure?: StructureIndex | undefined;
  /** Таблиця, у якій живе вираз. */
  readonly tableDefId?: number | undefined;
  readonly hasTemplateVersion?: boolean | undefined;
  readonly hasMethodologyVersion?: boolean | undefined;
}

/**
 * Символи, після яких перелік відкривається САМ — без Ctrl+Space.
 *
 * ⛔ Тут, а не в `monaco.ts`: той модуль у jsdom не завантажується, і перелік
 * тригерів, оголошений лише там, ніхто не перевірив би. `[` — посилання на
 * структуру, `.` — `CST.`/`HDR.` і наступна частина імені аргументу, `@` —
 * аргумент, `!` — формула. Слова (`SU` → `SUM`) підказуються без тригера —
 * через `quickSuggestions` редактора.
 */
export const TriggerCharacters: readonly string[] = ['[', '.', '!', '@'];

/** Що доповнюємо і який фрагмент тексту замінюємо. */
export interface CompletionContext {
  /** Розпізнаний вид. */
  readonly kind: ContextKind;
  /** Початок фрагмента, який заміняє підстановка (зміщення в символах). */
  readonly replaceFrom: number;
  /** Уже набрана частина імені — для фільтрації. */
  readonly typed: string;
  /** Для `cell` — ланцюжок попередніх ланок. */
  readonly bracket?: BracketContext;
}

/**
 * Префікси мови (`02b` §3.4) у порядку перевірки.
 *
 * ⚠ `CST.` і `HDR.` — ПЕРЕД однолітерними: інакше `!` та `@` ніколи не дійшли
 * б до перевірки в тексті на кшталт `CST.X`, бо крапка вже все вирішила.
 */
const Prefixes: readonly {
  readonly text: string;
  readonly kind: CompletionKind & ContextKind;
  /** Діалекти, у яких префікс означає посилання. */
  readonly dialects: readonly ExpressionDialect[];
}[] = [
  { text: 'CST.', kind: 'constant', dialects: ['Methodology'] },
  { text: 'HDR.', kind: 'header', dialects: ['Methodology', 'Template'] },
  // ⛔ У діалекті шаблону `!` — це заперечення (`02b` §2, рядок 8), а не
  // посилання на формулу: запропонувати після нього імена формул означало б
  // навчити писати вираз, який не розбереться.
  { text: '!', kind: 'formula', dialects: ['Methodology'] },
  { text: '@', kind: 'argument', dialects: ['Methodology'] },
];

/**
 * Визначає, що доповнюється зліва від курсора.
 *
 * @param text Увесь текст виразу.
 * @param offset Позиція курсора — зміщення в символах від початку.
 * @returns Контекст доповнення; `null` — доповнювати нема чого.
 */
export function completionAt(
  text: string,
  offset: number,
  dialect?: ExpressionDialect,
): CompletionContext | null {
  const before = text.slice(0, Math.max(0, Math.min(offset, text.length)));

  // ⛔ Перевірка рядка — ПЕРША, до розпізнавання префіксів. Зворотний порядок
  // здається рівноцінним і не є ним: у `'@Fuel` префікс `@` знаходиться раніше,
  // ніж хтось питає, чи ми взагалі в тексті, — і редактор пропонує підставити
  // ім'я аргументу всередину константи, яку користувач саме друкує.
  if (insideString(before)) {
    return null;
  }

  // Ланка посилання `[…]`: усередині неї пишуть код, а не вираз.
  if (dialect !== 'Report') {
    const bracket = bracketAt(text, before.length);

    if (bracket !== null) {
      return {
        kind: 'cell',
        replaceFrom: bracket.replaceFrom,
        typed: bracket.typed,
        bracket,
      };
    }
  }

  const allows = (dialects: readonly ExpressionDialect[]): boolean =>
    dialect === undefined || dialects.includes(dialect);

  // ⛔ Ім'я аргументу може містити крапки (`@GCV.HSE400_FG_Makat_Methane`,
  // `02b` §3.4): 2534 із 4230 посилань корпусу. Без цієї гілки після крапки
  // перелік аргументів змінювався б переліком функцій.
  if (allows(['Methodology'])) {
    const argument = /@((?:[A-Za-z_]\w*)(?:\.\w*)*)?$/.exec(before);

    if (argument !== null) {
      const typed = argument[1] ?? '';
      return { kind: 'argument', replaceFrom: before.length - typed.length, typed };
    }
  }

  // Ім'я, яке вже набрали: літери, цифри і підкреслення.
  const typed = /[A-Za-z0-9_]*$/.exec(before)?.[0] ?? '';
  const head = before.slice(0, before.length - typed.length);

  for (const { text: prefix, kind, dialects } of Prefixes) {
    if (head.endsWith(prefix) && allows(dialects)) {
      return { kind, replaceFrom: before.length - typed.length, typed };
    }
  }

  // ⛔ Крапка поза `CST.`/`HDR.` — це десятковий дріб (`1.5`) або властивість
  // (`[Period].Days`), і перелік функцій на ній лише заважав би: крапка —
  // символ-тригер, тож він відкривався б САМ посеред числа.
  if (head.endsWith('.')) {
    return null;
  }

  // ⚠ Порожній `typed` теж доповнюється: перелік функцій має відкриватися по
  // Ctrl+Space на порожньому місці, а не лише після першої літери.
  return { kind: 'function', replaceFrom: before.length - typed.length, typed };
}

/** Перелік для позиції разом із контекстом, що визначає межі заміни. */
export interface Suggestions {
  readonly context: CompletionContext;
  readonly items: readonly CompletionItem[];
}

/**
 * Усі варіанти для позиції курсора — те, що бачить провайдер Monaco.
 *
 * @param text Увесь текст виразу.
 * @param offset Позиція курсора.
 * @param symbols Склад мови і структура на мить запиту.
 * @returns `null` — доповнювати нема чого (рядок, крапка в числі).
 */
export function suggestionsAt(
  text: string,
  offset: number,
  symbols: EditorSymbols,
): Suggestions | null {
  const context = completionAt(text, offset, symbols.dialect);
  if (context === null) return null;

  if (context.kind === 'cell' && context.bracket !== undefined) {
    return { context, items: filtered(cellItems(context.bracket, symbols), context.typed) };
  }

  const items = completionsFor(context, symbols.metadata);

  if (items.length === 0 && context.typed === '' && symbols.metadata !== undefined) {
    const hint = emptyHint(context.kind, symbols);
    if (hint !== undefined) return { context, items: [hintItem(hint)] };
  }

  return { context, items };
}

function emptyHint(kind: ContextKind, symbols: EditorSymbols): HintKey | undefined {
  switch (kind) {
    case 'header':
      return symbols.hasTemplateVersion === true ? 'noHeaders' : 'pickTemplateVersionHeaders';
    case 'constant':
      return symbols.hasMethodologyVersion === true ? 'noConstants' : 'pickMethodologyVersion';
    case 'formula':
      return symbols.hasMethodologyVersion === true ? 'noFormulas' : 'pickMethodologyVersion';
    case 'argument':
      return symbols.hasMethodologyVersion === true ? 'noArguments' : 'pickMethodologyVersion';
    default:
      return undefined;
  }
}

function hintItem(hint: HintKey): CompletionItem {
  return { insert: '', label: hint, kind: 'hint', hint };
}

/**
 * Варіанти всередині `[…]` — за роллю ланки (`references.ts`).
 *
 * ⚠ Структура не запитується тут: вона вже в кеші сторінки. Цей код
 * виконується на кожне натискання, тож мережі в ньому бути не може.
 */
function cellItems(bracket: BracketContext, symbols: EditorSymbols): CompletionItem[] {
  const period = bracket.segments.length === 0 ? [periodItem()] : [];

  // У діалекті методології комірок немає за побудовою (`02b` §3.4) — лише `[Period]`.
  if (symbols.dialect === 'Methodology') return period;

  const structure = symbols.structure;

  if (structure === undefined) {
    // ⚠ Версію обрано, структура ще в дорозі — порожньо, а не «оберіть версію»:
    // друге було б неправдою.
    if (symbols.hasTemplateVersion === true) return period;

    return bracket.segments.length === 0 ? [hintItem('pickTemplateVersion'), ...period] : [];
  }

  const slot = slotAfter(structure, bracket.segments, symbols.tableDefId);

  switch (slot.kind) {
    case 'first': {
      const tables = structure.tables.map((t, i) => tableItem(t, i));
      const sheets = structure.sheets.map(
        (s, i): CompletionItem => ({
          insert: s.code,
          label: s.code,
          kind: 'sheet',
          description: s.name,
          chain: true,
          close: true,
          sort: `4${pad(i)}`,
        }),
      );

      if (slot.table === undefined) return [...tables, ...sheets, ...period];

      return [...columnItems(slot.table), ...rowItems(slot.table), ...tables, ...sheets, ...period];
    }

    case 'table':
      return slot.sheet.tables.map((t, i) => tableItem(t, i));

    case 'row':
      return rowItems(slot.table);

    case 'column':
      return columnItems(slot.table);

    default:
      return [];
  }
}

function columnItems(table: TableSymbol): CompletionItem[] {
  return [
    ...table.columns.map((c, i) => columnItem(c, i)),

    // ⚠ Підстановки поточної колонки (`02b` §1, `column_selector`): формула
    // рядка «Разом» пишеться один раз на всі місяці саме через них.
    ...['{Month}', '{Period}'].map(
      (code, i): CompletionItem => ({
        insert: code,
        label: code,
        kind: 'keyword',
        close: true,
        sort: `1${pad(i)}`,
      }),
    ),
  ];
}

function columnItem(column: ColumnSymbol, i: number): CompletionItem {
  return {
    insert: column.code,
    label: column.code,
    kind: 'column',
    description: column.name,
    detail: column.unit === null ? column.dataType : `${column.dataType} · ${column.unit}`,
    close: true,
    sort: `0${pad(i)}`,
  };
}

function rowItems(table: TableSymbol): CompletionItem[] {
  const rows = table.rows.map(
    (r, i): CompletionItem => ({
      insert: r.key,
      label: r.key,
      kind: 'row',
      description: r.label ?? undefined,
      detail: table.code,
      chain: true,
      close: true,
      sort: `2${pad(i)}`,
    }),
  );

  // ⛔ Динамічні рядки адресуються лише предикатом (`02b` §3.3 п. 3): конкретний
  // ключ рядка, який створив користувач, у формулі заборонений.
  if (!table.dynamic && rows.length > 0) return rows;

  return [
    ...rows,
    { insert: 'WHERE ', label: 'WHERE', kind: 'keyword', detail: table.code, close: false, sort: '29' },
  ];
}

function tableItem(table: TableSymbol, i: number): CompletionItem {
  return {
    insert: table.code,
    label: table.code,
    kind: 'table',
    description: table.name,
    detail: table.sheetCode,
    chain: true,
    close: true,
    sort: `3${pad(i)}`,
  };
}

function periodItem(): CompletionItem {
  return { insert: 'Period', label: 'Period', kind: 'keyword', close: true, sort: '5' };
}

function filtered(items: readonly CompletionItem[], typed: string): CompletionItem[] {
  const prefix = typed.toUpperCase();

  // ⚠ Пояснення не фільтрується: воно стосується контексту, а не набраного.
  return items.filter((i) => i.kind === 'hint' || i.insert.toUpperCase().startsWith(prefix));
}

function pad(i: number): string {
  return String(i).padStart(4, '0');
}

/**
 * Чи стоїть курсор усередині рядкового літерала.
 *
 * ⚠ Рахуються лапки з урахуванням подвоєння `''` — єдиного способу екранувати
 * лапку в нашій мові (`02b` §1). Наївний підрахунок непарності зламався б на
 * `'don''t'` і вважав би решту виразу текстом.
 */
function insideString(before: string): boolean {
  let inside = false;

  for (let i = 0; i < before.length; i++) {
    if (before[i] !== "'") continue;

    if (inside && before[i + 1] === "'") {
      i++;
      continue;
    }

    inside = !inside;
  }

  return inside;
}

/**
 * Варіанти підстановки для контексту.
 *
 * @param context Що доповнюємо.
 * @param metadata Склад мови, отриманий із сервера.
 * @returns Відфільтрований і впорядкований перелік.
 */
export function completionsFor(
  context: CompletionContext,
  metadata: ExpressionMetadataDto | undefined,
): readonly CompletionItem[] {
  const kind = context.kind;
  if (metadata === undefined || kind === 'cell') return [];

  const all =
    kind === 'function'
      ? metadata.functions.map(functionItem)
      : symbolsOf(metadata, kind).map((s) => symbolItem(s, kind));

  const typed = context.typed.toUpperCase();

  return all
    .filter((item) => item.insert.toUpperCase().startsWith(typed))
    .sort((a, b) => a.label.localeCompare(b.label));
}

function symbolsOf(
  metadata: ExpressionMetadataDto,
  kind: ContextKind,
): readonly ExpressionSymbolDto[] {
  switch (kind) {
    case 'constant':
      return metadata.constants;
    case 'formula':
      return metadata.formulas;
    case 'argument':
      return metadata.arguments;
    case 'header':
      return metadata.headers;
    default:
      return [];
  }
}

function symbolItem(symbol: ExpressionSymbolDto, kind: CompletionKind & ContextKind): CompletionItem {
  return {
    insert: symbol.name,
    label: symbol.name,
    kind,
    detail: symbol.unit ?? undefined,
    documentation: symbol.note ?? undefined,
  };
}

/**
 * Варіант для функції: підставляє дужки і лишає курсор між ними.
 *
 * ⚠ Функція без аргументів у нашій мові не існує: у діалекті шаблонів усі 13,
 * у діалекті методологій усі 26 приймають щонайменше один (`02b` §7–§8). Тому
 * дужки підставляються завжди, і курсор завжди всередині: інакше кожен виклик
 * доводилося б дописувати руками.
 */
function functionItem(fn: ExpressionFunctionDto): CompletionItem {
  return {
    insert: fn.name,
    label: fn.name,
    kind: 'function',
    detail: signatureOf(fn),
    documentation: fn.resultType ?? undefined,
    tier: fn.tier,
    fn,
  };
}

/**
 * Людський вигляд сигнатури: `SUM(діапазон | число, …)`.
 *
 * ⛔ Показує саме те, що перевірить публікація: скільки аргументів і чи
 * приймається діапазон. Підказка, яка про діапазон мовчить, навчала б
 * передавати комірки по одній — а це і є та форма, від якої відходить уся
 * міграція з Excel.
 */
export function signatureOf(fn: ExpressionFunctionDto): string {
  const parts: string[] = [];

  for (let i = 0; i < fn.minArgs; i++) {
    parts.push(fn.acceptsRange && i === 0 ? 'range | number' : `arg${i + 1}`);
  }

  if (fn.maxArgs === null || fn.maxArgs === undefined) {
    parts.push('…');
  } else if (fn.maxArgs > fn.minArgs) {
    parts.push(`arg${fn.minArgs + 1}?`);
  }

  return `${fn.name}(${parts.join(', ')})`;
}
