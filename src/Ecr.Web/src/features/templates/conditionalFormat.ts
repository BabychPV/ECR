import { normalizeDecimal } from "@/shared/format";

/**
 * Умовне форматування комірок за правилами шаблону (`ФВ-2.7`) — клієнтська
 * модель правила й перевірка «чи спрацьовує правило на значенні».
 *
 * Сервер зберігає правила (`cfg.ConditionalFormatRule`, `GET/PUT
 * /template-versions/{id}/conditional-formats`, транспорт — `conditionalFormatApi.ts`).
 * Порожнє значення в API — `null`, у моделі редактора — `''`; перетворення —
 * `fromWire`/`toWire`. Модель тут — те, що редактор і застосування в сітці
 * мають розуміти однаково.
 *
 * ⚠ Порівняння — через `Number` після `normalizeDecimal`: для вибору кольору
 * межа точності `double` не має значення, а для збереження значень ця
 * функція не використовується ніде.
 */

export type ConditionOperator =
  "gt" | "ge" | "lt" | "le" | "eq" | "ne" | "between" | "empty" | "notEmpty";

export const ConditionOperators: readonly ConditionOperator[] = [
  "gt",
  "ge",
  "lt",
  "le",
  "eq",
  "ne",
  "between",
  "empty",
  "notEmpty",
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

/** Форма правила в API (`ConditionalFormatRuleDto`): порожнє — `null`, не `''`. */
export interface ConditionalRuleWire {
  backgroundHex: string | null;
  columnCode: string;
  foregroundHex: string | null;
  isBold: boolean;
  operator: string;
  value: string | null;
  valueTo: string | null;
}

const orEmpty = (text: string | null): string => text ?? "";
const orNull = (text: string): string | null =>
  text.trim() === "" ? null : text.trim();

/** З API до моделі редактора: `null` → `''`. */
export function fromWire(dto: ConditionalRuleWire): ConditionalRule {
  return {
    columnCode: dto.columnCode,
    operator: dto.operator as ConditionOperator,
    value: orEmpty(dto.value),
    valueTo: orEmpty(dto.valueTo),
    backgroundHex: orEmpty(dto.backgroundHex),
    foregroundHex: orEmpty(dto.foregroundHex),
    isBold: dto.isBold,
  };
}

/**
 * З моделі до API: `''` → `null`; операнди, яких оператор не потребує, теж
 * `null` (сервер відхиляє зайве значення в `empty`/`notEmpty`).
 */
export function toWire(rule: ConditionalRule): ConditionalRuleWire {
  const count = operandCount(rule.operator);

  return {
    columnCode: rule.columnCode,
    operator: rule.operator,
    value: count >= 1 ? orNull(rule.value) : null,
    valueTo: count === 2 ? orNull(rule.valueTo) : null,
    backgroundHex: orNull(rule.backgroundHex),
    foregroundHex: orNull(rule.foregroundHex),
    isBold: rule.isBold,
  };
}

export function emptyRule(columnCode = ""): ConditionalRule {
  return {
    columnCode,
    operator: "gt",
    value: "",
    valueTo: "",
    backgroundHex: "",
    foregroundHex: "",
    isBold: false,
  };
}

/** Чи потрібне операторові значення (і скільки). */
export function operandCount(operator: ConditionOperator): 0 | 1 | 2 {
  if (operator === "empty" || operator === "notEmpty") return 0;
  return operator === "between" ? 2 : 1;
}

export type RuleBlocker = "Column" | "Value" | "ValueTo" | "Range" | "Style";

/** Чому правило ще не повне; `null` — повне. */
export function whyRuleIncomplete(rule: ConditionalRule): RuleBlocker | null {
  if (rule.columnCode.trim().length === 0) return "Column";

  const count = operandCount(rule.operator);
  const isOrdering = rule.operator !== "eq" && rule.operator !== "ne";

  if (count >= 1) {
    if (rule.value.trim().length === 0) return "Value";
    // ⚠ «більше/менше/між» має сенс лише для чисел; «дорівнює» — і для тексту.
    if (isOrdering && normalizeDecimal(rule.value) === null) return "Value";
  }

  if (count === 2) {
    const to = normalizeDecimal(rule.valueTo);
    if (to === null) return "ValueTo";
    if (Number(to) < Number(normalizeDecimal(rule.value))) return "Range";
  }

  if (rule.backgroundHex === "" && rule.foregroundHex === "" && !rule.isBold)
    return "Style";

  return null;
}

function numberOf(text: string): number | null {
  const normalized = normalizeDecimal(text);
  return normalized === null ? null : Number(normalized);
}

/** Чи спрацьовує правило на значенні комірки (`null` — порожня комірка). */
export function ruleMatches(
  rule: ConditionalRule,
  cell: string | null,
): boolean {
  const text = cell?.trim() ?? "";

  if (rule.operator === "empty") return text.length === 0;
  if (rule.operator === "notEmpty") return text.length > 0;
  if (text.length === 0) return false;

  const value = numberOf(text);
  const operand = numberOf(rule.value);

  if (rule.operator === "eq" || rule.operator === "ne") {
    // Обидва числа — порівнюємо як числа (`1.0` = `1`), інакше як текст.
    const equal =
      value !== null && operand !== null
        ? value === operand
        : text === rule.value.trim();
    return rule.operator === "eq" ? equal : !equal;
  }

  if (value === null || operand === null) return false;

  switch (rule.operator) {
    case "gt":
      return value > operand;
    case "ge":
      return value >= operand;
    case "lt":
      return value < operand;
    case "le":
      return value <= operand;
    case "between": {
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
      (rule) =>
        rule.columnCode === columnCode &&
        whyRuleIncomplete(rule) === null &&
        ruleMatches(rule, cell),
    ) ?? null
  );
}
