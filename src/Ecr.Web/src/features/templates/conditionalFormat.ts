import { normalizeDecimal } from '@/shared/format';

/**
 * Умовне форматування комірок за правилами шаблону (`ФВ-2.7`) — клієнтська
 * модель правила й перевірка «чи спрацьовує правило на значенні».
 *
 * ⛔ СЕРВЕР ПРАВИЛ НЕ ЗБЕРІГАЄ. Ні `StyleDef`, ні `ColumnDef`, ні окрема
 * сутність не мають місця під умову (`D-234`, звіт лінії
 * `constructor-dnd-format`): потрібна схема (`cfg.ConditionalFormatRule` —
 * міграція EF), CRUD-ендпоінти й поле в структурі версії. Тому редактор
 * (`ConditionalFormatPanel.tsx`) дає скласти правила й перевірити їх на
 * прикладі значення, але збереження вимкнене і так і підписане. Модель тут —
 * те, що редактор і майбутнє застосування в сітці мають розуміти однаково.
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

export type RuleBlocker = 'Column' | 'Value' | 'ValueTo' | 'Range' | 'Style';

/** Чому правило ще не повне; `null` — повне. */
export function whyRuleIncomplete(rule: ConditionalRule): RuleBlocker | null {
  if (rule.columnCode.trim().length === 0) return 'Column';

  const count = operandCount(rule.operator);
  const isOrdering = rule.operator !== 'eq' && rule.operator !== 'ne';

  if (count >= 1) {
    if (rule.value.trim().length === 0) return 'Value';
    // ⚠ «більше/менше/між» має сенс лише для чисел; «дорівнює» — і для тексту.
    if (isOrdering && normalizeDecimal(rule.value) === null) return 'Value';
  }

  if (count === 2) {
    const to = normalizeDecimal(rule.valueTo);
    if (to === null) return 'ValueTo';
    if (Number(to) < Number(normalizeDecimal(rule.value))) return 'Range';
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
