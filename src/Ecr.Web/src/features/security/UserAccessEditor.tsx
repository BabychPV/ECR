import { useEffect, useState, type JSX } from 'react';
import { Alert, Button, Group, Modal, MultiSelect, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { AffectedRolesResponse, RoleView, UserView } from '@/api/types';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Ролі й адреса наявного користувача.
 *
 * ⛔ Двох дефектів тут було два, і обидва мовчазні.
 *
 * `A7-61`: способу призначити роль наявному користувачеві не існувало
 * **взагалі**. Ролі видавалися лише при створенні, а форма створення
 * надсилала порожній перелік — обліковий запис виходив працездатним на
 * вигляд і безправним насправді, і виправити це було нічим.
 *
 * `A7-62`: `User.Email` не присвоювався ніде в системі. `NotificationJob`
 * завжди отримував порожній перелік адресатів, тобто сповіщення (`ФВ-12`)
 * не надходили нікому, а перемикач алертів був вічно неактивним і виглядав
 * як налаштування, яке просто вимкнули.
 */
export function UserAccessEditor({
  user,
  roles,
  onClose,
}: {
  user: UserView | null;
  roles: RoleView[];
  onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [selected, setSelected] = useState<string[]>([]);
  const [email, setEmail] = useState('');

  const assigned = useQuery({
    queryKey: ['user-roles', user?.id],
    queryFn: () => apiFetch<string[]>(`/api/v1/users/${user?.id ?? 0}/roles`),
    enabled: user !== null,
  });

  // ⚠ Форма наповнюється тим, що ВЖЕ призначено. Порожній перелік на
  // відкритті виглядав би як «ролей немає», і збереження мовчки відібрало б
  // усі права.
  useEffect(() => {
    if (assigned.data !== undefined) {
      setSelected(assigned.data);
    }
  }, [assigned.data]);

  useEffect(() => {
    setEmail(user?.email ?? '');
  }, [user]);

  const save = useMutation({
    mutationFn: async () => {
      const result = await apiFetch<AffectedRolesResponse>(
        `/api/v1/users/${user?.id ?? 0}/roles`,
        { method: 'PUT', body: JSON.stringify({ roleCodes: selected }) },
      );

      await apiFetch(`/api/v1/users/${user?.id ?? 0}/email`, {
        method: 'PUT',
        body: JSON.stringify({ email: email.trim().length === 0 ? null : email.trim() }),
      });

      return result;
    },
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['users'] });
      await queryClient.invalidateQueries({ queryKey: ['user-roles', user?.id] });
      onClose();
      showDone(t('security.accessSaved', { count: result.roles }));
    },
    onError: showApiError,
  });

  return (
    <Modal
      opened={user !== null}
      onClose={onClose}
      title={`${t('security.access')} · ${user?.userName ?? ''}`}
    >
      {/* ⛔ Порожній набір ролей — це обліковий запис, який не може нічого.
          Сказати про це треба тут, а не залишити людину гадати, чому в неї
          порожні екрани. */}
      {selected.length === 0 && (
        <Alert color="statusWarning" title={t('security.noRolesTitle')}>
          {t('security.noRolesWarning')}
        </Alert>
      )}

      <MultiSelect
        mt="md"
        label={t('security.roles')}
        description={t('security.rolesHint')}
        data={roles.map((r) => r.code)}
        value={selected}
        onChange={setSelected}
        searchable
      />

      <TextInput
        mt="sm"
        label={t('security.email')}
        description={t('security.emailHint')}
        value={email}
        onChange={(event) => setEmail(event.currentTarget.value)}
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        {/* ⛔ Заблоковано, поки `assigned` не підтвердив саме поточні ролі
            користувача: PUT тут — повна заміна (`roleCodes: []` знімає
            ВСІ ролі), і невдалий запит лишає `selected` порожнім — без
            цього збереження мовчки забрало б усі права після звичайного
            збою мережі. */}
        <Button
          loading={save.isPending}
          disabled={assigned.isPending || Boolean(assigned.error)}
          onClick={() => save.mutate()}
        >
          {t('common.save')}
        </Button>
      </Group>
    </Modal>
  );
}
