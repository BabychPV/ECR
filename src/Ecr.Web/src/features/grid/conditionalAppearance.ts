import type { CellStyleDto, ColumnDto } from '@/api/types';
import { firstMatchingRule, ruleFromWire, type ConditionalRule } from '@/features/templates/conditionalFormat';
import { cellText } from './cellValue';

/**
 * Умовне форматування на живій сітці документа (`ФВ-2.7`).
 *
 * Правила їдуть у зрізі разом зі стилем колонки (`ColumnDto.conditionalFormats`,
 * `GetTableSliceHandler`), а не окремим запитом: `GET …/conditional-formats`
 * вимагає `Template.View`, якого в оператора може не бути.
 *
 * ⛔ Правило — ШАР ПОВЕРХ стилю автора (`cellAppearance.ts`), а не третій
 * механізм поруч: спрацьоване правило підміняє у `CellStyleDto` колонки лише
 * те, що задає саме (заливку, колір тексту, жирність), і далі все йде тим
 * самим шляхом — контраст під обидві теми, заливка лише на комірці без стану
 * (`X-10`). Тому незбережена чи заблокована комірка лишається впізнаваною і з
 * правилом.
 *
 * ⚠ Модуль живе в лінивому чанку сітки: статично його бере лише
 * `DocumentGrid`, тож вхідний чанк `DocumentPage` (`D-132`) він не розширює.
 */

const NoRules: readonly ConditionalRule[] = [];

/** Правила колонки в порядку застосування; невідомий оператор — пропускається. */
export function conditionalRulesOf(column: Pick<ColumnDto, 'conditionalFormats'>): readonly ConditionalRule[] {
  const wire = column.conditionalFormats;
  if (wire === null || wire === undefined || wire.length === 0) return NoRules;

  return wire.map(ruleFromWire).filter((rule): rule is ConditionalRule => rule !== null);
}

/** Спрацьоване правило і його позиція (1…) серед правил колонки. */
export interface ConditionalMatch {
  readonly rule: ConditionalRule;
  readonly position: number;
}

/**
 * Перше правило колонки, що спрацьовує на значенні комірки; `null` — жодне.
 *
 * ⚠ Значення — канонічним текстом (`cellText`), тим самим, що йде в буфер
 * обміну: `5.0000000000` зі сховища і `5` з редактора — одне число.
 */
export function conditionalMatchOf(
  rules: readonly ConditionalRule[],
  columnCode: string,
  value: unknown,
): ConditionalMatch | null {
  if (rules.length === 0) return null;

  const text = value === null || value === undefined ? null : cellText(value);
  const rule = firstMatchingRule(rules, columnCode, text);

  return rule === null ? null : { rule, position: rules.indexOf(rule) + 1 };
}

/**
 * Стиль автора з накладеним правилом. Правило задає лише те, що задає:
 * порожній колір правила лишає колір автора, жирність додається.
 */
export function withConditionalRule(
  style: CellStyleDto | null | undefined,
  rule: ConditionalRule,
): CellStyleDto {
  const base: CellStyleDto = style ?? {
    isBold: false,
    isItalic: false,
    foregroundArgb: null,
    backgroundArgb: null,
    horizontalAlign: null,
    verticalAlign: null,
    wrapText: false,
  };

  return {
    ...base,
    isBold: base.isBold || rule.isBold,
    foregroundArgb: rule.foregroundHex === '' ? base.foregroundArgb : argbOfHex(rule.foregroundHex),
    backgroundArgb: rule.backgroundHex === '' ? base.backgroundArgb : argbOfHex(rule.backgroundHex),
  };
}

/**
 * `#rrggbb` → ARGB зі знаком .NET `int` (непрозорий), дзеркало `hexOfArgb` у
 * `cellAppearance.ts`: `| 0` дає те саме від'ємне число, що й `StyleDef`.
 */
function argbOfHex(hex: string): number {
  return (0xff000000 | Number.parseInt(hex.slice(1), 16)) | 0;
}
