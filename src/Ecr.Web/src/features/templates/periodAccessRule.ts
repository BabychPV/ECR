import type {
  OutOfWindowBehavior,
  PeriodAccessRuleDto,
  PeriodAccessRuleKind,
  RowKind,
} from '@/api/types';

/**
 * Правила доступу до періоду (`ФВ-2.15`, W5.4).
 *
 * ⛔ Три дії (Create/Save/Delete), а не дві (PUT-за-кодом/DELETE), як у
 * `sheet.ts`/`validationRule.ts`: у `PeriodAccessRuleDef` немає поля `Code` і
 * жодного унікального індексу — єдина адреса, яку сутність узагалі має, це
 * `id`, призначений базою вже ПІСЛЯ створення (докладніше —
 * `PeriodAccessRuleHandlers.cs` на сервері). Тому тут немає єдиної
 * «чернетки»: створення (`CreateDraft`) і правка наявного правила
 * (`UpdateDraft`, адресована окремо відомим `id`) — різні форми.
 */

export const PeriodAccessRuleKinds: readonly PeriodAccessRuleKind[] = [
  'AlwaysReadOnly',
  'HeaderRows',
  'EditablePeriodOnly',
  'RelativeWindow',
  'SourceWindow',
  'Expression',
];

/**
 * Поведінка поза вікном доступу — без `Hide`.
 *
 * ⛔ `Hide` навмисно виключений із переліку для форми, хоч перелік домену
 * його й містить (для наявних рядків, `H-1`): конструктор правила на сервері
 * відхиляє його для НОВОГО правила (`ФВ-2.16`) — показаний тут варіант, який
 * сервер однаково відхилить, гірший за відсутній.
 */
export const OutOfWindowBehaviors: readonly Exclude<OutOfWindowBehavior, 'Hide'>[] = [
  'ReadOnly',
  'Warn',
  'AllowWithConfirmation',
];

export const RowKinds: readonly Exclude<RowKind, null>[] = ['Group', 'Item', 'Balance', 'Note', 'Header'];

/** Чернетка нового правила в редакторі. */
export interface CreatePeriodAccessRuleDraft {
  readonly ruleKind: PeriodAccessRuleKind;
  readonly onOutOfWindow: Exclude<OutOfWindowBehavior, 'Hide'>;
  readonly sheetDefId: number | null;
  readonly tableDefId: number | null;
  readonly roleId: number | null;
  readonly rowKind: RowKind | null;
  readonly fromSequence: number | null;
  readonly toSequence: number | null;
  readonly sourceColumnDefId: number | null;
  readonly relativeOffset: number | null;
  readonly conditionExpr: string;
}

/** Порожня чернетка нового правила. */
export function emptyPeriodAccessRuleDraft(): CreatePeriodAccessRuleDraft {
  return {
    ruleKind: 'AlwaysReadOnly',
    onOutOfWindow: 'ReadOnly',
    sheetDefId: null,
    tableDefId: null,
    roleId: null,
    rowKind: null,
    fromSequence: null,
    toSequence: null,
    sourceColumnDefId: null,
    relativeOffset: null,
    conditionExpr: '',
  };
}

/** Що саме заважає завести правило. */
export type PeriodAccessRuleBlocker = 'Target' | 'SourceColumn' | 'RelativeOffset' | 'Condition';

/**
 * Чому чернетку ще не можна надіслати; `null` — можна.
 *
 * ⛔ Не копія серверних правил: сервер відхиляє те саме
 * (`CK_PAR_Target`/`CK_PAR_Kind`) сам, форма лише не везе в мережу те, що
 * напевно повернеться відмовою.
 */
export function whyCannotCreatePeriodAccessRule(
  draft: CreatePeriodAccessRuleDraft,
): PeriodAccessRuleBlocker | null {
  if (draft.sheetDefId === null && draft.tableDefId === null) return 'Target';
  if (draft.ruleKind === 'SourceWindow' && draft.sourceColumnDefId === null) return 'SourceColumn';
  if (draft.ruleKind === 'RelativeWindow' && (draft.relativeOffset === null || draft.relativeOffset <= 0)) {
    return 'RelativeOffset';
  }
  if (draft.ruleKind === 'Expression' && draft.conditionExpr.trim().length === 0) return 'Condition';

  return null;
}

/** Чернетка правки наявного правила — за відомим `id`. */
export interface UpdatePeriodAccessRuleDraft {
  readonly onOutOfWindow: Exclude<OutOfWindowBehavior, 'Hide'>;
  readonly sheetDefId: number | null;
  readonly tableDefId: number | null;
  readonly roleId: number | null;
  readonly rowKind: RowKind | null;
}

/**
 * Чернетка правки з наявного правила.
 *
 * ⚠ `onOutOfWindow` наявного правила теоретично може бути `Hide` (застарілий
 * рядок, `H-1`): форма показує його як `ReadOnly` — те саме «заборонити»,
 * яким `Hide` і був, — а не показує варіант, якого немає в переліку форми.
 */
export function updatePeriodAccessRuleDraftOf(rule: PeriodAccessRuleDto): UpdatePeriodAccessRuleDraft {
  return {
    onOutOfWindow: rule.onOutOfWindow === 'Hide' ? 'ReadOnly' : rule.onOutOfWindow,
    sheetDefId: rule.sheetDefId,
    tableDefId: rule.tableDefId,
    roleId: rule.roleId,
    rowKind: rule.rowKind,
  };
}
