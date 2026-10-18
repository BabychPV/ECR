import type { PermitType, PermitTypeRule } from '@/features/permits/api/types';
import { codeFromName, displayName, uniqueCode } from '../dialogs/ops';

/*
 * Перевірки типу дозволу (Permits, PM-08c): плоский перелік для вкладки Checks і додавання
 * нової. Перевірка належить або шапці типу (`type.rules`), або таблиці (`table.rules`).
 */

/** Рівні перевірки в контракті. */
export const RuleSeverities = ['Warn', 'Block'] as const;

/** Рівень перевірки. */
export type RuleSeverity = (typeof RuleSeverities)[number];

/** До чого належить перевірка. */
export type RuleScope =
  | { readonly kind: 'permit' }
  | { readonly kind: 'table'; readonly tableCode: string; readonly tableName: string };

/** Рядок вкладки Checks. */
export interface RuleRow {
  /** Унікальний ключ рядка: область плюс код. */
  readonly key: string;
  readonly rule: PermitTypeRule;
  readonly scope: RuleScope;
}

/** Усі перевірки типу: спершу рівня типу, далі по таблицях у порядку складу. */
export function flattenRules(type: PermitType): RuleRow[] {
  const rows: RuleRow[] = type.rules.map((rule) => ({
    key: `permit:${rule.code}`,
    rule,
    scope: { kind: 'permit' },
  }));

  for (const table of type.tables) {
    for (const rule of table.rules) {
      rows.push({
        key: `table:${table.code}:${rule.code}`,
        rule,
        scope: { kind: 'table', tableCode: table.code, tableName: displayName(table.nameL10n) },
      });
    }
  }

  return rows;
}

/** Скільки перевірок у типі (для лічильника на вкладці). */
export function countRules(type: PermitType): number {
  return type.tables.reduce((sum, table) => sum + table.rules.length, type.rules.length);
}

/** Що вводить людина у діалозі «Add a check». */
export interface RuleInput {
  readonly name: string;
  readonly expression: string;
  readonly severity: RuleSeverity;
}

/** Помилки форми перевірки: назва й вираз обов'язкові. */
export function validateRuleInput(input: Pick<RuleInput, 'name' | 'expression'>): {
  name: boolean;
  expression: boolean;
} {
  return { name: input.name.trim().length === 0, expression: input.expression.trim().length === 0 };
}

/**
 * Перевірка для запиту: код із назви, унікальний серед усіх перевірок типу.
 *
 * ⚠ Унікальність ширша, ніж вимагає сервер (він порівнює в межах одного переліку): однакові
 * коди в шапці й таблиці читаються в журналі правил як одна перевірка.
 */
export function buildRule(input: RuleInput, existingCodes: readonly string[]): PermitTypeRule {
  return {
    code: uniqueCode(codeFromName(input.name, 'snake', 'Check'), existingCodes),
    nameL10n: { en: input.name.trim() },
    expression: input.expression.trim(),
    severity: input.severity,
  };
}

/** Коди всіх перевірок типу. */
export function allRuleCodes(type: PermitType): string[] {
  return flattenRules(type).map((row) => row.rule.code);
}
