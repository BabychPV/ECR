import type { JSX } from 'react';
import { Alert, Button, Group, NativeSelect, NumberInput, Stack, Textarea, TextInput } from '@mantine/core';
import type { OutOfWindowBehavior, PeriodAccessRuleKind, RowKind } from '@/api/types';
import { t } from '@/shared/i18n';
import {
  OutOfWindowBehaviors,
  PeriodAccessRuleKinds,
  RowKinds,
  whyCannotCreatePeriodAccessRule,
  type CreatePeriodAccessRuleDraft,
  type PeriodAccessRuleBlocker,
  type UpdatePeriodAccessRuleDraft,
} from './periodAccessRule';

/**
 * Форма нового правила доступу до періоду (`ФВ-2.15`, W5.4) — за зразком
 * `SheetEditor`.
 *
 * ⚠ Вид правила (`RuleKind`) і його специфічний параметр задаються ЛИШЕ тут,
 * при створенні: `PUT …/{id}` (див. `PeriodAccessRuleManager` нижче) їх не
 * приймає — той самий поділ, що в `SheetEditor` між кодом (незмінним) і
 * рештою полів.
 */
export function PeriodAccessRuleEditor({
  draft,
  disabled,
  saving,
  onChange,
  onSubmit,
}: {
  draft: CreatePeriodAccessRuleDraft;
  disabled: boolean;
  saving: boolean;
  onChange: (next: CreatePeriodAccessRuleDraft) => void;
  onSubmit: () => void;
}): JSX.Element {
  const blocker = whyCannotCreatePeriodAccessRule(draft);

  return (
    <Stack gap="sm">
      <NativeSelect
        label={t('periodRules.kind')}
        description={t('periodRules.kindHint')}
        data={PeriodAccessRuleKinds}
        disabled={disabled}
        value={draft.ruleKind}
        onChange={(event) =>
          onChange({ ...draft, ruleKind: event.currentTarget.value as PeriodAccessRuleKind })
        }
      />

      <NativeSelect
        label={t('periodRules.outOfWindow')}
        description={t('periodRules.outOfWindowHint')}
        data={OutOfWindowBehaviors}
        disabled={disabled}
        value={draft.onOutOfWindow}
        onChange={(event) =>
          onChange({ ...draft, onOutOfWindow: event.currentTarget.value as Exclude<OutOfWindowBehavior, 'Hide'> })
        }
      />

      <NumberInput
        label={t('periodRules.sheetDefId')}
        description={t('periodRules.sheetDefIdHint')}
        disabled={disabled}
        value={draft.sheetDefId ?? ''}
        onChange={(value) => onChange({ ...draft, sheetDefId: typeof value === 'number' ? value : null })}
      />

      <NumberInput
        label={t('periodRules.tableDefId')}
        description={t('periodRules.tableDefIdHint')}
        disabled={disabled}
        value={draft.tableDefId ?? ''}
        onChange={(value) => onChange({ ...draft, tableDefId: typeof value === 'number' ? value : null })}
      />

      <NumberInput
        label={t('periodRules.roleId')}
        description={t('periodRules.roleIdHint')}
        disabled={disabled}
        value={draft.roleId ?? ''}
        onChange={(value) => onChange({ ...draft, roleId: typeof value === 'number' ? value : null })}
      />

      <NativeSelect
        label={t('periodRules.rowKind')}
        description={t('periodRules.rowKindHint')}
        data={[{ value: '', label: t('periodRules.rowKindAny') }, ...RowKinds.map((k) => ({ value: k, label: k }))]}
        disabled={disabled}
        value={draft.rowKind ?? ''}
        onChange={(event) =>
          onChange({
            ...draft,
            rowKind: event.currentTarget.value === '' ? null : (event.currentTarget.value as RowKind),
          })
        }
      />

      {draft.ruleKind === 'EditablePeriodOnly' && (
        <Group grow>
          <NumberInput
            label={t('periodRules.fromSequence')}
            disabled={disabled}
            value={draft.fromSequence ?? ''}
            onChange={(value) =>
              onChange({ ...draft, fromSequence: typeof value === 'number' ? value : null })
            }
          />
          <NumberInput
            label={t('periodRules.toSequence')}
            disabled={disabled}
            value={draft.toSequence ?? ''}
            onChange={(value) => onChange({ ...draft, toSequence: typeof value === 'number' ? value : null })}
          />
        </Group>
      )}

      {draft.ruleKind === 'SourceWindow' && (
        <NumberInput
          label={t('periodRules.sourceColumnDefId')}
          description={t('periodRules.sourceColumnDefIdHint')}
          disabled={disabled}
          value={draft.sourceColumnDefId ?? ''}
          onChange={(value) =>
            onChange({ ...draft, sourceColumnDefId: typeof value === 'number' ? value : null })
          }
        />
      )}

      {draft.ruleKind === 'RelativeWindow' && (
        <NumberInput
          label={t('periodRules.relativeOffset')}
          description={t('periodRules.relativeOffsetHint')}
          disabled={disabled}
          value={draft.relativeOffset ?? ''}
          onChange={(value) =>
            onChange({ ...draft, relativeOffset: typeof value === 'number' ? value : null })
          }
        />
      )}

      {draft.ruleKind === 'Expression' && (
        <Textarea
          label={t('periodRules.condition')}
          description={t('periodRules.conditionHint')}
          autosize
          minRows={2}
          disabled={disabled}
          value={draft.conditionExpr}
          onChange={(event) => onChange({ ...draft, conditionExpr: event.currentTarget.value })}
        />
      )}

      {blocker !== null && <Alert color="yellow">{createBlockerLabel(blocker)}</Alert>}

      <Group justify="flex-end">
        <Button disabled={disabled || blocker !== null} loading={saving} onClick={onSubmit}>
          {t('periodRules.add')}
        </Button>
      </Group>
    </Stack>
  );
}

/**
 * Форма правки й видалення НАЯВНОГО правила за його `id`.
 *
 * ⛔ Без цієї форми `PUT`/`DELETE …/period-access-rules/{id}` лишалися б
 * недосяжними з інтерфейсу: сервер поки не віддає перелік готових правил
 * версії (лише матрицю доступу — `AccessMatrix` — і оцінку по клітинках, а не
 * самі рядки з їхніми `id`). Тому `id` тут вводить людина: показаний одразу
 * після створення правила вище або взятий із журналу структурних змін.
 * Це чесна деградація, а не вигадана структура: коли з'явиться перелік
 * правил, ця форма адресуватиметься з нього, а не текстовим полем.
 */
export function PeriodAccessRuleManager({
  ruleId,
  draft,
  disabled,
  saving,
  onRuleIdChange,
  onChange,
  onSave,
  onDelete,
}: {
  ruleId: number | null;
  draft: UpdatePeriodAccessRuleDraft;
  disabled: boolean;
  saving: boolean;
  onRuleIdChange: (id: number | null) => void;
  onChange: (next: UpdatePeriodAccessRuleDraft) => void;
  onSave: () => void;
  onDelete: () => void;
}): JSX.Element {
  return (
    <Stack gap="sm">
      <TextInput
        label={t('periodRules.manageId')}
        description={t('periodRules.manageIdHint')}
        value={ruleId ?? ''}
        disabled={disabled}
        onChange={(event) => {
          const value = event.currentTarget.value.trim();
          onRuleIdChange(value.length === 0 ? null : Number(value));
        }}
      />

      <NativeSelect
        label={t('periodRules.outOfWindow')}
        data={OutOfWindowBehaviors}
        disabled={disabled || ruleId === null}
        value={draft.onOutOfWindow}
        onChange={(event) =>
          onChange({ ...draft, onOutOfWindow: event.currentTarget.value as Exclude<OutOfWindowBehavior, 'Hide'> })
        }
      />

      <NumberInput
        label={t('periodRules.sheetDefId')}
        disabled={disabled || ruleId === null}
        value={draft.sheetDefId ?? ''}
        onChange={(value) => onChange({ ...draft, sheetDefId: typeof value === 'number' ? value : null })}
      />

      <NumberInput
        label={t('periodRules.tableDefId')}
        disabled={disabled || ruleId === null}
        value={draft.tableDefId ?? ''}
        onChange={(value) => onChange({ ...draft, tableDefId: typeof value === 'number' ? value : null })}
      />

      <Group justify="flex-end">
        <Button
          variant="default"
          color="statusError"
          disabled={disabled || ruleId === null}
          loading={saving}
          onClick={onDelete}
        >
          {t('periodRules.delete')}
        </Button>
        <Button disabled={disabled || ruleId === null} loading={saving} onClick={onSave}>
          {t('periodRules.save')}
        </Button>
      </Group>
    </Stack>
  );
}

/** Підпис причини, з якої нове правило ще не можна завести. */
function createBlockerLabel(blocker: PeriodAccessRuleBlocker): string {
  switch (blocker) {
    case 'Target':
      return t('periodRules.errTarget');
    case 'SourceColumn':
      return t('periodRules.errSourceColumn');
    case 'RelativeOffset':
      return t('periodRules.errRelativeOffset');
    case 'Condition':
      return t('periodRules.errCondition');
    default:
      return blocker;
  }
}
