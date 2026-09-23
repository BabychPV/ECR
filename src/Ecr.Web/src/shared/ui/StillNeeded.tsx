import type { JSX } from 'react';
import { Text } from '@mantine/core';
import { t } from '@/shared/i18n';

/**
 * Рядок «Still needed: …» під формою створення — чого бракує, щоб її надіслати.
 *
 * ⛔ Спільний, а не скопійований у кожен діалог (`U-18`). Доки він жив лише в
 * `CreateProjectModal`, сусідній «New registry» мав вимкнену кнопку без
 * жодного пояснення: два діалоги створення в одному продукті — два різні
 * контракти. Один компонент — один контракт: вимкнена кнопка підтвердження
 * завжди йде разом із переліком того, чого бракує.
 *
 * ⚠ Приймає вже перекладені підписи полів, а не ключі: підпис поля в переліку
 * мусить бути ТИМ САМИМ, що й над полем, і його знає лише форма.
 *
 * ⚠ Порожній перелік — нічого не малюється: форма готова, і рядок «ще
 * потрібно:» без жодного пункту читався б як збій.
 */
export function StillNeeded({ fields }: { fields: readonly string[] }): JSX.Element | null {
  if (fields.length === 0) return null;

  return (
    <Text size="xs" c="dimmed" mt="sm" data-still-needed>
      {t('common.stillNeeded', { fields: fields.join(', ') })}
    </Text>
  );
}
