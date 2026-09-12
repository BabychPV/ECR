import type { JSX } from 'react';
import { Alert, Button, Group, NumberInput, Stack, Switch, TextInput } from '@mantine/core';
import { t } from '@/shared/i18n';
import { LocalizedInput } from '@/shared/ui/LocalizedInput';
import { type SheetBlocker, type SheetDraft, whyCannotSave } from './sheet';

/**
 * Форма аркуша — перший вертикальний зріз авторства структури шаблону
 * (`ФВ-2.1`).
 *
 * ⛔ До цього зрізу `SheetDef` створював лише офлайновий генератор тестових
 * даних (`Ecr.DataGen`): у веб-інтерфейсі не було жодного способу завести
 * аркуш, не кажучи вже про таблицю, колонку чи рядок.
 *
 * ⚠ Форма нічого не вирішує про стан версії. Чи можна правити — каже сервер
 * (`isEditable` у структурі версії), а відхиляє домен (`ECR-TMPL-0409`); тут
 * лише `disabled`, щоб людина не заповнювала те, що однаково не збережеться.
 */
export function SheetEditor({
  draft,
  disabled,
  saving,
  onChange,
  onSubmit,
  onCancel,
}: {
  draft: SheetDraft;
  disabled: boolean;
  saving: boolean;
  onChange: (next: SheetDraft) => void;
  onSubmit: () => void;
  onCancel: () => void;
}): JSX.Element {
  const blocker = whyCannotSave(draft);

  return (
    <Stack gap="sm">
      <TextInput
        label={t('sheets.code')}
        description={t('sheets.codeHint')}
        value={draft.code}
        disabled={disabled || !draft.isNew}
        onChange={(event) => onChange({ ...draft, code: event.currentTarget.value })}
      />

      <LocalizedInput
        label={t('sheets.name')}
        description={t('sheets.nameHint')}
        value={draft.nameL10n}
        onChange={(nameL10n) => onChange({ ...draft, nameL10n })}
      />

      <TextInput
        label={t('sheets.group')}
        description={t('sheets.groupHint')}
        disabled={disabled}
        value={draft.sheetGroup}
        onChange={(event) => onChange({ ...draft, sheetGroup: event.currentTarget.value })}
      />

      <NumberInput
        label={t('sheets.ordinal')}
        description={t('sheets.ordinalHint')}
        disabled={disabled}
        value={draft.ordinal ?? ''}
        onChange={(value) =>
          onChange({ ...draft, ordinal: typeof value === 'number' ? value : draft.ordinal })
        }
      />

      <Switch
        label={t('sheets.mandatory')}
        description={t('sheets.mandatoryHint')}
        disabled={disabled}
        checked={draft.isMandatory}
        onChange={(event) => onChange({ ...draft, isMandatory: event.currentTarget.checked })}
      />

      <Switch
        label={t('sheets.visible')}
        description={t('sheets.visibleHint')}
        disabled={disabled}
        checked={draft.isVisible}
        onChange={(event) => onChange({ ...draft, isVisible: event.currentTarget.checked })}
      />

      {blocker !== null && <Alert color="statusWarning">{blockerLabel(blocker)}</Alert>}

      <Group>
        <Button variant="default" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button disabled={disabled || blocker !== null} loading={saving} onClick={onSubmit}>
          {t('sheets.save')}
        </Button>
      </Group>
    </Stack>
  );
}

/** Підпис причини, з якої зберегти ще не можна. */
function blockerLabel(blocker: SheetBlocker): string {
  switch (blocker) {
    case 'Code':
      return t('sheets.errCode');
    case 'Name':
      return t('sheets.errName');
    default:
      return blocker;
  }
}
