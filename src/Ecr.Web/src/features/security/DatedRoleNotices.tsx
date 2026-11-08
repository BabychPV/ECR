import type { JSX } from 'react';
import { Badge, Button, Group, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { UserRoleAssignmentView } from '@/api/types';
import { formatDate } from '@/shared/format/datetime';
import { todayDateOnly } from '@/shared/format/dateOnly';
import { t } from '@/shared/i18n';

/**
 * Строкові призначення ролей у формі доступу — передусім ті, що СПЛИЛИ.
 *
 * ⛔ P1 (людина 08.11, кореляція e16184de): форма будувалась лише з безстрокових ролей
 * (`GET …/roles`), тож роль, чия строкова підміна сплила (`LandRestricted`, кінець 26.10), виглядала
 * ВІДСУТНЬОЮ. Адміністратор додавав її знову — і отримував «Internal error»: рядок призначення в
 * базі лишався. Сервер тепер оновлює наявний рядок, а форма показує сплилу роль явно —
 * «LandRestricted — expired 26.10, re-assign?» — і дає повернути її одним натиском.
 *
 * ⚠ Той самий запит (`['user-role-assignments', userId]`), що й області (`useUserRoleScopes`):
 * React Query не шле його вдруге.
 */
export function DatedRoleNotices({
  userId,
  selected,
  label,
  onReassign,
  today = todayDateOnly(),
}: {
  userId: number | null;
  selected: readonly string[];
  /** Назва ролі для показу за кодом. */
  label: (code: string) => string;
  onReassign: (code: string) => void;
  /** Сьогодні `yyyy-MM-dd` (для тестів). */
  today?: string;
}): JSX.Element | null {
  const query = useQuery({
    queryKey: ['user-role-assignments', userId],
    // ⚠ Шлях літералом: сторож `EndpointCoverageTests` шукає споживача за текстом.
    queryFn: () => apiFetch<UserRoleAssignmentView[]>(`/api/v1/users/${userId ?? 0}/role-assignments`),
    enabled: userId !== null,
  });

  const dated = datedAssignments(query.data);
  if (dated.length === 0) return null;

  return (
    <Stack gap="xs" mt="xs" data-testid="dated-roles">
      {dated.map((row) => {
        const role = label(row.roleCode);
        const until = row.validTo ?? '';
        const date = formatDate(until);

        if (!isExpired(row, today)) {
          return (
            <Text key={row.roleCode} size="sm" c="dimmed" data-role-validity="active">
              {t('security.roleValidUntil', { role, date })}
            </Text>
          );
        }

        const pending = selected.includes(row.roleCode);

        return (
          <Group key={row.roleCode} gap="xs" wrap="wrap" data-role-validity="expired" data-role-code={row.roleCode}>
            <Badge size="sm" color="statusWarning" variant="light">
              {t('security.roleExpiredOn', { date })}
            </Badge>
            <Text size="sm">
              {pending
                ? t('security.roleReassignPending', { role, date })
                : t('security.roleExpiredReassign', { role, date })}
            </Text>
            {!pending && (
              <Button
                size="xs"
                variant="light"
                aria-label={t('security.reassignRoleNamed', { role })}
                onClick={() => onReassign(row.roleCode)}
              >
                {t('security.reassignRole')}
              </Button>
            )}
          </Group>
        );
      })}
    </Stack>
  );
}

/** Строкові особисті призначення з відповіді (безстрокові форма показує чіпами). */
export function datedAssignments(data: unknown): UserRoleAssignmentView[] {
  if (!Array.isArray(data)) return [];

  return (data as UserRoleAssignmentView[]).filter(
    (row) =>
      typeof row === 'object' &&
      row !== null &&
      typeof row.roleCode === 'string' &&
      typeof row.validTo === 'string' &&
      row.validTo.length > 0,
  );
}

/** Сплило: останній день дії (включно, як `RoleAssignment.IsEffectiveOn`) — раніше за сьогодні. */
export function isExpired(row: Pick<UserRoleAssignmentView, 'validTo'>, today: string): boolean {
  return typeof row.validTo === 'string' && row.validTo.slice(0, 10) < today;
}
