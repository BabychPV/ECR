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
          />
          <PasswordInput
            label={t('password.next')}
            value={next}
            onChange={(event) => setNext(event.currentTarget.value)}
            autoComplete="new-password"
          />
          <PasswordInput
            label={t('password.repeat')}
            value={repeat}
            onChange={(event) => setRepeat(event.currentTarget.value)}
            error={mismatch ? t('password.mismatch') : undefined}
            autoComplete="new-password"
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
