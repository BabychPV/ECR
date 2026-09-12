import { useState, type JSX } from 'react';
import { Button, Card, Center, PasswordInput, Stack, Text, Title } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { ChangePasswordRequest } from '@/api/types';
import { MeQueryKey } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { t } from '@/shared/i18n';

/**
 * Зміна пароля.
 *
 * ⛔ Доки стоїть `MustChangePassword`, це єдиний доступний екран (ФВ-6.18):
 * тимчасовий пароль знає той, хто його видав, і робота під ним не є роботою
 * названого користувача.
 */

/**
 * Аргументи для кнопки-тумблера видимості пароля (`Q-260`).
 *
 * ⛔ Mantine ставить на цю кнопку `aria-hidden="true"` і `tabIndex={-1}` за
 * замовчуванням, доки `visibilityToggleButtonProps` цього не перекриє
 * (`PasswordInput.mjs`) — сама наявність об'єкта вже знімає `aria-hidden`, а
 * явний `tabIndex: 0` повертає зупинку табом. Без цього тумблер існував лише
 * для миші: клавіатура й читалка його не бачили взагалі.
 *
 * ⚠ Напис — ЛІТЕРАЛ, не `t()`. Рядки цього застосунку йдуть винятково із
 * серверного каталогу (`GET /api/v1/ui-strings/...`, сам каталог наповнює
 * `09-seed.sql`), а цей файл — DDL/сід, виключно оркестраторський. Ключа під
 * цей напис там ще немає, і завести його звідси не можна: голий `t()` без
 * рядка в каталозі показав би позначений ключ (`⟦...⟧`) читалці замість
 * опису кнопки — рівно той дефект, від якого рятує `Missing`-позначка в
 * `shared/i18n`.
 */
const passwordToggleProps = { 'aria-label': 'Toggle password visibility', tabIndex: 0 } as const;
export function ChangePasswordPage(): JSX.Element {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [repeat, setRepeat] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const mismatch = next.length > 0 && repeat.length > 0 && next !== repeat;

  async function submit(): Promise<void> {
    setBusy(true);
    setError(null);

    try {
      await apiFetch('/api/v1/auth/change-password', {
        method: 'POST',
        body: JSON.stringify(
          { currentPassword: current, newPassword: next } satisfies ChangePasswordRequest,
        ),
      });

      // Профіль перечитується: саме він несе прапорець MustChangePassword,
      // і без цього користувач лишився б замкненим на цьому екрані.
      await queryClient.invalidateQueries({ queryKey: MeQueryKey });
      navigate('/', { replace: true });
    } catch (failure) {
      setError(failure);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Center>
      <Card withBorder w={420} p="lg">
        <Title order={4} mb="md">
          {t('password.title')}
        </Title>

        <Stack gap="sm">
          <PasswordInput
            label={t('password.current')}
            value={current}
            onChange={(event) => setCurrent(event.currentTarget.value)}
            autoComplete="current-password"
            visibilityToggleButtonProps={passwordToggleProps}
          />
          <PasswordInput
            label={t('password.next')}
            value={next}
            onChange={(event) => setNext(event.currentTarget.value)}
            autoComplete="new-password"
            visibilityToggleButtonProps={passwordToggleProps}
          />
          <PasswordInput
            label={t('password.repeat')}
            value={repeat}
            onChange={(event) => setRepeat(event.currentTarget.value)}
            error={mismatch ? t('password.mismatch') : undefined}
            autoComplete="new-password"
            visibilityToggleButtonProps={passwordToggleProps}
          />

          <Button loading={busy} disabled={mismatch || next.length === 0} onClick={() => void submit()}>
            {t('password.submit')}
          </Button>

          <ErrorAlert error={error} />

          {/* ⚠ Вимоги до пароля показуються ДО спроби: правила, видимі лише у
              відповіді про помилку, змушують вгадувати. */}
          <Text size="xs" c="dimmed">
            {t('password.policy')}
          </Text>
        </Stack>
      </Card>
    </Center>
  );
}
