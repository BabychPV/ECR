import type { JSX } from 'react';
import { Alert, Button, Group, NativeSelect, NumberInput, Stack, TextInput } from '@mantine/core';
import type { TableDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { LocalizedInput } from '@/shared/ui/LocalizedInput';
import {
  TableLayoutKinds,
  TableRowModes,
  whyCannotSave,
  type TableBlocker,
  type TableDraft,
} from './table';

/**
 * Форма таблиці — другий вертикальний зріз авторства структури шаблону
 * (`W5.1`), той самий патерн, що й `SheetEditor` (`ФВ-2.1`, W5.0).
 *
 * ⚠ Форма нічого не вирішує про стан версії. Чи можна правити — каже сервер
 * (`isEditable` у структурі версії), а відхиляє домен (`ECR-TMPL-0409`); тут
 * лише `disabled`, щоб людина не заповнювала те, що однаково не збережеться.
 */
export function TableEditor({
  draft,
  disabled,
  saving,
  onChange,
  onSubmit,
  onCancel,
}: {
  draft: TableDraft;
  disabled: boolean;
  saving: boolean;
  onChange: (next: TableDraft) => void;
  onSubmit: () => void;
  onCancel: () => void;
}): JSX.Element {
  const blocker = whyCannotSave(draft);

  // ⚠ Стеля рядків має сенс лише там, де рядки додає користувач — та сама
  // перевірка, що й `TableDef.AllowsDynamicRows` на сервері. Форма не блокує
  // збереження за цим (сервер відхилить сам, `ECR-TMPL-0422`), але ховає
  // поле там, де воно однаково не застосовне.
  const allowsDynamicRows = draft.rowMode === 'Dynamic' || draft.rowMode === 'Mixed';

  return (
    <Stack gap="sm">
      <TextInput
        label={t('tableDef.code')}
        description={t('tableDef.codeHint')}
        value={draft.code}
        disabled={disabled || !draft.isNew}
        onChange={(event) => onChange({ ...draft, code: event.currentTarget.value })}
      />

      <LocalizedInput
        label={t('tableDef.name')}
        description={t('tableDef.nameHint')}
        value={draft.nameL10n}
        onChange={(nameL10n) => onChange({ ...draft, nameL10n })}
      />

      <NativeSelect
        label={t('tableDef.layoutKind')}
        description={t('tableDef.layoutKindHint')}
        disabled={disabled}
        data={TableLayoutKinds.map((kind) => ({ value: kind, label: layoutKindLabel(kind) }))}
        value={draft.layoutKind}
        onChange={(event) =>
          onChange({ ...draft, layoutKind: event.currentTarget.value as TableDto['layoutKind'] })
        }
      />

      <NativeSelect
        label={t('tableDef.rowMode')}
        description={t('tableDef.rowModeHint')}
        disabled={disabled}
        data={TableRowModes.map((mode) => ({ value: mode, label: rowModeLabel(mode) }))}
        value={draft.rowMode}
        onChange={(event) =>
          onChange({ ...draft, rowMode: event.currentTarget.value as TableDto['rowMode'] })
        }
      />

      {allowsDynamicRows && (
        <NumberInput
          label={t('tableDef.maxDynamicRows')}
          description={t('tableDef.maxDynamicRowsHint')}
          disabled={disabled}
          min={1}
          value={draft.maxDynamicRows ?? ''}
          onChange={(value) =>
            onChange({ ...draft, maxDynamicRows: typeof value === 'number' ? value : null })
          }
        />
      )}

      <NumberInput
        label={t('sheets.ordinal')}
        description={t('sheets.ordinalHint')}
        disabled={disabled}
        value={draft.ordinal ?? ''}
        onChange={(value) =>
          onChange({ ...draft, ordinal: typeof value === 'number' ? value : draft.ordinal })
        }
      />

      {blocker !== null && <Alert color="statusWarning">{blockerLabel(blocker)}</Alert>}

      <Group>
        <Button variant="default" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button disabled={disabled || blocker !== null} loading={saving} onClick={onSubmit}>
          {t('tableDef.save')}
        </Button>
      </Group>
    </Stack>
  );
}

/**
 * Підпис розкладки.
 *
 * ⛔ `switch` із явними ключами, а не зібраний `t(\`tableDef.layout${kind}\`)`:
 * зібраний ключ невидимий для сторожа каталогу (`D2-172`).
 */
function layoutKindLabel(kind: TableDto['layoutKind']): string {
  switch (kind) {
    case 'MonthsInColumns':
      return t('tableDef.layoutMonthsInColumns');
    case 'MonthsInRows':
      return t('tableDef.layoutMonthsInRows');
    case 'Static':
      return t('tableDef.layoutStatic');
    case 'PerPeriodInstance':
      return t('tableDef.layoutPerPeriodInstance');
    default:
      return kind;
  }
}

/** Підпис способу формування рядків; та сама причина виписаного `switch`. */
function rowModeLabel(mode: TableDto['rowMode']): string {
  switch (mode) {
    case 'Fixed':
      return t('tableDef.rowModeFixed');
    case 'Dynamic':
      return t('tableDef.rowModeDynamic');
    case 'Mixed':
      return t('tableDef.rowModeMixed');
    default:
      return mode;
  }
}

/** Підпис причини, з якої зберегти ще не можна. */
function blockerLabel(blocker: TableBlocker): string {
  switch (blocker) {
    case 'Code':
      return t('tableDef.errCode');
    case 'Name':
      return t('tableDef.errName');
    default:
      return blocker;
  }
}
