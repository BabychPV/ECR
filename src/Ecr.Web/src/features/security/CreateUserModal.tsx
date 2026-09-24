import { useState, type JSX } from 'react';
import { Button, Group, Modal, MultiSelect, PasswordInput, Select, Text, TextInput } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { RoleView, UserIdResponse } from '@/api/types';
import { createUserBody } from '@/features/security/createUserBody';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';
import { useOpenGeneration } from '@/features/security/useOpenGeneration';

/**
 * Аргументи для кнопки-тумблера видимості пароля (`Q-260`).
 *
 * ⛔ Mantine ставить на цю кнопку `aria-hidden="true"` і `tabIndex={-1}` за
 * замовчуванням; сама наявність об'єкта знімає `aria-hidden`, а явний
 * `tabIndex: 0` повертає зупинку табом.
 *
 * ⚠ Напис — ЛІТЕРАЛ, не `t()`: ключа під нього в каталозі (`09-seed.sql`)
 * немає, і голий `t()` показав би читалці позначений ключ.
 */
const passwordToggleProps = { 'aria-label': 'Toggle password visibility', tabIndex: 0 } as const;

interface CreateUserModalProps {
  opened: boolean;
  onClose: () => void;
  roles: RoleView[];
  rolesError: Error | null;
  onRetry: () => void;
}

/**
 * Діалог «New user».
 *
 * ⛔ Той самий дефект, що й «New role» (`CreateRoleModal.tsx`): чернетка була
 * станом `SecurityPage`, і кожен символ перерендерював сторінку. Тепер
 * чернетка — у `CreateUserForm`, а скидання при Cancel/закритті (аудит-пас 5)
 * структурне: кожне відкриття монтує нову форму (`useOpenGeneration`).
 *
 * ⛔ Локальні користувачі заводяться лише тут (`A7-42`). Разовий пароль
 * ВВОДИТЬ адміністратор (`A7-60`): сервер його не генерує, повернути пароль у
 * відповіді API заборонено (`D-11`, `ФВ-6.11`), а обов'язкова зміна при
 * першому вході (`ФВ-6.18`) робить його справді разовим.
 */
export function CreateUserModal({ opened, onClose, roles, rolesError, onRetry }: CreateUserModalProps): JSX.Element {
  const generation = useOpenGeneration(opened);

  return (
    <Modal opened={opened} onClose={onClose} title={t('security.createUser')}>
      <CreateUserForm key={generation} onClose={onClose} roles={roles} rolesError={rolesError} onRetry={onRetry} />
    </Modal>
  );
}

function CreateUserForm({ onClose, roles, rolesError, onRetry }: Omit<CreateUserModalProps, 'opened'>): JSX.Element {
  const queryClient = useQueryClient();
  const [userName, setUserName] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [provider, setProvider] = useState('Windows');
  const [sid, setSid] = useState('');
  const [oneTimePassword, setOneTimePassword] = useState('');
  const [email, setEmail] = useState('');
  const [newUserRoles, setNewUserRoles] = useState<string[]>([]);

  const createUser = useMutation({
    mutationFn: () =>
      apiFetch<UserIdResponse>('/api/v1/users', {
        method: 'POST',
        // ⛔ Склад тіла — окремою чистою функцією (`createUserBody`): саме
        // там жили `A7-60`, `A7-61` і `A7-62`.
        body: JSON.stringify(
          createUserBody({
            userName,
            displayName,
            provider,
            sid,
            oneTimePassword,
            email,
            roleCodes: newUserRoles,
          }),
        ),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['users'] });
      onClose();
      showDone(t('security.userCreated'));
    },
    onError: showApiError,
  });

  return (
    <>
      <Select
        label={t('security.kind')}
        description={t('security.kindHint')}
        data={['Windows', 'Local']}
        value={provider}
        onChange={(value) => setProvider(value ?? 'Windows')}
        allowDeselect={false}
      />

      <TextInput
        mt="sm"
        label={t('security.login')}
        value={userName}
        onChange={(event) => setUserName(event.currentTarget.value)}
        data-autofocus
      />

      <TextInput
        mt="sm"
        label={t('security.name')}
        value={displayName}
        onChange={(event) => setDisplayName(event.currentTarget.value)}
      />

      {provider === 'Windows' && (
        <TextInput
          mt="sm"
          label={t('security.sid')}
          description={t('security.sidHint')}
          value={sid}
          onChange={(event) => setSid(event.currentTarget.value)}
        />
      )}

      {provider === 'Local' && (
        <>
          <PasswordInput
            mt="sm"
            label={t('security.oneTimePassword')}
            description={t('security.oneTimePasswordHint')}
            value={oneTimePassword}
            onChange={(event) => setOneTimePassword(event.currentTarget.value)}
            visibilityToggleButtonProps={passwordToggleProps}
          />
          <Text size="xs" c="dimmed" mt="xs">
            {t('security.localHint')}
          </Text>
        </>
      )}

      {/* ⛔ Адреса: без неї сповіщення не надходять нікому (`ФВ-12`). */}
      <TextInput
        mt="sm"
        label={t('security.email')}
        description={t('security.emailHint')}
        value={email}
        onChange={(event) => setEmail(event.currentTarget.value)}
      />

      {/* ⛔ Ролі задаються ОДРАЗУ: обліковий запис без жодної ролі виглядає
          працездатним і не може нічого. */}
      <ErrorAlert error={rolesError} onRetry={onRetry} />
      <MultiSelect
        mt="sm"
        label={t('security.roles')}
        description={t('security.rolesHint')}
        data={roles.map((r) => r.code)}
        value={newUserRoles}
        onChange={setNewUserRoles}
        searchable
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button
          // ⛔ Ролі не приїхали — не зберігаємо: порожній перелік тут не
          // вибір, а відмова довідника.
          disabled={
            userName.trim().length === 0
            || (provider === 'Local' && oneTimePassword.length === 0)
            || rolesError !== null
          }
          loading={createUser.isPending}
          onClick={() => createUser.mutate()}
        >
          {t('common.save')}
        </Button>
      </Group>
    </>
  );
}
