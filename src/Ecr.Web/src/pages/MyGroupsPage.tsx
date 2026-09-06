import type { JSX } from 'react';
import { Divider, Select, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { AccessDiagnosticsView, UserPage } from '@/api/types';
import { AccessDiagnosticsPanel } from '@/features/security/AccessDiagnosticsPanel';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * «Мої групи» — звідки взялися ролі, і чому не взялися (`H-21`).
 *
 * ⛔ Маршрут **не закритий правом**, і це головне в ньому. Ролі доменних
 * користувачів призначаються на AD-групу (`ФВ-6.15`); доки контур без домену,
 * більшість отримає нуль ролей — і побачить порожні екрани, які нічим не
 * відрізняються від справної системи без даних. Вимагати на цю сторінку
 * `Security.ManageUsers` означало б лишити без відповіді рівно тих, заради
 * кого вона існує.
 *
 * ⚠ Друга половина — чужий запис. Вона під `Security.ManageUsers` і потрібна,
 * щоб адміністратор відповідав на «чому в мене немає доступу», **не заходячи
 * під людиною**: симуляція (`ФВ-6.16a`) для цього завелика — вона пише сеанс
 * в аудит і показує чужі дані, тоді як питання про самі призначення.
 */
export function MyGroupsPage(): JSX.Element {
  const session = useSession();
  const me = session.data;
  const manages = can(me, 'Security.ManageUsers');

  // ⚠ Вибір людини живе в адресі: «подивись, чому в нього немає доступу» має
  // бути посиланням, а не інструкцією з трьох кроків (`ФВ-14.29`).
  const [subject, setSubject] = useUrlState('user');

  const mine = useQuery({
    queryKey: ['my-groups'],
    queryFn: () => apiFetch<AccessDiagnosticsView>('/api/v1/security/my-groups'),
  });

  // ⚠ Ключ несе РОЗМІР сторінки. Екран безпеки бере `['users']` із
  // `limit=200`, і спільний ключ означав би, що перелік у цьому вибиральнику
  // мовчки залежить від того, яку сторінку відкрили першою. Префікс той
  // самий, тож `invalidateQueries(['users'])` після створення користувача
  // так само освіжає обидва.
  const users = useQuery({
    queryKey: ['users', 500],
    queryFn: () => apiFetch<UserPage>('/api/v1/users?limit=500'),
    enabled: manages,
  });

  const other = useQuery({
    queryKey: ['user-groups', subject],
    queryFn: () =>
      apiFetch<AccessDiagnosticsView>(
        `/api/v1/security/users/${encodeURIComponent(subject ?? '')}/groups`,
      ),
    enabled: manages && subject !== null,
  });

  return (
    <>
      <PageHeader title={t('myGroups.title')} />

      <Text size="sm" c="dimmed" mb="md">
        {t('myGroups.hint')}
      </Text>

      <AsyncBoundary<AccessDiagnosticsView>
        isPending={mine.isPending}
        error={mine.error}
        data={mine.data}
        skeleton="table"
        onRetry={() => void mine.refetch()}
      >
        {(view) => <AccessDiagnosticsPanel view={view} />}
      </AsyncBoundary>

      {manages && (
        <>
          <Divider my="lg" />

          <Stack gap="sm">
            {/* ⚠ Невдалий запит переліку НЕ виглядає як «користувачів немає»
                (`ФВ-14.22`): порожній вибиральник без пояснення — це той
                самий клас дефекту, що й `A7-04`, тільки на рівні поля. */}
            <Select
              label={t('myGroups.other')}
              description={t('myGroups.otherHint')}
              placeholder={t('myGroups.otherPlaceholder')}
              error={
                users.error === null || users.error === undefined
                  ? undefined
                  : t('state.errorTitle')
              }
              data={(users.data?.items ?? []).map((user) => ({
                value: String(user.id),
                label: user.userName,
              }))}
              value={subject}
              onChange={setSubject}
              searchable
              clearable
            />

            {subject !== null && (
              <AsyncBoundary<AccessDiagnosticsView>
                isPending={other.isPending}
                error={other.error}
                data={other.data}
                skeleton="table"
                onRetry={() => void other.refetch()}
              >
                {(view) => <AccessDiagnosticsPanel view={view} />}
              </AsyncBoundary>
            )}
          </Stack>
        </>
      )}
    </>
  );
}
