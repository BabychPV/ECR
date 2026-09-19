import { useState, type JSX } from 'react';
import { Alert, Button, Code, Group, Select, Stack, Table, Text, TextInput, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { RoleView } from '@/api/types';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

type Assignment = components['schemas']['GroupRoleAssignmentView'];
type AssignRequest = components['schemas']['AssignGroupRoleRequest'];
type Assigned = components['schemas']['GroupRoleAssignedResult'];

const URL = '/api/v1/security/group-assignments';

/** Небезпечні права з відмови `409`; `null` — відмова про інше. */
export function dangerousPermissions(error: unknown): string[] | null {
  if (!(error instanceof EcrApiError)) return null;

  const list = error.problem.extensions2?.['dangerousPermissions'];
  return Array.isArray(list) ? list.map(String) : null;
}

/**
 * Ролі, призначені групам каталогу: перелік, відкликання, призначення.
 *
 * ⚠ Мінімальний споживач ендпоінтів; повноцінна вкладка «Групи» — окремо.
 */
export function GroupAssignmentsPanel({ roles }: { roles: RoleView[] }): JSX.Element {
  const queryClient = useQueryClient();
  const [roleId, setRoleId] = useState<string | null>(null);
  const [principal, setPrincipal] = useState('');

  const list = useQuery({ queryKey: ['group-assignments'], queryFn: () => apiFetch<Assignment[]>(URL) });
  const refresh = (): Promise<void> => queryClient.invalidateQueries({ queryKey: ['group-assignments'] });

  // ⚠ `handled`: відмову «роль небезпечна» показує сама форма — з переліком
  // прав і кнопкою підтвердження; решта відмов іде звичайним сповіщенням.
  const assign = useMutation<Assigned, Error, boolean>({
    meta: { handled: true },
    mutationFn: (confirmDangerous) =>
      apiFetch<Assigned>(URL, {
        method: 'POST',
        body: JSON.stringify({
          roleId: Number(roleId),
          principal: principal.trim(),
          confirmDangerous,
        } satisfies AssignRequest),
      }),
    onSuccess: async (result) => {
      await refresh();
      setPrincipal('');
      showDone(t(result.effectiveAfterNextSignIn ? 'groupRoles.assignedNextSignIn' : 'groupRoles.assigned'));
    },
    onError: (error) => {
      if (dangerousPermissions(error) === null) showApiError(error);
    },
  });

  const revoke = useMutation({
    // ⚠ Шлях літералом: сторож `EndpointCoverageTests` шукає споживача за текстом.
    mutationFn: (id: number) =>
      apiFetch<void>(`/api/v1/security/group-assignments/${id}`, { method: 'DELETE' }),
    onSuccess: async () => {
      await refresh();
      showDone(t('groupRoles.revoked'));
    },
    onError: showApiError,
  });

  const dangerous = dangerousPermissions(assign.error);

  return (
    <Stack gap="xs" mb="lg">
      <Title order={5}>{t('groupRoles.title')}</Title>

      <Table striped>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('groupRoles.group')}</Table.Th>
            <Table.Th>{t('security.role')}</Table.Th>
            <Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {(list.data ?? []).map((row) => (
            <Table.Tr key={row.id}>
              <Table.Td>
                {row.principalName !== null && <Text size="sm">{row.principalName}</Text>}
                <Code>{row.principalSid}</Code>
              </Table.Td>
              <Table.Td>{row.roleCode}</Table.Td>
              <Table.Td>
                <Button
                  size="compact-xs"
                  variant="subtle"
                  color="statusError"
                  loading={revoke.isPending && revoke.variables === row.id}
                  onClick={() => revoke.mutate(row.id)}
                >
                  {t('groupRoles.revoke')}
                </Button>
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>

      <Group align="flex-end" gap="xs">
        <Select
          label={t('security.role')}
          data={roles.map((role) => ({ value: String(role.id), label: role.code }))}
          value={roleId}
          onChange={setRoleId}
          searchable
        />
        <TextInput
          label={t('groupRoles.principal')}
          description={t('groupRoles.principalHint')}
          value={principal}
          onChange={(event) => setPrincipal(event.currentTarget.value)}
        />
        <Button
          disabled={roleId === null || principal.trim().length === 0}
          loading={assign.isPending}
          onClick={() => assign.mutate(false)}
        >
          {t('groupRoles.assign')}
        </Button>
      </Group>

      {dangerous !== null && (
        <Alert color="statusError" title={t('groupRoles.dangerousTitle')}>
          <Text size="sm">{dangerous.join(', ')}</Text>
          <Button mt="xs" size="xs" color="statusError" onClick={() => assign.mutate(true)}>
            {t('groupRoles.assignAnyway')}
          </Button>
        </Alert>
      )}
    </Stack>
  );
}
