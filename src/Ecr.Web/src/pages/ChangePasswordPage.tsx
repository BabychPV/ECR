import { useState, type JSX } from 'react';
import { Button, Card, Center, PasswordInput, Stack, Text } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { EcrApiError, apiFetch } from '@/api/client';
import type { ChangePasswordRequest } from '@/api/types';
import { isPasswordTooShort } from '@/features/security/UserAdminActions';
import { MeQueryKey } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { problemText } from '@/shared/ui/problemText';
import { passwordToggleProps } from '@/shared/ui/a11yLabels';
import { t } from '@/shared/i18n';

/**
 * Зміна пароля.
 *
 * ⛔ Доки стоїть `MustChangePassword`, це єдиний доступний екран (ФВ-6.18):
 * тимчасовий пароль знає той, хто його видав, і робота під ним не є роботою
 * названого користувача.
 *
 * ⛔ `PageHeader`, а не голий `Title` (`Q-261`). Ця сторінка — ЄДИНИЙ виняток
 * усередині `AppLayout`, що обходив спільний заголовок: усі інші екрани
 * переносять фокус на заголовок і оголошують назву маршруту через
 * `RouteAnnouncer` при монтуванні (`shared/ui/PageHeader.tsx`, `ФВ-14.19`).
 * Саме тут це найбільш болюча відсутність — на цей екран потрапляють
 * майже виключно ПРИМУСОВИМ редиректом (`me.mustChangePassword`), тобто
 * користувач читалки опиняється на новому екрані без жодного пояснення,
 * чому раптом зник той, на якому він щойно був.
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
 * ✎ `X-26`: напис — із каталогу (`common.togglePasswordVisibility`) з
 * англійським запасним (`a11yLabels.ts`); раніше — англійський літерал.
 */

/**
 * Відмова «поточний пароль не підходить» (V-16).
 *
 * ⚠ Сервер відповідає `401`, але сеанс живий: транспорт не виводить із системи
 * саме на цьому ключі (`api/client.ts`, `isFormAnswer401`), а тут відмова йде
 * під поле поточного пароля — туди, де помилка й зроблена.
 */
export function isCurrentPasswordWrong(error: unknown): boolean {
  return (
    error instanceof EcrApiError
    && error.problem.errorCode === 'ECR-AUTH-0401'
    && error.problem.extensions2?.['messageKey'] === 'err.ECR-AUTH-0401.currentPasswordWrong'
  );
}
export function ChangePasswordPage(): JSX.Element {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [repeat, setRepeat] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const mismatch = next.length > 0 && repeat.length > 0 && next !== repeat;

  // ⚠ Мінімальна довжина пароля нізвідки клієнту не доступна ДО спроби: не
  // конфіг, не `GET /api/v1/public/bootstrap`, не публічна частина каталогу
  // рядків (сід `09-seed.sql` тримає `password.policy`/`err.ECR-PWD-0422.*`
  // приватними). Хардкодити число означало б розсинхронізацію з реальним
  // `PasswordPolicy.MinLength` (`UserStore.GetPolicyAsync`). Тому — реактивна
  // перевірка за тим самим патерном, що й у `UserAdminActions.tsx`: код
  // помилки й `messageKey`, а не текст, під полем, а не в загальному банері.
  const tooShort = isPasswordTooShort(error);
  const tooShortText = tooShort ? (problemText(error).detail ?? problemText(error).title) : null;
  const currentWrong = isCurrentPasswordWrong(error);
  const currentWrongText = currentWrong ? (problemText(error).detail ?? problemText(error).title) : null;

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
        <PageHeader title={t('password.title')} />

        <Stack gap="sm">
          <PasswordInput
            label={t('password.current')}
            value={current}
            onChange={(event) => setCurrent(event.currentTarget.value)}
            error={currentWrongText ?? undefined}
            autoComplete="current-password"
            visibilityToggleButtonProps={passwordToggleProps()}
          />
          <PasswordInput
            label={t('password.next')}
            value={next}
            onChange={(event) => setNext(event.currentTarget.value)}
            error={tooShortText ?? undefined}
            autoComplete="new-password"
            visibilityToggleButtonProps={passwordToggleProps()}
          />
          <PasswordInput
            label={t('password.repeat')}
            value={repeat}
            onChange={(event) => setRepeat(event.currentTarget.value)}
            error={mismatch ? t('password.mismatch') : undefined}
            autoComplete="new-password"
            visibilityToggleButtonProps={passwordToggleProps()}
          />

          <Button loading={busy} disabled={mismatch || next.length === 0} onClick={() => void submit()}>
            {t('password.submit')}
          </Button>

          {/* Решта відмов — банером; `tooShort` і хибний поточний пароль уже під своїми полями. */}
          {!tooShort && !currentWrong && <ErrorAlert error={error} />}

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
