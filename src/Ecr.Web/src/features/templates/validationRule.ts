import type { ValidationRuleDto, ValidationSeverity } from '@/api/types';
import type { LocalizedValue } from '@/shared/ui/LocalizedInput';

/**
 * Чернетка правила валідації таблиці в редакторі (W5.4, продовження
 * `ФВ-2.1`..`ФВ-2.5` на `ValidationRule`).
 *
 * ⚠ Окремий тип від `ValidationRuleDto` — та сама причина, що й у
 * `SheetDraft`: `id` і `tableDefId` рахує сервер, форма ними не керує.
 */
export interface ValidationRuleDraft {
  /** Код правила; після створення не змінюється — це його адреса в API. */
  readonly code: string;
  readonly severity: ValidationSeverity;
  readonly scope: number;
  readonly expression: string;
  readonly messageL10n: LocalizedValue;
  readonly columnDefId: number | null;
  readonly isActive: boolean;

  /** Чи це нова чернетка: код нової ще можна набрати. */
  readonly isNew: boolean;
}

/** Область дії правила: 0 Cell, 1 Row, 2 Table, 3 Document. */
export const ValidationScopes: readonly { value: number; label: string }[] = [
  { value: 0, label: 'Cell' },
  { value: 1, label: 'Row' },
  { value: 2, label: 'Table' },
  { value: 3, label: 'Document' },
];

/** Порожня чернетка нового правила. */
export function emptyValidationRuleDraft(): ValidationRuleDraft {
  return {
    code: '',
    severity: 'Error',
    scope: 0,
    expression: '',
    messageL10n: {},
    columnDefId: null,
    isActive: true,
    isNew: true,
  };
}

/** Чернетка з наявного правила — для правки. */
export function validationRuleDraftOf(rule: ValidationRuleDto): ValidationRuleDraft {
  return {
    code: rule.code,
    severity: rule.severity,
    scope: rule.scope,
    expression: rule.expression,
    messageL10n: { ...(rule.messageL10n.values ?? {}) },
    columnDefId: rule.columnDefId,
    isActive: rule.isActive,
    isNew: false,
  };
}

/** Що саме заважає зберегти чернетку. */
export type ValidationRuleBlocker = 'Code' | 'Expression' | 'Message';

/**
 * Чому чернетку ще не можна зберегти; `null` — можна.
 *
 * ⛔ Не копія серверних правил: сервер відхиляє недопустимий код
 * `ECR-CFG-0422` сам, форма лише не везе в мережу те, що напевно повернеться
 * відмовою.
 */
export function whyCannotSaveValidationRule(draft: ValidationRuleDraft): ValidationRuleBlocker | null {
  if (draft.code.trim().length === 0) return 'Code';
  if (!/^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(draft.code)) return 'Code';
  if (draft.expression.trim().length === 0) return 'Expression';
  if (Object.values(draft.messageL10n).every((text) => text.trim().length === 0)) return 'Message';

  return null;
}
