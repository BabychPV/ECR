import { useState, type JSX } from 'react';
import { Alert, Button, Code, Group, Select, Stack, Table, Text, TextInput, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { RoleView } from '@/api/types';
import { DateInput } from '@mantine/dates';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError, showDone } from '@/shared/ui/notify';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';

type Assignment = components['schemas']['GroupRoleAssignmentView'];
type AssignRequest = components['schemas']['AssignGroupRoleRequest'];
type Assigned = components['schemas']['GroupRoleAssignedResult'];

const URL = '/api/v1/security/group-assignments';

/**
 * Межа, якої немає: «діє без кінця» / «від завжди».
 *
 * ⛔ Не тире: тире в цьому застосунку означає «значення немає», а тут значення
 * є — необмеженість. Так само зроблено в `RegistriesPage` і константах методик.
 */
const Unbounded = '…';

/**
 * Календарна дата для сервера (`DateOnly`): `YYYY-MM-DD` тієї доби, яку обрано.
 *
 * ⛔ Не `toISOString()`: він переводить у UTC, і локальна північ на схід від
 * Гринвіча стає ПОПЕРЕДНЬОЮ добою (на захід — пізній вечір стає наступною).
 */
export function toMachineDate(value: Date | null): string | null {
  if (value === null) return null;

  const pad = (part: number): string => String(part).padStart(2, '0');
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}`;
}

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
  const [validFrom, setValidFrom] = useState<Date | null>(null);
  const [validTo, setValidTo] = useState<Date | null>(null);

  const from = toMachineDate(validFrom);
  const to = toMachineDate(validTo);
  // `YYYY-MM-DD` порівнюється як рядок; рівні дати дозволені — `validTo` включно.
  const orderBroken = from !== null && to !== null && to < from;

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
          // Незадана межа не шлеться взагалі: для сервера це те саме, що `null`.
          ...(from !== null && { validFrom: from }),
          ...(to !== null && { validTo: to }),
        } satisfies AssignRequest),
      }),
    onSuccess: async (result) => {
      await refresh();
      setPrincipal('');
      setValidFrom(null);
      setValidTo(null);
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

      {/* ⛔ Відмова переліку — НЕ «групам нічого не призначено»: порожня таблиця
          під заголовком читалася саме так. Форма нижче лишається — вона від
          переліку не залежить. */}
      <ErrorAlert error={list.error} onRetry={() => void list.refetch()} />

      {list.error === null && (
      <Table striped>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('groupRoles.group')}</Table.Th>
            <Table.Th>{t('security.role')}</Table.Th>
            <Table.Th>{t('groupRoles.validFrom')}</Table.Th>
            <Table.Th>{t('groupRoles.validTo')}</Table.Th>
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
                <Timestamp value={row.validFrom} dateOnly fallback={Unbounded} />
              </Table.Td>
              <Table.Td>
                <Timestamp value={row.validTo} dateOnly fallback={Unbounded} />
              </Table.Td>
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
      )}

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
        {/* `valueFormat` заданий кодом — однозначний і не залежить від локалі браузера. */}
        <DateInput
          label={t('groupRoles.validFrom')}
          valueFormat="YYYY-MM-DD"
          placeholder={Unbounded}
          clearable
          value={validFrom}
          onChange={setValidFrom}
        />
        <DateInput
          label={t('groupRoles.validTo')}
          valueFormat="YYYY-MM-DD"
          placeholder={Unbounded}
          clearable
          value={validTo}
          onChange={setValidTo}
          error={orderBroken ? t('groupRoles.validityOrder') : undefined}
        />
        <Button
          disabled={roleId === null || principal.trim().length === 0 || orderBroken}
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
