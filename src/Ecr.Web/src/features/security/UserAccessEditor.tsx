import { useEffect, useState, type JSX } from 'react';
import { Button, Group, Modal, MultiSelect, Stack, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { AffectedRolesResponse, RoleView, UserView } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
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

  // ⛔ UI-аудит, lane 1: обраний перелік МІГ бути непорожнім і водночас не
  // давати жодного права — роль без прав `AsyncBoundary`'s «ролей немає»
  // (нижче) не бачить узагалі, бо з погляду мультиселекту роль ПРИЗНАЧЕНА.
  // Адмін, що зняв єдину змістовну роль і додав замість неї порожню, не
  // отримував жодного натяку, чому обліковий запис і далі нічого не бачить.
  const grantsNothing =
    selected.length > 0 &&
    selected.every((code) => {
      const role = roles.find((r) => r.code === code);
      return role !== undefined && role.permissions.length === 0 && role.dangerousPermissions.length === 0;
    });

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
      {/*
       * ⛔ Невдалий запит ролей і справді порожній перелік раніше виглядали
       * ОДНАКОВО: обидва малювали ту саму жовту пересторогу «ролей немає», і
       * причину збою побачити не можна було нізвідки (`Q-254`). `AsyncBoundary`
       * малює помилку (`ErrorAlert`, `role="alert"`) окремо від порожнього
       * стану — той самий клас дефекту, що й `A7-04`.
       *
       * ⛔ УВАГА (аудит UI, lane 1): тут НАВМИСНО немає `isEmpty` —
       * `AsyncBoundary` без нього ніколи не підмінює дітей порожнім станом.
       * Раніше `isEmpty={() => selected.length === 0}` перевіряв ЖИВИЙ стан
       * форми, а не відповідь сервера: щойно адмін знімав останню роль-чіп
       * у `MultiSelect`, `AsyncBoundary` миттю ховав САМ `MultiSelect` і
       * малював натомість нередаговуваний текст — без жодного контролю,
       * щоб додати роль назад. Єдиний вихід був «Cancel», що відкидав і цю
       * зміну, і будь-яку іншу зроблену в тому самому сеансі (наприклад,
       * правку email). Попередження нижче (`security.noRolesTitle`/
       * `security.noRolesWarning`) лишається — тим самим текстом — але
       * ПОРЯД із контролем, а не ЗАМІСТЬ нього (той самий рисунок, що вже
       * working «Add grant» у `GrantsPanel.tsx`: кнопка стоїть ПОЗА
       * `AsyncBoundary`, тож порожній перелік грантів так само не ховає
       * спосіб додати перший).
       */}
      <AsyncBoundary<string[]>
        isPending={user !== null && assigned.isPending}
        error={assigned.error}
        data={user === null ? undefined : assigned.data}
        skeleton="form"
        onRetry={() => void assigned.refetch()}
      >
        {() => (
          <>
            <MultiSelect
              mt="md"
              label={t('security.roles')}
              description={t('security.rolesHint')}
              data={roles.map((r) => r.code)}
              value={selected}
              onChange={setSelected}
              searchable
            />

            {/*
             * ⛔ НЕ `<Alert>`: Mantine ставить йому `role="alert"` за
             * умовчанням, а це саме той стан, від якого `Q-254` навмисно
             * відрізняв «дійсно порожньо» (`AsyncBoundary.tsx`'s власний
             * коментар про `NoPermissionState`/`EmptyState` — обидва
             * СВІДОМО без `role="alert"`, щоб код помилки з кореляцією
             * (`ErrorAlert`) лишався єдиним, що читач екрана чує як
             * тривогу).
             */}
            {selected.length === 0 && (
              <Stack gap="xs" mt="xs">
                <Text size="sm" fw={600} c="statusWarning">
                  {t('security.noRolesTitle')}
                </Text>
                <Text size="sm" c="dimmed">
                  {t('security.noRolesWarning')}
                </Text>
              </Stack>
            )}

            {/* ⛔ UI-аудит, lane 1: роль(і) призначені, але жодна не несе
                жодного права — та сама пастка, що й «ролей немає», лише
                непомітна для самого мультиселекту. */}
            {grantsNothing && (
              <Stack gap="xs" mt="xs">
                <Text size="sm" fw={600} c="statusWarning">
                  {t('security.rolesGrantNothingTitle')}
                </Text>
                <Text size="sm" c="dimmed">
                  {t('security.rolesGrantNothingWarning')}
                </Text>
              </Stack>
            )}
          </>
        )}
      </AsyncBoundary>

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
