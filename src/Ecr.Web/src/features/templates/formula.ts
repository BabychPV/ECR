import type { components } from '@/api/schema';
import type { ExpressionDialect } from '@/api/types';

/**
 * DTO псевдоніми для формул (`W5.3`).
 *
 * ⛔ Не в `@/api/types.ts` навмисно: цей зріз працює у власному переліку
 * файлів (`FormulaDefHandlers.cs`, `formulaApi.ts`, `formula.ts`,
 * `FormulaEditor.tsx`), поки два сусідні зрізи (`TableDef`+`ColumnDef`,
 * `RowDef`) паралельно доповнюють ту саму структуру версії в ізольованих
 * робочих копіях. `@/api/types.ts` — спільний файл: правка в ньому одразу
 * трьома зрізами одночасно означала б конфлікт злиття там, де жодної
 * СПРАВЖНЬОЇ суперечності немає — просто три різні додавання в той самий
 * список псевдонімів. Пряме посилання на `components['schemas'][...]` тут —
 * той самий підхід, яким сам `@/api/types.ts` називає типи (D-137
 * дозволяє псевдонім, а не забороняє йому жити поза цим файлом), лише без
 * спільної точки, за яку довелося б змагатися.
 */
export type FormulaDto = components['schemas']['FormulaDto'];
export type SaveFormulaDefRequest = components['schemas']['SaveFormulaDefRequest'];
export type FormulaScope = components['schemas']['FormulaScope'];

/**
 * Чернетка формули в редакторі (`W5.3`).
 *
 * ⚠ Ціль (`tableDefId`, `scope`, `target`) НЕ входить у форму: її визначає
 * колонка чи рядок, на якому відкрили редактор, а не людина. Це відрізняє
 * формулу від аркуша (`SheetDraft`), де код таки вводить людина: у формули
 * власної ідентичності немає взагалі (`FormulaDefHandlers.cs`) — нею є сама
 * ціль.
 */
export interface FormulaDraft {
  /** Таблиця, якій належить ціль. */
  readonly tableDefId: number;
  /** `Column` чи `Row`. */
  readonly scope: FormulaScope;
  /** `ColumnDefId` числом текстом при `Column`; `RowKey` при `Row`. */
  readonly target: string;
  readonly dialect: ExpressionDialect;
  readonly expression: string;
  /** Чи вже існує формула на цій цілі — визначає підпис форми. */
  readonly isNew: boolean;
}

/** Порожня чернетка нової формули на вказаній цілі. */
export function emptyFormulaDraft(
  tableDefId: number,
  scope: FormulaScope,
  target: string,
): FormulaDraft {
  return {
    tableDefId,
    scope,
    target,
    dialect: 'Template',
    expression: '',
    isNew: true,
  };
}

/** Чернетка з наявної формули — для правки. */
export function draftOfFormula(
  tableDefId: number,
  scope: FormulaScope,
  target: string,
  formula: FormulaDto,
): FormulaDraft {
  return {
    tableDefId,
    scope,
    target,
    dialect: formula.dialect,
    expression: formula.expression,
    isNew: false,
  };
}

/** Тіло запиту `PUT …/formulas/{scope}/{target}`. */
export function formulaBody(draft: FormulaDraft): SaveFormulaDefRequest {
  return { dialect: draft.dialect, expression: draft.expression };
}

/** Що саме заважає зберегти чернетку. */
export type FormulaBlocker = 'Expression';

/**
 * Чому чернетку ще не можна зберегти; `null` — можна.
 *
 * ⛔ Не копія серверної перевірки виразу: та йде через
 * `ExpressionEditor`/`POST /expressions/validate` і звіряє СИНТАКСИС і
 * посилання. Тут лише порожнеча — надсилати запит заради «поле не заповнене»
 * означало б чекати мережу там, де відповідь відома одразу.
 */
export function whyCannotSaveFormula(draft: FormulaDraft): FormulaBlocker | null {
  if (draft.expression.trim().length === 0) return 'Expression';

  return null;
}
