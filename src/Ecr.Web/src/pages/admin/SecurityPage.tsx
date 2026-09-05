import { useState, type JSX } from 'react';
import { Badge, Group, Loader, SegmentedControl, Switch, Table, Text, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { RoleView, SetAlertsRequest, UserPage } from '@/api/types';
import { GrantsPanel } from '@/pages/admin/GrantsPanel';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

/**
 * Адміністрування безпеки: ролі, матриця прав, користувачі.
 *
 * ⚠ Матриця показує **оголошені** права ролей. Ефективні права конкретного
 * користувача рахує сервер і віддає в `/me`: складати їх тут означало б
 * другу реалізацію правил, яка рано чи пізно покаже дозвіл там, де сервер
 * відмовить.
 *
 * ⛔ Форми беруться зі згенерованої схеми. До аудиту (`A7-05`) екран оголошував
 * власні `RoleDto` і `UserDto` з полями `name`, `login`, `isLocal` і `roles` —
 * жодного з них сервер не віддає, і обидві таблиці малювалися порожніми.
 */
export function SecurityPage(): JSX.Element {
  const [tab, setTab] = useState('roles');
  const queryClient = useQueryClient();

  // ⛔ Адресати алертів — ДАНІ, а не конфігурація (`D-125`). Перелік у змінних
  // оточення довелося б міняти розгортанням щоразу, коли хтось іде у
  // відпустку, — і саме тому його б не міняли.
  const alerts = useMutation({
    mutationFn: (target: { id: number; value: boolean }) =>
      apiFetch(`/api/v1/users/${target.id}/alerts`, {
        method: 'PUT',
        body: JSON.stringify({ receivesAlerts: target.value } satisfies SetAlertsRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['users'] });
    },
    onError: (error) => {
      // ⚠ Причина показується як є: «немає пошти» — це те, що людина може
      // виправити, а «не вдалося» — ні.
      notifications.show({
        color: 'red',
        message: error instanceof EcrApiError ? error.message : String(error),
      });
    },
  });

  const roles = useQuery({
    queryKey: ['roles'],
    queryFn: () => apiFetch<RoleView[]>('/api/v1/roles'),
  });

  const users = useQuery({
    queryKey: ['users'],
    queryFn: () => apiFetch<UserPage>('/api/v1/users?limit=200'),
    enabled: tab === 'users',
  });

  const permissions = [...new Set((roles.data ?? []).flatMap((role) => role.permissions))].sort();

  return (
    <>
      <PageHeader
        title={t('security.title')}
        actions={
          <SegmentedControl
            size="xs"
            value={tab}
            onChange={setTab}
            data={[
              { value: 'roles', label: t('security.roles') },
              { value: 'grants', label: t('security.grants') },
              { value: 'users', label: t('security.users') },
            ]}
          />
        }
      />

      <ErrorAlert error={roles.error ?? users.error} />

      {tab === 'roles' &&
        (roles.isPending ? (
          <Loader />
        ) : (
          <Table striped withTableBorder style={{ overflowX: 'auto' }}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('security.role')}</Table.Th>
                {permissions.map((permission) => (
                  <Table.Th key={permission}>
                    <Text size="xs">{permission}</Text>
                  </Table.Th>
                ))}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {(roles.data ?? []).map((role) => (
                <Table.Tr key={role.id} opacity={role.isActive ? 1 : 0.5}>
                  <Table.Td>
                    {role.code}
                    {role.isBuiltIn && (
                      <Badge ml={4} size="xs" variant="light">
                        {t('security.builtIn')}
                      </Badge>
                    )}

                    {/* ⚠ Небезпечні права показуються ОКРЕМО: у складені ролі
                        вони не входять навмисно, і адміністратор має бачити
                        різницю (ФВ-6.12, D-40). */}
                    {role.dangerousPermissions.length > 0 && (
                      <Badge ml={4} size="xs" color="red" variant="light">
                        {t('security.dangerous', { count: role.dangerousPermissions.length })}
                      </Badge>
                    )}
                  </Table.Td>
                  {permissions.map((permission) => (
                    <Table.Td key={permission}>
                      {role.permissions.includes(permission) ? '✓' : ''}
                    </Table.Td>
                  ))}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        ))}

      {/* ⛔ Гранти — окрема вкладка, а не колонка в матриці прав. Права
          відповідають на питання «що людина вміє», гранти — «до чого саме»;
          без другої відповіді перша не відкриває нічого (`A7-22`). */}
      {tab === 'grants' && <GrantsPanel roles={roles.data ?? []} />}

      {tab === 'users' &&
        (users.isPending ? (
          <Loader />
        ) : (
          <Table striped>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('security.login')}</Table.Th>
                <Table.Th>{t('security.name')}</Table.Th>
                <Table.Th>{t('security.kind')}</Table.Th>
                <Table.Th>{t('security.userState')}</Table.Th>
                <Table.Th>{t('security.alerts')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {(users.data?.items ?? []).map((user) => (
                <Table.Tr key={user.id} opacity={user.isActive ? 1 : 0.5}>
                  <Table.Td>{user.userName}</Table.Td>
                  <Table.Td>{user.displayName}</Table.Td>
                  <Table.Td>
                    {/* Локальний і доменний вхід дають ту саму сесію; різниця
                        лише в тому, хто зберігає пароль. */}
                    <Badge variant="light">{user.provider}</Badge>
                  </Table.Td>
                  <Table.Td>
                    {/* ⛔ Без пошти перемикач ВИМКНЕНИЙ, а не «вмикається і
                        мовчки не працює»: увімкнений адресат, якому нічого не
                        надсилається, виглядає як налаштований (`D-125`). */}
                    <Tooltip
                      label={t('security.alertsNeedEmail')}
                      disabled={user.email !== null && user.email !== ''}
                    >
                      <Switch
                        size="xs"
                        checked={user.receivesAlerts}
                        disabled={
                          user.email === null || user.email === '' || alerts.isPending
                        }
                        onChange={(event) =>
                          alerts.mutate({ id: user.id, value: event.currentTarget.checked })
                        }
                      />
                    </Tooltip>
                  </Table.Td>
                  <Table.Td>
                    <Group gap={4}>
                      {/* ⚠ Разовий пароль і блокування видно в переліку:
                          «користувач не може увійти» найчастіше пояснюється
                          саме ними, а не правами. */}
                      {user.mustChangePassword && (
                        <Badge size="sm" color="orange" variant="light">
                          {t('security.mustChangePassword')}
                        </Badge>
                      )}
                      {user.isLockedOut && (
                        <Badge size="sm" color="red" variant="light">
                          {t('security.lockedOut')}
                        </Badge>
                      )}
                      {user.isBootstrapAdmin && (
                        <Badge size="sm" variant="outline">
                          {t('security.bootstrap')}
                        </Badge>
                      )}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        ))}
    </>
  );
}
