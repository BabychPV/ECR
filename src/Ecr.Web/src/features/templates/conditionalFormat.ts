import { normalizeDecimal } from '@/shared/format';

/**
 * Умовне форматування комірок за правилами шаблону (`ФВ-2.7`) — клієнтська
 * модель правила й перевірка «чи спрацьовує правило на значенні».
 *
 * Сервер зберігає правила в `cfg.ConditionalFormatRule` (`GET/PUT
 * …/template-versions/{id}/conditional-formats`, `D-234`) і сам рахує їх для
 * сітки документа й Excel (`ConditionalFormatEvaluator`, `TableSliceDto.cellFormats`).
 * Тут — модель редактора (`ConditionalFormatPanel.tsx`) і перевірка правила на
 * значенні-прикладі, узгоджені з серверною: ті самі оператори, межі включно,
 * перше спрацьоване правило виграє.
 *
 * ⛔ Семантика ОДНА з сервером, і перевіряє її спільна фікстура
 * `tests/Ecr.TestKit/Fixtures/conditional-format-parity.json` (читають і
 * `conditionalFormatParity.test.ts`, і `ConditionalFormatParityTests.cs`):
 * усі оператори з операндом — числові, включно з `eq`/`ne`; значення, що не
 * є числом (текст — навіть «5», дата, булеве), жодного з них не задовольняє.
 * «Не дорівнює 100» для тексту «abc» — не привід фарбувати: правило про
 * число, а числа в комірці немає. До 2026-10-01 тут `eq`/`ne` порівнювали
 * ще й текст, і панель фарбувала те, чого не фарбували сітка й Excel.
 *
 * ⚠ Порівняння — точне, на канонічних десяткових рядках (`normalizeDecimal`),
 * без `Number`: сервер рахує в `decimal`, і `double` розійшовся б із ним на
 * 16-му знаку.
 */

export type ConditionOperator =
  | 'gt'
  | 'ge'
  | 'lt'
  | 'le'
  | 'eq'
  | 'ne'
  | 'between'
  | 'empty'
  | 'notEmpty';

export const ConditionOperators: readonly ConditionOperator[] = [
  'gt',
  'ge',
  'lt',
  'le',
  'eq',
  'ne',
  'between',
  'empty',
  'notEmpty',
];

export interface ConditionalRule {
  /** Код колонки, до якої правило застосовується. */
  readonly columnCode: string;
  readonly operator: ConditionOperator;
  readonly value: string;
  /** Верхня межа — лише для `between`. */
  readonly valueTo: string;
  /** `#rrggbb` або порожньо (колір теми). */
  readonly backgroundHex: string;
  readonly foregroundHex: string;
  readonly isBold: boolean;
}

export function emptyRule(columnCode = ''): ConditionalRule {
  return {
    columnCode,
    operator: 'gt',
    value: '',
    valueTo: '',
    backgroundHex: '',
    foregroundHex: '',
    isBold: false,
  };
}

/** Чи потрібне операторові значення (і скільки). */
export function operandCount(operator: ConditionOperator): 0 | 1 | 2 {
  if (operator === 'empty' || operator === 'notEmpty') return 0;
  return operator === 'between' ? 2 : 1;
}

export type RuleBlocker = 'Column' | 'Value' | 'ValueTo' | 'Color' | 'Style';

/** Межа довжини операнда — `ConditionalFormatRule.MaxOperandLength` на сервері. */
export const MaxOperandLength = 64;

const HexColor = /^#[0-9a-fA-F]{6}$/;

/** Найбільше `decimal` .NET (`decimal.MaxValue`) — ціла частина. */
const DecimalMaxInteger = '79228162514264337593543950335';

/**
 * Канонічне десяткове число, як його розбирає сервер (`decimal.TryParse` після
 * заміни коми крапкою); `null` — не число або не вміщається в `decimal`.
 */
function decimalOf(text: string): string | null {
  const normalized = normalizeDecimal(text.replace(/,/g, '.'));
  if (normalized === null) return null;

  const integer = normalized.replace(/^-/, '').split('.')[0] ?? '';
  const fits =
    integer.length < DecimalMaxInteger.length ||
    (integer.length === DecimalMaxInteger.length && integer <= DecimalMaxInteger);

  return fits ? normalized : null;
}

function isOperandNumber(text: string): boolean {
  return text.trim().length <= MaxOperandLength && decimalOf(text) !== null;
}

/** Точне порівняння двох канонічних десяткових рядків: від'ємне, 0 або додатне. */
function compareDecimal(left: string, right: string): number {
  const negativeLeft = left.startsWith('-');
  const negativeRight = right.startsWith('-');
  if (negativeLeft !== negativeRight) return negativeLeft ? -1 : 1;

  const magnitude = compareMagnitude(left.replace(/^-/, ''), right.replace(/^-/, ''));
  return negativeLeft ? -magnitude : magnitude;
}

function compareMagnitude(left: string, right: string): number {
  const [leftInteger = '', leftFraction = ''] = left.split('.');
  const [rightInteger = '', rightFraction = ''] = right.split('.');

  if (leftInteger.length !== rightInteger.length) return leftInteger.length - rightInteger.length;
  if (leftInteger !== rightInteger) return leftInteger < rightInteger ? -1 : 1;

  const width = Math.max(leftFraction.length, rightFraction.length);
  const a = leftFraction.padEnd(width, '0');
  const b = rightFraction.padEnd(width, '0');
  return a === b ? 0 : a < b ? -1 : 1;
}

/**
 * Чому правило ще не повне; `null` — повне.
 *
 * ⛔ Ті самі умови, що перевіряє сервер (`ConditionalFormatRule.Validate`):
 * операнд — число для КОЖНОГО оператора з операндом, включно з «дорівнює»
 * (сервер текстового операнда не прийме — `condFormatOperand`), колір —
 * рівно `#rrggbb`. Правило, яке редактор назвав повним, не може отримати
 * відмову `422` при збереженні.
 *
 * ⛔ L9-25: і НІЧОГО понад сервер. `between` із межами навпаки (`5…1`) сервер
 * приймає й застосовує як `1…5` (`ConditionalFormatEvaluator`: `Math.Min`/
 * `Math.Max`; вектор «between: межі навпаки» спільної фікстури). Доти тут була
 * своя перевірка «верхня не менша за нижню»: таке правило, збережене раніше чи
 * прийшле з сервера, панель вважала неповним — пропускала в прикладі (сітка ж
 * його фарбує) і блокувала «Зберегти» для ВСЬОГО набору таблиці.
 */
export function whyRuleIncomplete(rule: ConditionalRule): RuleBlocker | null {
  if (rule.columnCode.trim().length === 0) return 'Column';

  const count = operandCount(rule.operator);

  if (count >= 1 && !isOperandNumber(rule.value)) return 'Value';

  if (count === 2 && !isOperandNumber(rule.valueTo)) return 'ValueTo';

  for (const hex of [rule.backgroundHex, rule.foregroundHex]) {
    if (hex !== '' && !HexColor.test(hex)) return 'Color';
  }

  if (rule.backgroundHex === '' && rule.foregroundHex === '' && !rule.isBold) return 'Style';

  return null;
}

/**
 * Значення комірки для правила — те саме розгортання, що на сервері
 * (`CellValueMapping.ToRuleValue`): число — десятковим рядком (як його шле
 * API), усе інше (текст, дата, булеве, елемент довідника) — `other` зі своїм
 * текстом; `null` — комірка порожня.
 */
export type RuleCellValue =
  | { readonly kind: 'number'; readonly value: string }
  | { readonly kind: 'other'; readonly value: string }
  | null;

/**
 * Порожньо — `null` або текст із самих пробілів, як `string.IsNullOrWhiteSpace`.
 * ⚠ `trim()` у JS знімає ще й U+FEFF, якого .NET пробілом не вважає.
 */
function isBlank(value: RuleCellValue): boolean {
  if (value === null) return true;
  return value.kind === 'other' && value.value.trim().length === 0 && !value.value.includes('\uFEFF');
}

/** Чи спрацьовує правило на значенні комірки — дзеркало `ConditionalFormatEvaluator.Matches`. */
export function ruleMatchesValue(rule: ConditionalRule, cell: RuleCellValue): boolean {
  const empty = isBlank(cell);

  if (rule.operator === 'empty') return empty;
  if (rule.operator === 'notEmpty') return !empty;

  // ⛔ Нечислове значення не задовольняє ЖОДНОГО оператора з операндом — і `ne` теж.
  const value = cell?.kind === 'number' ? normalizeDecimal(cell.value) : null;
  const operand = decimalOf(rule.value);
  if (value === null || operand === null) return false;

  const order = compareDecimal(value, operand);

  switch (rule.operator) {
    case 'gt':
      return order > 0;
    case 'ge':
      return order >= 0;
    case 'lt':
      return order < 0;
    case 'le':
      return order <= 0;
    case 'eq':
      return order === 0;
    case 'ne':
      return order !== 0;
    case 'between': {
      // Межі — включно, і порядок операндів не важливий, як на сервері.
      const upper = decimalOf(rule.valueTo);
      if (upper === null) return false;
      const [low, high] = compareDecimal(operand, upper) <= 0 ? [operand, upper] : [upper, operand];
      return compareDecimal(value, low) >= 0 && compareDecimal(value, high) <= 0;
    }
  }
}

/**
 * Значення-приклад, яке людина ввела в панелі: число — числом (кома
 * дозволена, як в операнді), решта — текстом; `null` — порожньо.
 *
 * ⚠ Тип колонки приклад не несе, тож текст «5» у текстовій колонці тут
 * виглядає числом; сервер його числом не вважає. Сітка й Excel беруть
 * результат лише з сервера (`TableSliceDto.cellFormats`), тому розбіжність
 * обмежена прикладом у конструкторі.
 */
export function sampleValue(sample: string | null): RuleCellValue {
  if (sample === null) return null;

  const number = decimalOf(sample);
  return number === null ? { kind: 'other', value: sample } : { kind: 'number', value: number };
}

/** Чи спрацьовує правило на значенні-прикладі (`null` — порожня комірка). */
export function ruleMatches(rule: ConditionalRule, cell: string | null): boolean {
  return ruleMatchesValue(rule, sampleValue(cell));
}

/**
 * Перше повне правило колонки, що спрацьовує на значенні; `null` — жодне.
 *
 * ⚠ Перше, а не всі разом: порядок правил — їхній пріоритет, як у Excel зі
 * «зупинитися, якщо істина». Змішування кольорів двох правил дало б колір,
 * якого не задавав ніхто.
 */
export function firstMatchingRule(
  rules: readonly ConditionalRule[],
  columnCode: string,
  cell: string | null,
): ConditionalRule | null {
  return (
    rules.find(
      (rule) => rule.columnCode === columnCode && whyRuleIncomplete(rule) === null && ruleMatches(rule, cell),
    ) ?? null
  );
}

/**
 * Форма правила на дроті (`ConditionalFormatRuleDto`). ⚠ Своя, а не з
 * `schema.d.ts`: модулю досить опису полів, без залежності від контракту.
 */
export interface ConditionalRuleWire {
  readonly columnCode: string;
  readonly operator: string;
  readonly value?: string | null;
  readonly valueTo?: string | null;
  readonly backgroundHex?: string | null;
  readonly foregroundHex?: string | null;
  readonly isBold: boolean;
}

function isOperator(value: string): value is ConditionOperator {
  return (ConditionOperators as readonly string[]).includes(value);
}

/**
 * Правило з відповіді сервера. `null` у полях — порожньо (колір теми, немає
 * операнда). Невідомий оператор — `null`: сервер такого не збереже
 * (`ECR-CFG-0422`), а вгадати, що він означав, клієнт не може.
 */
export function ruleFromWire(wire: ConditionalRuleWire): ConditionalRule | null {
  if (!isOperator(wire.operator)) return null;

  return {
    columnCode: wire.columnCode,
    operator: wire.operator,
    value: wire.value ?? '',
    valueTo: wire.valueTo ?? '',
    backgroundHex: wire.backgroundHex ?? '',
    foregroundHex: wire.foregroundHex ?? '',
    isBold: wire.isBold,
  };
}

/**
 * Правило для `PUT`. Порожні поля — `null`; операнд, якого оператор не бере
 * (`valueTo` поза `between`, будь-який для `empty`), не шлеться: сервер
 * відхилив би зайве (`condFormatOperand`), а людина його вже не бачить.
 */
export function ruleToWire(rule: ConditionalRule): Required<ConditionalRuleWire> {
  const count = operandCount(rule.operator);
  const blank = (text: string): string | null => (text.trim().length === 0 ? null : text.trim());

  return {
    columnCode: rule.columnCode,
    operator: rule.operator,
    value: count >= 1 ? blank(rule.value) : null,
    valueTo: count === 2 ? blank(rule.valueTo) : null,
    backgroundHex: blank(rule.backgroundHex),
    foregroundHex: blank(rule.foregroundHex),
    isBold: rule.isBold,
  };
}
