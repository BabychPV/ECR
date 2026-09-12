import type { JSX } from 'react';
import { Alert, Button, Group, NativeSelect, NumberInput, Stack, Switch, Textarea, TextInput } from '@mantine/core';
import type { ValidationSeverity } from '@/api/types';
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
  disabled,
  saving,
  onChange,
  onSubmit,
  onCancel,
}: {
  draft: ValidationRuleDraft;
  disabled: boolean;
  saving: boolean;
  onChange: (next: ValidationRuleDraft) => void;
  onSubmit: () => void;
  onCancel: () => void;
}): JSX.Element {
  const blocker = whyCannotSaveValidationRule(draft);

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

      <Textarea
        label={t('validationRules.expression')}
        description={t('validationRules.expressionHint')}
        autosize
        minRows={2}
        disabled={disabled}
        value={draft.expression}
        onChange={(event) => onChange({ ...draft, expression: event.currentTarget.value })}
      />

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
    case 'Code':
      return t('validationRules.errCode');
    case 'Expression':
      return t('validationRules.errExpression');
    case 'Message':
      return t('validationRules.errMessage');
    default:
      return blocker;
  }
}
