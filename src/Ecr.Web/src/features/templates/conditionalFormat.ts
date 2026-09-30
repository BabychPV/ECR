import { normalizeDecimal } from '@/shared/format';

/**
 * Умовне форматування комірок за правилами шаблону (`ФВ-2.7`) — клієнтська
 * модель правила й перевірка «чи спрацьовує правило на значенні».
 *
 * Сервер зберігає правила в `cfg.ConditionalFormatRule` (`GET/PUT
 * …/template-versions/{id}/conditional-formats`, `D-234`) і віддає їх сітці
 * документа в зрізі таблиці (`ColumnDto.conditionalFormats`). Модель тут — те,
 * що редактор (`ConditionalFormatPanel.tsx`) і сітка (`DocumentGrid.tsx`)
 * розуміють однаково: той самий `firstMatchingRule` і в перегляді редактора, і
 * на живій комірці.
 *
 * ⚠ Порівняння — через `Number` після `normalizeDecimal`: для вибору кольору
 * межа точності `double` не має значення, а для збереження значень ця
 * функція не використовується ніде.
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

export type RuleBlocker = 'Column' | 'Value' | 'ValueTo' | 'Range' | 'Color' | 'Style';

/** Межа довжини операнда — `ConditionalFormatRule.MaxOperandLength` на сервері. */
export const MaxOperandLength = 64;

const HexColor = /^#[0-9a-fA-F]{6}$/;

function isOperandNumber(text: string): boolean {
  return text.trim().length <= MaxOperandLength && normalizeDecimal(text) !== null;
}

/**
 * Чому правило ще не повне; `null` — повне.
 *
 * ⛔ Ті самі умови, що перевіряє сервер (`ConditionalFormatRule.Validate`):
 * операнд — число для КОЖНОГО оператора з операндом, включно з «дорівнює»
 * (сервер текстового операнда не прийме — `condFormatOperand`), колір —
 * рівно `#rrggbb`. Правило, яке редактор назвав повним, не може отримати
 * відмову `422` при збереженні.
 */
export function whyRuleIncomplete(rule: ConditionalRule): RuleBlocker | null {
  if (rule.columnCode.trim().length === 0) return 'Column';

  const count = operandCount(rule.operator);

  if (count >= 1 && !isOperandNumber(rule.value)) return 'Value';

  if (count === 2) {
    if (!isOperandNumber(rule.valueTo)) return 'ValueTo';
    if (Number(normalizeDecimal(rule.valueTo)) < Number(normalizeDecimal(rule.value))) return 'Range';
  }

  for (const hex of [rule.backgroundHex, rule.foregroundHex]) {
    if (hex !== '' && !HexColor.test(hex)) return 'Color';
  }

  if (rule.backgroundHex === '' && rule.foregroundHex === '' && !rule.isBold) return 'Style';

  return null;
}

function numberOf(text: string): number | null {
  const normalized = normalizeDecimal(text);
  return normalized === null ? null : Number(normalized);
}

/** Чи спрацьовує правило на значенні комірки (`null` — порожня комірка). */
export function ruleMatches(rule: ConditionalRule, cell: string | null): boolean {
  const text = cell?.trim() ?? '';

  if (rule.operator === 'empty') return text.length === 0;
  if (rule.operator === 'notEmpty') return text.length > 0;
  if (text.length === 0) return false;

  const value = numberOf(text);
  const operand = numberOf(rule.value);

  if (rule.operator === 'eq' || rule.operator === 'ne') {
    // Обидва числа — порівнюємо як числа (`1.0` = `1`), інакше як текст.
    const equal = value !== null && operand !== null ? value === operand : text === rule.value.trim();
    return rule.operator === 'eq' ? equal : !equal;
  }

  if (value === null || operand === null) return false;

  switch (rule.operator) {
    case 'gt':
      return value > operand;
    case 'ge':
      return value >= operand;
    case 'lt':
      return value < operand;
    case 'le':
      return value <= operand;
    case 'between': {
      const upper = numberOf(rule.valueTo);
      return upper !== null && value >= operand && value <= upper;
    }
  }
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
 * `schema.d.ts`: модуль живе в чанку сітки, і тип тут — лише опис полів.
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
