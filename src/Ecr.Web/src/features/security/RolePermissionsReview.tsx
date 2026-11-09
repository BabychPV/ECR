import { useState, type JSX } from 'react';
import { Button, Checkbox, Group, Modal, Stack, Table, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch, apiFetchResponse, EcrApiError } from '@/api/client';
import type { RolePermissionsPreview, RoleView } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { showApiError, showDone } from '@/shared/ui/notify';
import { logSuppressedDetail, problemText } from '@/shared/ui/problemText';
import { t } from '@/shared/i18n';
import { PermissionCaption } from './PermissionCaption';
import { permissionLabel } from './permissionLabel';
import { roleLabel } from './roleLabel';
import { SecurityIcon } from './securityIcons';

/** Ключ `409`: набір прав ролі змінили після того, як його прочитали (`ReplaceRolePermissionsHandler`). */
const StaleKey = 'err.ECR-SEC-0409.permissionsStale';

/**
 * Попередній перегляд і збереження нового набору прав власної ролі (рішення людини 08.11, (v)).
 *
 * ⛔ Спершу вплив, потім запис: хто з носіїв що отримає і що втратить (підсумковий доступ до → після, з
 * урахуванням інших ролей людини), які групи зачеплено. Додані небезпечні права — окремий прапорець
 * підтвердження: без нього «Save» вимкнено, а сервер однаково відмовить (409).
 *
 * ⚠ Версія для `If-Match` — з цього перегляду; а що перегляд показує САМЕ той набір, від якого почалася правка,
 * звіряє `base` (права ролі на момент натиску «Edit permissions»): інакше чужа правка між ними затерлася б мовчки.
 */
export function RolePermissionsReview({
  role,
  base,
  next,
  onClose,
  onSaved,
}: {
  role: RoleView;
  /** Права ролі на момент початку правки. */
  base: readonly string[];
  /** Новий повний набір. */
  next: readonly string[];
  onClose: () => void;
  onSaved: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [confirmed, setConfirmed] = useState(false);
  const [conflict, setConflict] = useState<string | null>(null);
  const body = [...next].sort();

  const preview = useQuery({
    queryKey: ['role-permissions-preview', role.id, body.join(',')],
    queryFn: () =>
      apiFetch<RolePermissionsPreview>(`/api/v1/roles/${role.id}/permissions/preview`, {
        method: 'POST',
        body: JSON.stringify({ permissionCodes: body }),
      }),
    gcTime: 0,
    staleTime: 0,
    retry: false,
  });

  const changedMeanwhile =
    preview.data !== undefined && [...preview.data.current].sort().join(',') !== [...base].sort().join(',');

  const save = useMutation({
    mutationFn: async (version: string) => {
      await apiFetchResponse(`/api/v1/roles/${role.id}/permissions`, {
        method: 'PUT',
        body: JSON.stringify({ permissionCodes: body, confirmDangerous: confirmed }),
        headers: { 'If-Match': `"${version}"` },
      });
    },
    onSettled: async () => {
      await queryClient.invalidateQueries({ queryKey: ['roles'] });
    },
    onSuccess: () => {
      showDone(t('security.rolePermissionsSaved', { role: roleLabel(role) }));
      onSaved();
    },
    onError: (error) => {
      if (error instanceof EcrApiError && error.problem.extensions2?.['messageKey'] === StaleKey) {
        setConflict(messageOf(error));
        return;
      }
      showApiError(error);
    },
  });

  const dangerous = preview.data?.addedDangerous ?? [];
  const blocked = changedMeanwhile || conflict !== null || (dangerous.length > 0 && !confirmed);

  return (
    <Modal opened onClose={onClose} size="xl" title={t('security.reviewTitle', { role: roleLabel(role) })}>
      <AsyncBoundary<RolePermissionsPreview>
        isPending={preview.isPending}
        error={preview.error}
        data={preview.data}
        skeleton="form"
        onRetry={() => void preview.refetch()}
      >
        {(data) => (
          <Stack gap="md" data-testid="role-impact">
            <ChangeList added={data.added} removed={data.removed} dangerous={data.addedDangerous} />

            <Stack gap="xs">
              <Text fw={600}>{t('security.impactUsersTitle', { count: data.users.length })}</Text>
              {data.users.length === 0 ? (
                <Text size="sm" c="dimmed">
                  {t('security.impactNoUsers')}
                </Text>
              ) : (
                <Table.ScrollContainer minWidth={520}>
                  <Table data-testid="role-impact-users">
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>{t('security.impactUser')}</Table.Th>
                        <Table.Th>{t('security.impactAccess')}</Table.Th>
                        <Table.Th>{t('security.impactChange')}</Table.Th>
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {data.users.map((user) => (
                        <Table.Tr key={user.userId} data-impact-user={user.userName}>
                          <Table.Td>
                            {user.displayName}
                            {user.displayName !== user.userName && (
                              <Text span size="xs" c="dimmed" ml="xs">
                                {user.userName}
                              </Text>
                            )}
                          </Table.Td>
                          <Table.Td>
                            {t('security.impactCounts', { before: user.effectiveBefore, after: user.effectiveAfter })}
                          </Table.Td>
                          <Table.Td>
                            {user.gained.length === 0 && user.lost.length === 0 ? (
                              <Text size="sm" c="dimmed">
                                {t('security.impactNoChange')}
                              </Text>
                            ) : (
                              <ul className="ecr-sec-impact-changes">
                                {[...user.gained.map((code) => ['+', code] as const), ...user.lost.map((code) => ['−', code] as const)].map(([sign, code]) => (
                                  <li key={`${sign}${code}`} title={code} data-change={sign === '+' ? 'added' : 'removed'}>
                                    <span aria-hidden="true">{sign}</span>
                                    <span className="ecr-sec-sr">{sign === '+' ? t('security.changeAdded') : t('security.changeRemoved')}</span>
                                    {permissionLabel(code)}
                                  </li>
                                ))}
                              </ul>
                            )}
                          </Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </Table.ScrollContainer>
              )}
            </Stack>

            <Stack gap="xs">
              <Text fw={600}>{t('security.impactGroupsTitle', { count: data.groups.length })}</Text>
              {data.groups.length === 0 ? (
                <Text size="sm" c="dimmed">
                  {t('security.impactNoGroups')}
                </Text>
              ) : (
                <>
                  <Text size="sm" c="dimmed">
                    {t('security.impactGroupsHint')}
                  </Text>
                  <Text size="sm" ff="monospace" data-testid="role-impact-groups">
                    {data.groups.map((group) => group.sid).join(', ')}
                  </Text>
                </>
              )}
            </Stack>

            <Text size="sm" c="dimmed">
              {t('security.impactSessionHint')}
            </Text>

            {data.addedDangerous.length > 0 && (
              <Stack gap="xs" className="ecr-sec-danger-confirm" data-testid="role-dangerous-confirm">
                <Group gap="xs">
                  <SecurityIcon name="shieldAlert" />
                  <Text fw={600}>{t('security.dangerousConfirmTitle', { count: data.addedDangerous.length })}</Text>
                </Group>
                <Checkbox
                  checked={confirmed}
                  onChange={(event) => setConfirmed(event.currentTarget.checked)}
                  label={t('security.dangerousConfirmLabel', {
                    permissions: data.addedDangerous.map((code) => permissionLabel(code)).join(', '),
                  })}
                  description={
                    <Text span size="xs" ff="monospace">
                      {data.addedDangerous.join(', ')}
                    </Text>
                  }
                />
              </Stack>
            )}

            {(changedMeanwhile || conflict !== null) && (
              <Stack gap="xs" data-testid="role-permissions-conflict">
                <Text size="sm" fw={600} c="statusError">
                  {conflict ?? t('security.rolePermissionsChangedMeanwhile')}
                </Text>
                <Group>
                  <Button
                    variant="default"
                    onClick={() => {
                      void queryClient.invalidateQueries({ queryKey: ['roles'] });
                      onClose();
                    }}
                  >
                    {t('security.reloadRole')}
                  </Button>
                </Group>
              </Stack>
            )}

            <Group justify="flex-end">
              <Button variant="default" onClick={onClose}>
                {t('security.backToEditing')}
              </Button>
              <Button
                loading={save.isPending}
                disabled={blocked || (data.added.length === 0 && data.removed.length === 0)}
                onClick={() => save.mutate(data.version)}
              >
                {t('security.savePermissions')}
              </Button>
            </Group>
          </Stack>
        )}
      </AsyncBoundary>
    </Modal>
  );
}

function ChangeList({
  added,
  removed,
  dangerous,
}: {
  added: readonly string[];
  removed: readonly string[];
  dangerous: readonly string[];
}): JSX.Element {
  return (
    <Stack gap="xs" data-testid="role-impact-changes">
      <Text fw={600}>{t('security.impactChangesTitle', { added: added.length, removed: removed.length })}</Text>
      <ul className="ecr-sec-change-list">
        {added.map((code) => (
          <li key={`+${code}`} data-change="added" data-permission={code}>
            <span aria-hidden="true">+</span>
            <span className="ecr-sec-sr">{t('security.changeAdded')}</span>
            <PermissionCaption code={code} />
            {dangerous.includes(code) && (
              <span className="ecr-sec-danger">
                <SecurityIcon name="shieldAlert" />
                {t('security.dangerousMark')}
              </span>
            )}
          </li>
        ))}
        {removed.map((code) => (
          <li key={`-${code}`} data-change="removed" data-permission={code}>
            <span aria-hidden="true">−</span>
            <span className="ecr-sec-sr">{t('security.changeRemoved')}</span>
            <PermissionCaption code={code} />
          </li>
        ))}
      </ul>
    </Stack>
  );
}

function messageOf(error: unknown): string {
  const shown = problemText(error);
  logSuppressedDetail(shown);

  return shown.detail ?? shown.title;
}
