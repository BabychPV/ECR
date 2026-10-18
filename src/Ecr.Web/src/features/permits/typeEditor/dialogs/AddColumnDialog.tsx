import { useEffect, useState, type FormEvent, type JSX } from 'react';
import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import type { PermitTypeColumn } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { ColumnFields } from './ColumnFields';
import type { UnitOption } from './names';
import { ReservedColumnCode, buildColumn, emptyDraftColumn, type DraftColumn } from './ops';

/**
 * Діалог «Add a column» (мокап `add-column`; Permits, PM-08c): назва, тип, одиниця, «обов'язковий».
 *
 * Самодостатній: нічого не читає з мережі, про стан сторінки редактора не знає. Результат —
 * готовий стовпець запиту (`onApply`); куди він лягає в тип, вирішує хост (`draftModel.addColumn`).
 *
 * ⚠ Код стовпця видається з назви й унікалізується проти `existingCodes` (без регістру) та
 * зарезервованого `PERMIT`; людина його не вводить, у мокапі поля коду немає.
 * ⛔ Стовпець у діалозі ніколи не ключовий: ключ таблиці задає майстер, а після публікації він
 * не змінюється (`keyChanged`).
 */
export interface AddColumnDialogProps {
  readonly opened: boolean;

  /** Таблиця, до якої додається стовпець (для підпису). */
  readonly tableCode: string;

  /** Коди стовпців таблиці, які вже є. */
  readonly existingCodes: readonly string[];
  readonly units: readonly UnitOption[];
  readonly onApply: (column: PermitTypeColumn) => void;
  readonly onClose: () => void;
}

export function AddColumnDialog({
  opened,
  tableCode,
  existingCodes,
  units,
  onApply,
  onClose,
}: AddColumnDialogProps): JSX.Element {
  const [draft, setDraft] = useState<DraftColumn>(() => emptyDraftColumn());
  const [showError, setShowError] = useState(false);

  // ⚠ Скидання при КОЖНОМУ відкритті: назва попереднього стовпця в полі — найтихіший спосіб
  // додати стовпець, якого людина не збиралася додавати вдруге.
  useEffect(() => {
    if (!opened) return;

    setDraft(emptyDraftColumn());
    setShowError(false);
  }, [opened]);

  function submit(event: FormEvent): void {
    event.preventDefault();

    if (draft.name.trim().length === 0) {
      setShowError(true);
      document.getElementById('pm-ac-name')?.focus();

      return;
    }

    const used = new Set<string>([ReservedColumnCode, ...existingCodes]);
    onApply(buildColumn(draft, existingCodes.length + 1, used));
    onClose();
  }

  return (
    <Modal opened={opened} onClose={onClose} title={t('permits.editor.dialogs.addColumn.title')}>
      <form onSubmit={submit} noValidate>
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            {t('permits.editor.dialogs.addColumn.intro', { table: tableCode })}
          </Text>

          <ColumnFields
            idPrefix="pm-ac"
            nameLabel={t('permits.editor.dialogs.col.nameEn')}
            value={draft}
            units={units}
            showKey={false}
            autoFocusName
            nameError={
              showError && draft.name.trim().length === 0
                ? t('permits.editor.dialogs.col.nameRequired')
                : undefined
            }
            onChange={(patch) => {
              setDraft((current) => ({ ...current, ...patch }));
            }}
          />

          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button type="submit" variant="filled">
              {t('permits.editor.dialogs.addColumn.apply')}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
