import type { JSX } from 'react';
import { Alert, Badge, Code, Group, Stack, Table, Text, Title } from '@mantine/core';
import type { AccessDiagnosticsView } from '@/api/types';
import { t } from '@/shared/i18n';

/**
 * Звідки взялися (або не взялися) ролі людини (`H-21`).
 *
 * ⛔ Панель існує проти **тиші**. Ролі доменних користувачів призначаються на
 * AD-групу (`ФВ-6.15`), членство приходить із квитка входу (`ФВ-6.15a`), і
 * доки контур без домену — більшість отримає нуль ролей. Виглядає це
 * точнісінько як справна система без даних: людина входить, бачить порожні
 * переліки і вважає, що документів просто немає.
 *
 * ⚠ Показуються **всі** SID із квитка, а не самі лише збіги. Перелік із самих
 * збігів у людини без прав був би порожній — тобто та сама тиша, тільки на
 * окремому екрані. SID, який нічого не дав, і є тим, з чим ідуть до відділу AD.
 */
export function AccessDiagnosticsPanel({
  view,
}: {
  view: AccessDiagnosticsView;
}): JSX.Element {
  return (
    <Stack gap="md">
      <Group gap="xs" wrap="wrap">
        <Text fw={600}>{view.userName}</Text>
        <Badge variant="light">{view.provider}</Badge>

        {/* ⚠ Власний SID — не секрет і водночас єдине, чим людину впізнає
            каталог: без нього звернення до відділу AD доводиться починати з
            «а хто ви». */}
        {view.principalSid !== null && <Code>{view.principalSid}</Code>}
      </Group>

      {/*
       * ⛔ Найважливіший рядок панелі. Для ЧУЖОГО запису перелік груп порожній
       * завжди — квитка чужої сесії в нас немає (`P-02`), — і без цього
       * пояснення порожнеча читалася б як «людина ні в яких групах не
       * перебуває». Це неправда, і саме такою неправдою екран проти тиші
       * породив би власну.
       */}
      {!view.groupsFromTicket && (
        <Alert color="statusWarning" title={t('myGroups.notMineTitle')}>
          {t('myGroups.notMineHint')}
        </Alert>
      )}

      {view.groupsFromTicket && view.groups.length === 0 && (
        <Alert color="statusWarning" title={t('myGroups.noSidsTitle')}>
          {t('myGroups.noSidsHint')}
        </Alert>
      )}

      {view.groupsFromTicket && view.groups.length > 0 && view.unmatchedSids.length > 0 && (
        <Alert color="statusWarning" title={t('myGroups.unmatchedTitle')}>
          {t('myGroups.unmatchedHint', { count: view.unmatchedSids.length })}
        </Alert>
      )}

      {view.groups.length > 0 && (
        <Stack gap="xs">
          <Title order={5}>{t('myGroups.ticketGroups')}</Title>
          <Table.ScrollContainer minWidth={480}>
            <Table striped highlightOnHover>
              <Table.Caption>{t('myGroups.ticketGroupsHint')}</Table.Caption>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('myGroups.sid')}</Table.Th>
                  <Table.Th>{t('myGroups.matched')}</Table.Th>
                  <Table.Th>{t('security.roles')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {view.groups.map((group) => (
                  <Table.Tr key={group.sid}>
                    <Table.Td>
                      <Code>{group.sid}</Code>
                    </Table.Td>
                    <Table.Td>
                      {/* ⚠ Слово, а не колір: «збіглося» і «ні» мусять
                          читатися тим, хто не бачить кольору (`ФВ-14.16`). */}
                      <Badge color={group.matched ? 'green' : 'gray'} variant="light">
                        {group.matched ? t('myGroups.yes') : t('myGroups.no')}
                      </Badge>
                    </Table.Td>
                    <Table.Td>
                      {group.roleCodes.length === 0 ? '—' : group.roleCodes.join(', ')}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Stack>
      )}

      <Stack gap="xs">
        <Title order={5}>{t('myGroups.effective')}</Title>
        <Text size="sm">
          {view.effectiveRoleCodes.length === 0
            ? t('myGroups.noRoles')
            : view.effectiveRoleCodes.join(', ')}
        </Text>

        {view.personalRoleCodes.length > 0 && (
          <Text size="sm" c="dimmed">
            {t('myGroups.personal', { roles: view.personalRoleCodes.join(', ') })}
          </Text>
        )}

        {/* ⛔ «Роль була, підміна скінчилася» і «ролі не було ніколи» — різні
            відповіді: перша лікується продовженням призначення, друга —
            заведенням нового. */}
        {view.expiredRoleCodes.length > 0 && (
          <Text size="sm" c="statusWarning">
            {t('myGroups.expired', { roles: view.expiredRoleCodes.join(', ') })}
          </Text>
        )}
      </Stack>

      {view.groupAssignmentsInSystem.length > 0 && (
        <Stack gap="xs">
          <Title order={5}>{t('myGroups.catalogue')}</Title>
          <Table.ScrollContainer minWidth={480}>
            <Table striped>
              <Table.Caption>{t('myGroups.catalogueHint')}</Table.Caption>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('myGroups.sid')}</Table.Th>
                  <Table.Th>{t('security.roles')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {view.groupAssignmentsInSystem.map((assignment) => (
                  <Table.Tr key={assignment.sid}>
                    <Table.Td>
                      <Code>{assignment.sid}</Code>
                    </Table.Td>
                    <Table.Td>{assignment.roleCodes.join(', ')}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Stack>
      )}
    </Stack>
  );
}
