import { useMemo, type JSX } from 'react';
import { Alert, Button, Group, Input, NativeSelect, NumberInput, Stack, Switch, TextInput } from '@mantine/core';
import type { TemplateStructureDto, ValidationSeverity } from '@/api/types';
import { ExpressionEditor } from '@/features/expressions/ExpressionEditor';
import type { ExpressionPlacement } from '@/features/expressions/api';
import { t } from '@/shared/i18n';
import { LocalizedInput } from '@/shared/ui/LocalizedInput';
import {
  ValidationScopes,
  whyCannotSaveValidationRule,
  type ValidationRuleBlocker,
  type ValidationRuleDraft,
} from './validationRule';

/**
 * Форма правила валідації таблиці (W5.4, продовження `ФВ-2.1` на
 * `ValidationRule`) — за зразком `SheetEditor`.
 *
 * ⚠ Форма нічого не вирішує про стан версії. Чи можна правити — каже сервер
 * (`isEditable` у структурі версії), а відхиляє домен (`ECR-TMPL-0409`); тут
 * лише `disabled`.
 */
export function ValidationRuleEditor({
  draft,
  templateVersionId,
  tableDefId,
  structure,
  disabled,
  saving,
  onChange,
  onSubmit,
  onCancel,
}: {
  draft: ValidationRuleDraft;
  /** Версія і таблиця правила — контекст перевірки й підказок виразу. */
  templateVersionId: number;
  tableDefId: number;
  structure?: TemplateStructureDto | undefined;
  disabled: boolean;
  saving: boolean;
  onChange: (next: ValidationRuleDraft) => void;
  onSubmit: () => void;
  onCancel: () => void;
}): JSX.Element {
  const blocker = whyCannotSaveValidationRule(draft);

  // ⚠ Мемоізовано: редактор перезапитує склад мови й перевірку на кожну зміну
  // розміщення ЗА ПОСИЛАННЯМ (`FormulaEditor.placementIdentity.test.tsx`).
  const { columnDefId } = draft;
  const placement = useMemo<ExpressionPlacement>(
    () => ({
      templateVersionId,
      tableDefId,
      ...(columnDefId === null ? {} : { columnDefId }),
    }),
    [templateVersionId, tableDefId, columnDefId],
  );

  const severities: readonly ValidationSeverity[] = ['Info', 'Warning', 'Error'];

  return (
    <Stack gap="sm">
      <TextInput
        label={t('validationRules.code')}
        description={t('validationRules.codeHint')}
        value={draft.code}
        disabled={disabled || !draft.isNew}
        onChange={(event) => onChange({ ...draft, code: event.currentTarget.value })}
      />

      <NativeSelect
        label={t('validationRules.severity')}
        description={t('validationRules.severityHint')}
        data={severities}
        disabled={disabled}
        value={draft.severity}
        onChange={(event) =>
          onChange({ ...draft, severity: event.currentTarget.value as ValidationSeverity })
        }
      />

      <NativeSelect
        label={t('validationRules.scope')}
        description={t('validationRules.scopeHint')}
        data={ValidationScopes.map((scope) => ({ value: String(scope.value), label: scope.label }))}
        disabled={disabled}
        value={String(draft.scope)}
        onChange={(event) => onChange({ ...draft, scope: Number(event.currentTarget.value) })}
      />

      <NumberInput
        label={t('validationRules.columnDefId')}
        description={t('validationRules.columnDefIdHint')}
        disabled={disabled}
        value={draft.columnDefId ?? ''}
        onChange={(value) =>
          onChange({ ...draft, columnDefId: typeof value === 'number' ? value : null })
        }
      />

      {/*
        ⛔ Той самий редактор виразів, що й у формул (`ФВ-9.15a`), а не голе
        текстове поле: предикат правила пишеться тією самою мовою шаблону, і
        без редактора в ньому не було ні підказок колонок після `[`, ні
        підкреслення помилки до збереження.

        ⚠ `labelElement="div"`: поле Monaco не є міткованим елементом форми,
        його доступна назва — `ariaLabel` самого редактора.
      */}
      <Input.Wrapper
        label={t('validationRules.expression')}
        description={t('validationRules.expressionHint')}
        labelElement="div"
      >
        <ExpressionEditor
          value={draft.expression}
          onChange={(expression) => onChange({ ...draft, expression })}
          dialect="Template"
          placement={placement}
          structure={structure}
          readOnly={disabled}
          ariaLabel={t('validationRules.expression')}
          height="80px"
        />
      </Input.Wrapper>

      <LocalizedInput
        label={t('validationRules.message')}
        description={t('validationRules.messageHint')}
        value={draft.messageL10n}
        onChange={(messageL10n) => onChange({ ...draft, messageL10n })}
      />

      <Switch
        label={t('validationRules.isActive')}
        description={t('validationRules.isActiveHint')}
        disabled={disabled}
        checked={draft.isActive}
        onChange={(event) => onChange({ ...draft, isActive: event.currentTarget.checked })}
      />

      {blocker !== null && <Alert color="statusWarning">{blockerLabel(blocker)}</Alert>}

      <Group>
        <Button variant="default" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button disabled={disabled || blocker !== null} loading={saving} onClick={onSubmit}>
          {t('validationRules.save')}
        </Button>
      </Group>
    </Stack>
  );
}

/** Підпис причини, з якої зберегти ще не можна. */
function blockerLabel(blocker: ValidationRuleBlocker): string {
  switch (blocker) {
    case 'CodeEmpty':
      return t('validationRules.errCode');
    case 'CodeInvalid':
      return t('validationRules.errCodeInvalid');
    case 'Expression':
      return t('validationRules.errExpression');
    case 'Message':
      return t('validationRules.errMessage');
    default:
      return blocker;
  }
}
