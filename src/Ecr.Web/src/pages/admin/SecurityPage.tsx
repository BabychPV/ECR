import { useState, type JSX } from 'react';
import { Badge, Group, Loader, SegmentedControl, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { RoleView, UserPage } from '@/api/types';
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
