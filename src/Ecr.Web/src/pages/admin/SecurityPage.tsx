import { useState, type JSX } from 'react';
import { Badge, Group, Loader, SegmentedControl, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

interface RoleDto {
  id: number;
  code: string;
  name: string;
  permissions: string[];
}

interface UserDto {
  id: number;
  login: string;
  displayName: string;
  isActive: boolean;
  isLocal: boolean;
  roles: string[];
}

/**
 * Адміністрування безпеки: ролі, матриця прав, користувачі.
 *
 * ⚠ Матриця показує **оголошені** права ролей. Ефективні права конкретного
 * користувача рахує сервер і віддає в `/me`: складати їх тут означало б
 * другу реалізацію правил, яка рано чи пізно покаже дозвіл там, де сервер
 * відмовить.
 */
export function SecurityPage(): JSX.Element {
  const [tab, setTab] = useState('roles');

  const roles = useQuery({
    queryKey: ['roles'],
    queryFn: () => apiFetch<RoleDto[]>('/api/v1/roles'),
  });

  const users = useQuery({
    queryKey: ['users'],
    queryFn: () => apiFetch<{ items: UserDto[] }>('/api/v1/users?limit=200'),
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
                <Table.Tr key={role.id}>
                  <Table.Td>{role.name}</Table.Td>
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
                <Table.Th>{t('security.userRoles')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {(users.data?.items ?? []).map((user) => (
                <Table.Tr key={user.id} opacity={user.isActive ? 1 : 0.5}>
                  <Table.Td>{user.login}</Table.Td>
                  <Table.Td>{user.displayName}</Table.Td>
                  <Table.Td>
                    {/* Локальний і доменний вхід дають ту саму сесію; різниця
                        лише в тому, хто зберігає пароль. */}
                    <Badge variant="light">
                      {user.isLocal ? t('security.local') : t('security.domain')}
                    </Badge>
                  </Table.Td>
                  <Table.Td>
                    <Group gap={4}>
                      {user.roles.map((role) => (
                        <Badge key={role} size="sm" variant="outline">
                          {role}
                        </Badge>
                      ))}
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
