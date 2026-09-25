import { useState, type JSX } from 'react';
import { Button, Group, Modal, PasswordInput, Stack, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import type { UserView } from '@/api/types';
import { can, useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { showDone } from '@/shared/ui/notify';
import { problemText } from '@/shared/ui/problemText';
import { passwordToggleProps } from '@/shared/ui/a11yLabels';
import { t } from '@/shared/i18n';
import { LockReasonMaxLength, lockUser, resetUserPassword, unlockUser } from './userAdminApi';

// Та сама кнопка-тумблер, що й у формі створення користувача (`Q-260`).
// ✎ `X-26`: пропи — `a11yLabels.passwordToggleProps()` (каталог + запасний літерал).

/**
 * Відмова «пароль закороткий» — єдина, що належить ПОЛЮ, а не діалогу.
 *
 * ⚠ Розрізнення за кодом і `messageKey`, не за текстом: текст локалізований.
 */
export function isPasswordTooShort(error: unknown): boolean {
  return (
    error instanceof EcrApiError
    && error.problem.errorCode === 'ECR-PWD-0422'
    && error.problem.extensions2?.['messageKey'] === 'err.ECR-PWD-0422.tooShort'
  );
}

type LockAction = 'lock' | 'unlock';

/**
 * Дії адміністратора над чужим обліковим записом (`BE-12`): заблокувати,
 * розблокувати, скинути пароль.
 *
 * ⛔ Власного рядка кнопки не мають: сервер відмовив би `cannotTargetSelf`,
 * а свій пароль змінюється через `auth/change-password`.
 *
 * ⚠ «Скинути пароль» — лише для `provider === 'Local'`: пароль доменного
 * запису зберігає домен, і сервер відмовляє `domainPasswordReset`.
 */
export function UserAdminActions({ user }: { user: UserView }): JSX.Element | null {
  const session = useSession();
  const queryClient = useQueryClient();

  const [lockAction, setLockAction] = useState<LockAction | null>(null);
  const [reasonTooLong, setReasonTooLong] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [password, setPassword] = useState('');

  const refresh = (): Promise<void> => queryClient.invalidateQueries({ queryKey: ['users'] });

  // ⚠ `meta.handled`: відмову показує сам рядок (`ErrorAlert`), а не
  // страхувальна сітка — «останній адміністратор» є відповіддю по суті.
  const lock = useMutation<void, Error, { action: LockAction; reason: string }>({
    meta: { handled: true },
    mutationFn: ({ action, reason }) =>
      action === 'lock' ? lockUser(user.id, reason) : unlockUser(user.id, reason),
    onSuccess: async (_, { action }) => {
      await refresh();
      showDone(t(action === 'lock' ? 'security.userLocked' : 'security.userUnlocked'));
    },
    onSettled: () => setLockAction(null),
  });

  // ⛔ Пароль НЕ є змінною мутації: `mutate(password)` поклав би його в
  // `mutation.state.variables`, тобто в кеш запитів, звідки його читають
  // devtools. `mutationFn` бере його із замикання, а `gcTime: 0` прибирає
  // запис мутації одразу після завершення.
  const reset = useMutation<void, Error>({
    meta: { handled: true },
    gcTime: 0,
    mutationFn: () => resetUserPassword(user.id, password),
    onSuccess: async () => {
      closeReset();
      await refresh();
      showDone(t('security.passwordResetDone'));
    },
  });

  // ⛔ Поле очищається при КОЖНОМУ закритті — і скасуванні, і успіху: пароль,
  // що лишився в стані, виринув би у наступному відкритті для іншого рядка.
  function closeReset(): void {
    setResetting(false);
    setPassword('');
    reset.reset();
  }

  const self = session.data?.userId;

  if (!can(session.data, 'Security.ManageUsers') || self === undefined || self === user.id) {
    return null;
  }

  const openLock = (action: LockAction): void => {
    lock.reset();
    setReasonTooLong(false);
    setLockAction(action);
  };

  const tooShort = isPasswordTooShort(reset.error);
  const tooShortText = tooShort ? (problemText(reset.error).detail ?? problemText(reset.error).title) : null;

  return (
    <>
      {/* ⚠ Видимий текст короткий, а доступна назва — з іменем: у переліку
          кнопок читалка інакше чує десяток однакових «Lock» без рядка. */}
      <Group gap="xs" wrap="nowrap">
        {user.isLockedOut ? (
          <Button size="compact-xs" variant="subtle"
            aria-label={t('security.unlockUserNamed', { userName: user.userName })}
            onClick={() => openLock('unlock')}
          >
            {t('security.unlockUser')}
          </Button>
        ) : (
          <Button size="compact-xs" variant="subtle" color="statusError"
            aria-label={t('security.lockUserNamed', { userName: user.userName })}
            onClick={() => openLock('lock')}
          >
            {t('security.lockUser')}
          </Button>
        )}
        {user.provider === 'Local' && (
          <Button size="compact-xs" variant="subtle"
            aria-label={t('security.resetPasswordNamed', { userName: user.userName })}
            onClick={() => setResetting(true)}
          >
            {t('security.resetPassword')}
          </Button>
        )}
      </Group>

      {/* Відмова lock/unlock — під кнопками рядка: діалог закрито, повтор тієї
          самої дії дав би ту саму відповідь. */}
      <ErrorAlert error={lock.error} />

      <ReasonModal
        opened={lockAction !== null}
        title={`${t(lockAction === 'unlock' ? 'security.unlockUser' : 'security.lockUser')} · ${user.userName}`}
        label={t('workflow.reason')}
        // ⚠ `ReasonModal` не має верхньої межі (спільний компонент), тому
        // перевищення ловиться в `onConfirm` і показується тут же, в описі поля.
        description={
          reasonTooLong
            ? t('security.reasonTooLong', { max: LockReasonMaxLength })
            : t('security.lockReasonHint', { max: LockReasonMaxLength })
        }
        confirmLabel={t(lockAction === 'unlock' ? 'security.unlockUser' : 'security.lockUser')}
        isPending={lock.isPending}
        onConfirm={(reason) => {
          if (lockAction === null) return;
          if (reason.length > LockReasonMaxLength) {
            setReasonTooLong(true);
            return;
          }
          lock.mutate({ action: lockAction, reason });
        }}
        onClose={() => setLockAction(null)}
      />

      <Modal
        opened={resetting}
        onClose={closeReset}
        title={`${t('security.resetPassword')} · ${user.userName}`}
      >
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (password.length > 0) reset.mutate();
          }}
        >
          <Stack gap="sm">
            <Text size="sm">{t('security.resetPasswordHint')}</Text>

            {/* ⚠ Поля підтвердження немає: пароль разовий, власник змінить його
                при першому вході, а тумблер видимості дає звірити введене. */}
            <PasswordInput
              label={t('security.newPassword')}
              autoComplete="new-password"
              value={password}
              onChange={(event) => setPassword(event.currentTarget.value)}
              error={tooShortText}
              visibilityToggleButtonProps={passwordToggleProps()}
              data-autofocus
            />

            {/* Решта відмов — банером; `tooShort` уже під полем. */}
            {!tooShort && <ErrorAlert error={reset.error} />}

            <Group justify="flex-end">
              <Button variant="default" onClick={closeReset}>
                {t('common.cancel')}
              </Button>
              <Button type="submit" disabled={password.length === 0} loading={reset.isPending}>
                {t('security.resetPassword')}
              </Button>
            </Group>
          </Stack>
        </form>
      </Modal>
    </>
  );
}
