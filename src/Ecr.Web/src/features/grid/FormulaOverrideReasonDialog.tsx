import { useState, type JSX } from 'react';
import { Button, Group, Modal, Stack, Text, Textarea } from '@mantine/core';
import { t } from '@/shared/i18n';
import { normalizeOverrideReason, OverrideReasonMaxLength } from './formulaOverride';

/**
 * ✎ D-461: причина «ручного значення замість формули» — обов'язкова (рішення людини 30.10).
 *
 * ⚠ Закриття без «Зберегти» (хрестик, `Esc`, клік поза, «Скасувати») — СКАСУВАННЯ: значення в комірку не ляже
 * (`onBeforeEdit` заблокував його синхронно, як і діалог підтвердження `ФВ-2.16`).
 *
 * ⚠ «Зберегти» вимкнена, доки причина не пройде ту саму перевірку, що й на сервері (`normalizeOverrideReason`):
 * порожня, задовга або з керівними символами причина дала б `422` уже після закриття діалогу.
 *
 * ⛔ Лінивий модуль (`D-132`): сітка тягне його `import()` лише тоді, коли діалог справді відкривається.
 */
export interface FormulaOverrideReasonDialogProps {
  /** Скільки комірок отримають ручне значення з цією причиною (1 — одинична правка). */
  readonly count: number;
  /** Причина, з якою відкрити поле (наявна причина ручної комірки), або `''`. */
  readonly initialReason: string;
  readonly onSave: (reason: string) => void;
  readonly onCancel: () => void;
}

export default function FormulaOverrideReasonDialog({
  count,
  initialReason,
  onSave,
  onCancel,
}: FormulaOverrideReasonDialogProps): JSX.Element {
  const [raw, setRaw] = useState(initialReason);
  const reason = normalizeOverrideReason(raw);
  const tooLong = raw.trim().length > OverrideReasonMaxLength;

  const save = (): void => {
    if (reason !== null) onSave(reason);
  };

  return (
    <Modal opened onClose={onCancel} title={t('grid.formulaOverride.dialogTitle')} data-formula-override-dialog>
      <Stack gap="sm">
        <Text size="sm">
          {count > 1
            ? t('grid.formulaOverride.dialogBodyMany', { count })
            : t('grid.formulaOverride.dialogBody')}
        </Text>
        <Textarea
          label={t('grid.formulaOverride.reasonLabel')}
          description={t('grid.formulaOverride.reasonHint', { max: OverrideReasonMaxLength })}
          required
          autoFocus
          autosize
          minRows={2}
          maxRows={6}
          value={raw}
          maxLength={OverrideReasonMaxLength + 100}
          error={tooLong ? t('grid.formulaOverride.reasonTooLong', { max: OverrideReasonMaxLength }) : undefined}
          onChange={(event) => setRaw(event.currentTarget.value)}
          onKeyDown={(event) => {
            // Ctrl+Enter — зберегти (Enter лишається переносом рядка: причина може бути в кілька рядків).
            if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
              event.preventDefault();
              save();
            }
          }}
          data-formula-override-reason
        />
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onCancel}>
            {t('grid.confirmCancel')}
          </Button>
          <Button onClick={save} disabled={reason === null} data-formula-override-save>
            {t('grid.formulaOverride.save')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
