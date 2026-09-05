import { useEffect, useState, type JSX } from 'react';
import { Button, Group, Modal, Textarea } from '@mantine/core';
import { t } from '@/shared/i18n';

/**
 * Діалог, який збирає **обов'язкову причину** дії.
 *
 * ⛔ Причина потрібна не для протоколу. Відхилення без пояснення повертає
 * роботу тому, хто не знає, що виправляти (`ФВ-5.15`); повернення періоду без
 * причини через рік не пояснить, чому числа за закритий місяць змінилися
 * (`D-67`). Домен вимагає її сам і відмовляє `ECR-DOC-0422` — але дізнаватися
 * про обов'язкове поле з відмови сервера означає натиснути кнопку, зачекати і
 * прочитати червоне там, де достатньо було спитати.
 *
 * ⚠ Кнопка підтвердження **вимкнена**, доки поле порожнє: увімкнена кнопка,
 * яка гарантовано дасть 422, — це та сама обіцянка, якої система не виконує.
 */
export function ReasonModal({
  opened,
  title,
  label,
  description,
  confirmLabel,
  isPending,
  onConfirm,
  onClose,
}: {
  opened: boolean;
  title: string;
  label: string;
  description?: string | undefined;
  confirmLabel: string;
  isPending?: boolean | undefined;
  onConfirm: (reason: string) => void;
  onClose: () => void;
}): JSX.Element {
  const [reason, setReason] = useState('');

  // ⚠ Поле очищається при КОЖНОМУ відкритті, а не при закритті. Причина
  // попередньої дії, що лишилася в полі, — найтихіший спосіб підписати
  // відхилення поясненням від зовсім іншого аркуша.
  useEffect(() => {
    if (opened) setReason('');
  }, [opened]);

  return (
    <Modal opened={opened} onClose={onClose} title={title}>
      <Textarea
        label={label}
        description={description}
        value={reason}
        onChange={(event) => setReason(event.currentTarget.value)}
        minRows={3}
        autosize
        data-autofocus
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button
          disabled={reason.trim().length === 0}
          loading={isPending === true}
          onClick={() => onConfirm(reason.trim())}
        >
          {confirmLabel}
        </Button>
      </Group>
    </Modal>
  );
}
