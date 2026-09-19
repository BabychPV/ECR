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
 *
 * ⚠ `minLength` (директива №15, §2, Шар 2) — та сама думка, доведена до
 * домену: «.» довжиною в один символ формально непорожня і так само нічого не
 * пояснює тому, хто читатиме її через рік. Поріг задає ВИКЛИКАЧ, бо він
 * залежить від дії: відхилення аркуша й повернення закритого періоду коштують
 * різного.
 */
export function ReasonModal({
  opened,
  title,
  label,
  description,
  confirmLabel,
  isPending,
  minLength,
  onConfirm,
  onClose,
}: {
  opened: boolean;
  title: string;
  label: string;
  description?: string | undefined;
  confirmLabel: string;
  isPending?: boolean | undefined;

  /**
   * Скільки значущих символів потрібно, щоб кнопка ввімкнулася.
   *
   * ⚠ Без нього поріг — ОДИН символ, тобто дослівно чинна поведінка
   * («поле не порожнє»), а не нуль: нуль означав би увімкнену кнопку над
   * порожнім полем, що і є те `422`, заради якого діалог існує.
   */
  minLength?: number | undefined;
  onConfirm: (reason: string) => void;
  onClose: () => void;
}): JSX.Element {
  const [reason, setReason] = useState('');

  // ⛔ Рахуються символи ПІСЛЯ `trim()` — ті самі, що підуть на сервер
  // (`onConfirm(reason.trim())` нижче). Інакше поріг обходився б пробілами:
  // десять натискань на пробіл вмикали б кнопку, а домен отримував би
  // порожній рядок і відмовляв `ECR-DOC-0422` — тобто рівно те, чого діалог
  // мав не допустити.
  const required = Math.max(1, minLength ?? 1);
  const enough = reason.trim().length >= required;

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
          disabled={!enough}
          loading={isPending === true}
          onClick={() => onConfirm(reason.trim())}
        >
          {confirmLabel}
        </Button>
      </Group>
    </Modal>
  );
}
