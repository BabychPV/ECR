import type { JSX } from 'react';
import {
  Alert,
  Button,
  Checkbox,
  Group,
  NativeSelect,
  Stack,
  Textarea,
  TextInput,
} from '@mantine/core';
import { t } from '@/shared/i18n';
import {
  OnSourceChangeValues,
  RelationKinds,
  whyCannotSave,
  type RelationBlocker,
  type RelationDraft,
  type TableOption,
} from './relation';
import type { TableRelationKind } from '@/api/types';

/**
 * Форма зв'язку між таблицями — те, чим виконано `ФВ-2.13`.
 *
 * ⛔ `NativeSelect`, а не `Select` Mantine. Причина не смакова: `Select`
 * рендериться в jsdom хвилинами (`D1-12`), тож форма на ньому лишилася б без
 * тесту — а тест на форму тут і є доказом того, що зв'язок налаштовується у
 * вебі. Нативний `<select>` до того ж працює з клавіатури й зі скрінрідером
 * без жодного нашого коду.
 *
 * ⚠ Форма нічого не вирішує про стан версії. Чи можна правити — каже сервер
 * (`isEditable` у DTO), а відхиляє домен (`ECR-TMPL-0409`); тут лише
 * `disabled`, щоб людина не заповнювала те, що однаково не збережеться.
 */
export function RelationForm({
  draft,
  tables,
  disabled,
  saving,
  onChange,
  onSubmit,
}: {
  draft: RelationDraft;
  tables: readonly TableOption[];
  disabled: boolean;
  saving: boolean;
  onChange: (next: RelationDraft) => void;
  onSubmit: () => void;
}): JSX.Element {
  const blocker = whyCannotSave(draft);

  // ⚠ Порожній варіант стоїть першим і має порожнє значення: без нього
  // нативний `<select>` показує першу таблицю як «обрану», хоча користувач
  // не обирав нічого, — і зв'язок пішов би не туди мовчки.
  const options = [
    { value: '', label: t('tables.pickTable') },
    ...tables.map((table) => ({ value: String(table.id), label: table.label })),
  ];

  return (
    <Stack gap="sm">
      <TextInput
        label={t('tables.relationCode')}
        description={t('tables.relationCodeHint')}
        value={draft.code}
        disabled={disabled || !draft.isNew}
        onChange={(event) => onChange({ ...draft, code: event.currentTarget.value })}
      />

      <NativeSelect
        label={t('tables.sourceTable')}
        description={t('tables.sourceTableHint')}
        data={options}
        disabled={disabled}
        value={draft.sourceTableDefId === null ? '' : String(draft.sourceTableDefId)}
        onChange={(event) =>
          onChange({
            ...draft,
            sourceTableDefId:
              event.currentTarget.value === '' ? null : Number(event.currentTarget.value),
          })
        }
      />

      <NativeSelect
        label={t('tables.targetTable')}
        description={t('tables.targetTableHint')}
        data={options}
        disabled={disabled}
        value={draft.targetTableDefId === null ? '' : String(draft.targetTableDefId)}
        onChange={(event) =>
          onChange({
            ...draft,
            targetTableDefId:
              event.currentTarget.value === '' ? null : Number(event.currentTarget.value),
          })
        }
      />

      <NativeSelect
        label={t('tables.relationKind')}
        description={t('tables.relationKindHint')}
        data={RelationKinds.map((kind) => ({ value: kind, label: kindLabel(kind) }))}
        disabled={disabled}
        value={draft.relationKind}
        onChange={(event) =>
          onChange({ ...draft, relationKind: event.currentTarget.value as TableRelationKind })
        }
      />

      <Textarea
        label={t('tables.matchJson')}
        description={t('tables.matchJsonHint')}
        autosize
        minRows={2}
        disabled={disabled}
        value={draft.matchJson}
        onChange={(event) => onChange({ ...draft, matchJson: event.currentTarget.value })}
      />

      <Textarea
        label={t('tables.mapJson')}
        description={t('tables.mapJsonHint')}
        autosize
        minRows={2}
        disabled={disabled}
        value={draft.mapJson}
        onChange={(event) => onChange({ ...draft, mapJson: event.currentTarget.value })}
      />

      <NativeSelect
        label={t('tables.onSourceChange')}
        description={t('tables.onSourceChangeHint')}
        data={OnSourceChangeValues.map((value) => ({
          value: String(value),
          label: onSourceChangeLabel(value),
        }))}
        disabled={disabled}
        value={String(draft.onSourceChange)}
        onChange={(event) =>
          onChange({ ...draft, onSourceChange: Number(event.currentTarget.value) })
        }
      />

      <Checkbox
        label={t('tables.isActive')}
        description={t('tables.isActiveHint')}
        disabled={disabled}
        checked={draft.isActive}
        onChange={(event) => onChange({ ...draft, isActive: event.currentTarget.checked })}
      />

      {blocker !== null && <Alert color="statusWarning">{blockerLabel(blocker)}</Alert>}

      <Group>
        <Button disabled={disabled || blocker !== null} loading={saving} onClick={onSubmit}>
          {t('tables.saveRelation')}
        </Button>
      </Group>
    </Stack>
  );
}

/**
 * Підпис виду зв'язку.
 *
 * ⛔ `switch` із явними ключами, а не зібраний `t(\`tables.kind.${kind}\`)`:
 * зібраний ключ невидимий для сторожа каталогу, і новий вид зв'язку з'явився
 * б на екрані як `⟦tables.kind.…⟧` (`D2-172`).
 */
function kindLabel(kind: TableRelationKind): string {
  switch (kind) {
    case 'Mirror':
      return t('tables.kindMirror');
    case 'Rollup':
      return t('tables.kindRollup');
    case 'Reference':
      return t('tables.kindReference');
    case 'Cascade':
      return t('tables.kindCascade');
    case 'Check':
      return t('tables.kindCheck');
    case 'Copy':
      return t('tables.kindCopy');
    default:
      return kind;
  }
}

/** Підпис причини, з якої зберегти ще не можна. */
function blockerLabel(blocker: RelationBlocker): string {
  switch (blocker) {
    case 'Code':
      return t('tables.errCode');
    case 'Source':
      return t('tables.errSource');
    case 'Target':
      return t('tables.errTarget');
    case 'Self':
      return t('tables.errSelf');
    case 'Match':
      return t('tables.errMatch');
    default:
      return blocker;
  }
}

/** Підпис реакції на зміну джерела; той самий довід, що й для видів зв'язку. */
export function onSourceChangeLabel(value: number): string {
  switch (value) {
    case 1:
      return t('tables.onChangeWarn');
    case 2:
      return t('tables.onChangeBlock');
    default:
      return t('tables.onChangeRecalc');
  }
}

export { kindLabel };
