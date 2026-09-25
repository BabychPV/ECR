import { Suspense, lazy, useMemo, useState, type JSX } from 'react';
import {
  Badge,
  Button,
  Group,
  SegmentedControl,
  Switch,
  Table,
  Text,
} from '@mantine/core';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type {
  PermissionCatalogItem,
  RoleView,
  SetAlertsRequest,
  UserPage,
  UserView,
} from '@/api/types';
import { CreateRoleModal } from '@/features/security/CreateRoleModal';
import { CreateUserModal } from '@/features/security/CreateUserModal';
import { RoleMatrix } from '@/features/security/RoleMatrix';
import { StartSimulationButton } from '@/features/security/SimulationPanel';
import { UserAdminActions } from '@/features/security/UserAdminActions';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { showApiError } from '@/shared/ui/notify';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/** Скільки користувачів за один запит (X-07): сторінка, а не стеля переліку. */
const UsersPageSize = 200;

/** Без адреси алерти нікуди надсилати — перемикач вимкнено з названою причиною (`D-125`). */
function noEmail(user: UserView): boolean {
  return user.email === null || user.email === '';
}

/** Id видимої причини поруч із вимкненим перемикачем — ціль `aria-describedby`. */
function alertsReasonId(userId: number): string {
  return `security-alerts-reason-${userId}`;
}

/**
 * Вкладка грантів — за `import()`.
 *
 * ⛔ Та сама причина, що й на `TemplateVersionPage`: маршрут стояв на 249.3 КБ
 * зі стелі 250 (`D-132`), тобто мав 0.7 КБ запасу на ВСІ майбутні правки
 * спільного коду. Гранти — окрема вкладка (`tab === 'grants'`), і за
 * замовчуванням відкривається вкладка ролей: код, який більшість відвідувачів
 * не показує жодного разу, платився кожним.
 *
 * ⚠ `fallback={null}`: перемикач вкладок (`SegmentedControl`) лишається на
 * місці й уже показує, що вкладка змінилася — порожнеча під ним триває рівно
 * стільки, скільки йде чанк із того самого походження. Скелет таблиці тут був
 * би гіршим: `GrantsPanel` сам малює власний `AsyncBoundary` зі скелетом, і
 * два скелети поспіль блимали б один в одного.
 */
const GrantsPanel = lazy(async () => ({
  default: (await import('@/pages/admin/GrantsPanel')).GrantsPanel,
}));

/**
 * ⛔ `UserAccessEditor` носить `<Modal opened={user !== null}>` усередині себе,
 * тобто рендериться ЗАВЖДИ. Гейт нижче (`accessUsed`) тому односторонній:
 * `editingAccess !== null` знімав би компонент із дерева в ту саму мить, коли
 * діалог починає закриватися, і `lazy` перестав би бути лише моментом
 * завантаження — він зіпсував би анімацію закриття. Доки кнопку «Access» не
 * натиснули, у дереві немає нічого (закрита `Modal` і так не рендерить рамки);
 * після першого натискання — рівно те, що було до цієї правки.
 */
const UserAccessEditor = lazy(async () => ({
  default: (await import('@/features/security/UserAccessEditor')).UserAccessEditor,
}));

/** Ролі груп каталогу — за `import()` з тієї ж причини, що й гранти: бюджет маршруту. */
const GroupAssignmentsPanel = lazy(async () => ({
  default: (await import('@/features/security/GroupAssignmentsPanel')).GroupAssignmentsPanel,
}));

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
  // ⚠ Вкладка — теж в адресі: «подивись гранти цієї ролі» без неї означає
  // «відкрий безпеку і перемкнись на другу вкладку».
  const [rawTab, setTab] = useUrlState('tab');
  const tab = rawTab ?? 'roles';
  const queryClient = useQueryClient();
  const session = useSession();

  // ⛔ Сторінка знає лише «діалог відкрито/закрито». Чернетки «New role» і
  // «New user» живуть у самих діалогах (`CreateRoleModal`, `CreateUserModal`):
  // коли вони були станом сторінки, кожен символ перерендерював матрицю
  // ролей × 41 право — медіана 97–120 мс на символ на стенді (2026-09-24).
  const [creatingRole, setCreatingRole] = useState(false);
  const [creatingUser, setCreatingUser] = useState(false);

  // Кого редагуємо: `null` — діалог закритий.
  const [editingAccess, setEditingAccess] = useState<UserView | null>(null);

  // ⚠ «Діалог доступу вже відкривали». Назад у `false` не вертається навмисно —
  // див. коментар біля `UserAccessEditor` вище.
  const [accessUsed, setAccessUsed] = useState(false);

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
    // ⚠ Причина показується як є: «немає пошти» — це те, що людина може
    // виправити, а «не вдалося» — ні.
    onError: showApiError,
  });

  const roles = useQuery({
    queryKey: ['roles'],
    queryFn: () => apiFetch<RoleView[]>('/api/v1/roles'),
  });

  // ⛔ `X-07`: перелік мовчки обрізався на 200 (`?limit=200` і жодного слова
  // про решту). Відповідь курсорна — сторінки довантажуються кнопкою під
  // таблицею, і скільки показано з усіх, видно поруч.
  //
  // ⚠ Ключ `['users', 'list']`, а не `['users']`: під `['users']` лежить
  // звичайна сторінка `UserPage` (`RegistryConstructorPage`), а тут —
  // сторінки нескінченного запиту. Інвалідація `['users']` зачіпає обидва.
  const users = useInfiniteQuery({
    queryKey: ['users', 'list'],
    queryFn: ({ pageParam }: { pageParam: string | null }) =>
      apiFetch<UserPage>(
        `/api/v1/users?limit=${String(UsersPageSize)}`
          + (pageParam === null ? '' : `&cursor=${encodeURIComponent(pageParam)}`),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (last: UserPage) => last.nextCursor ?? undefined,
    enabled: tab === 'users',
  });

  const usersPage = useMemo<UserPage | undefined>(() => {
    const pages = users.data?.pages;
    if (pages === undefined || pages.length === 0) return undefined;

    return {
      items: pages.flatMap((page) => page.items),
      nextCursor: pages[pages.length - 1]?.nextCursor ?? null,
      totalCount: pages[0]?.totalCount ?? null,
    };
  }, [users.data]);

  /**
   * ПОВНИЙ каталог прав (директива №11, T2) — а не перетин того, що вже
   * оголошено в наявних ролях.
   *
   * ⛔ До цього запиту перелік складався як
   * `[...new Set(roles.flatMap(r => r.permissions))]`: право, якого ще жодна
   * роль не отримала, не існувало для форми створення ролі взагалі —
   * призначити його вперше можна було лише прямим записом у базу.
   */
  const permissionCatalog = useQuery({
    queryKey: ['permissions'],
    queryFn: () => apiFetch<PermissionCatalogItem[]>('/api/v1/permissions'),
  });

  // ⚠ `?? []` лишається лише для «ще їде» й заглушок із `null`. ВІДМОВА сюди
  // не доходить: матриця стоїть за `AsyncBoundary` з `permissionCatalog.error`,
  // форма ролі — за `ErrorAlert`. Інакше відмова каталогу малювала матрицю без
  // жодної колонки прав, і це читалося як «у ролей немає прав».
  // ⚠ `useMemo` — щоб `RoleMatrix` (`memo`) отримував те саме посилання, доки
  // каталог не змінився.
  const permissions = useMemo(
    () => [...(permissionCatalog.data ?? [])].sort((a, b) => a.code.localeCompare(b.code)),
    [permissionCatalog.data],
  );

  const retryReferences = (): void => {
    if (roles.error !== null) void roles.refetch();
    if (permissionCatalog.error !== null) void permissionCatalog.refetch();
  };

  return (
    <>
      <PageHeader
        title={t('security.title')}
        actions={
          <Group gap="xs">
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

            {/* ⚠ Кнопка створення належить ВКЛАДЦІ, а не екрану: «створити»
                поруч із матрицею прав і поруч із переліком користувачів
                означає різне, і одна кнопка на обидві була б загадкою. */}
            {tab === 'roles' && can(session.data, 'Security.ManageRoles') && (
              <Button size="xs" onClick={() => setCreatingRole(true)}>
                {t('security.createRole')}
              </Button>
            )}

            {tab === 'users' && can(session.data, 'Security.ManageUsers') && (
              <Button size="xs" onClick={() => setCreatingUser(true)}>
                {t('security.createUser')}
              </Button>
            )}
          </Group>
        }
      />

      {tab === 'roles' && (
        <AsyncBoundary<RoleView[]>
          isPending={roles.isPending || permissionCatalog.isPending}
          // ⛔ Матриця — це ролі × права: без каталогу прав її немає, є лише
          // перелік ролей із порожніми рядками. Тому відмова БУДЬ-ЯКОГО з двох
          // запитів — відмова матриці, а не «малюємо, що приїхало».
          error={roles.error ?? permissionCatalog.error}
          data={roles.data}
          isEmpty={(all) => all.length === 0}
          emptyTitle={t('security.noRoles')}
          emptyHint={t('security.noRolesHint')}
          skeleton="table"
          onRetry={retryReferences}
        >
          {(all) => (
            <RoleMatrix
              roles={all}
              permissions={permissions}
              canManage={can(session.data, 'Security.ManageRoles')}
            />
          )}
        </AsyncBoundary>
      )}

      {/* ⛔ Гранти — окрема вкладка, а не колонка в матриці прав. Права
          відповідають на питання «що людина вміє», гранти — «до чого саме»;
          без другої відповіді перша не відкриває нічого (`A7-22`). */}
      {/* ⛔ Ролі потрібні ВСІМ вкладкам (вибір ролі в грантах, групах, доступі),
          а межа помилки вище живе лише на «Ролях». Без цього банера відмова
          `GET /roles` на інших вкладках давала порожні випадні списки мовчки. */}
      {tab !== 'roles' && <ErrorAlert error={roles.error} onRetry={retryReferences} />}

      {tab === 'grants' && (
        <Suspense fallback={null}>
          <GrantsPanel roles={roles.data ?? []} />
        </Suspense>
      )}

      {tab === 'users' && can(session.data, 'Security.ManageUsers') && (
        <Suspense fallback={null}>
          <GroupAssignmentsPanel roles={roles.data ?? []} />
        </Suspense>
      )}

      {tab === 'users' && (
        <AsyncBoundary<UserPage>
          isPending={users.isPending}
          error={users.error}
          data={usersPage}
          isEmpty={(page) => page.items.length === 0}
          emptyTitle={t('security.noUsers')}
          emptyHint={t('security.noUsersHint')}
          skeleton="table"
          onRetry={() => void users.refetch()}
        >
          {(page) => (
          <>
          <Table striped className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('security.login')}</Table.Th>
                <Table.Th>{t('security.name')}</Table.Th>
                <Table.Th>{t('security.kind')}</Table.Th>
                <Table.Th>{t('security.access')}</Table.Th>
                <Table.Th>{t('security.alerts')}</Table.Th>
                <Table.Th>{t('security.userState')}</Table.Th>
                <Table.Th>{t('security.lastSignIn')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {page.items.map((user) => (
                // ⛔ `X-06`: без `opacity` на рядку. Прозорість гасила й КНОПКИ
                // рядка — контраст тексту й дій падав нижче AA, а неактивність і
                // так позначена бейджем поруч із логіном.
                <Table.Tr key={user.id} data-inactive={user.isActive ? undefined : ''}>
                  <Table.Td>
                    {user.userName}
                    {/* ⛔ Той самий дефект, що й у таблиці ролей: `opacity={0.5}`
                        на рядку — єдиний сигнал неактивності, невидимий читалці
                        й непомітний на бляклому екрані (UX-аудит, знахідка 2/3). */}
                    {!user.isActive && (
                      <Badge ml="xs" size="xs" color="gray" variant="outline">
                        {t('security.inactive')}
                      </Badge>
                    )}
                  </Table.Td>
                  <Table.Td>{user.displayName}</Table.Td>
                  <Table.Td>
                    {/* Локальний і доменний вхід дають ту саму сесію; різниця
                        лише в тому, хто зберігає пароль. */}
                    <Badge variant="light">{user.provider}</Badge>
                  </Table.Td>
                  <Table.Td>
                    {/* ⛔ Ролі й адреса правляться ТУТ (`A7-61`, `A7-62`).
                        Способу призначити роль наявному користувачеві не
                        існувало взагалі, а адреса не присвоювалася ніде —
                        обліковий запис виходив безправним і без сповіщень. */}
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      onClick={() => {
                        setAccessUsed(true);
                        setEditingAccess(user);
                      }}
                    >
                      {t('security.access')}
                    </Button>
                  </Table.Td>
                  <Table.Td>
                    {/* ⛔ Без пошти перемикач ВИМКНЕНИЙ, а не «вмикається і
                        мовчки не працює»: увімкнений адресат, якому нічого не
                        надсилається, виглядає як налаштований (`D-125`).
                        ⚠ Причину видно ТЕКСТОМ поруч і прив'язано
                        `aria-describedby`, а не `Tooltip`: вимкнений перемикач
                        не фокусується і не отримує наведення в частині
                        браузерів, тож причина під мишею була недосяжна з
                        клавіатури взагалі. */}
                    <Group gap="xs" wrap="nowrap">
                      <Switch
                        size="xs"
                        aria-label={`${t('security.alerts')} · ${user.userName}`}
                        checked={user.receivesAlerts}
                        // ⛔ Аудит 2026-09-16 §10.8: тут стояло голе
                        // `alerts.isPending` — ОДНЕ значення однієї мутації на
                        // весь перелік, тож перемикання адресата для одного
                        // користувача гасило перемикачі ВСІХ решти. Адресати
                        // алертів — це ДАНІ (`D-125`), і їх міняють саме
                        // списком: чекати кожен запит, не розуміючи, чому поля
                        // погасли, — рівно та поведінка, від якої список
                        // перестає бути списком.
                        disabled={
                          noEmail(user) || (alerts.isPending && alerts.variables?.id === user.id)
                        }
                        aria-describedby={noEmail(user) ? alertsReasonId(user.id) : undefined}
                        onChange={(event) =>
                          alerts.mutate({ id: user.id, value: event.currentTarget.checked })
                        }
                      />
                      {noEmail(user) && (
                        <Text id={alertsReasonId(user.id)} size="xs" c="dimmed" maw={220}>
                          {t('security.alertsNeedEmail')}
                        </Text>
                      )}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    <Group gap="xs">
                      {/* ⚠ Разовий пароль і блокування видно в переліку:
                          «користувач не може увійти» найчастіше пояснюється
                          саме ними, а не правами. */}
                      {user.mustChangePassword && (
                        <Badge size="sm" color="statusWarning" variant="light">
                          {t('security.mustChangePassword')}
                        </Badge>
                      )}
                      {user.isLockedOut && (
                        <Badge size="sm" color="statusError" variant="light">
                          {t('security.lockedOut')}
                        </Badge>
                      )}
                      {user.isBootstrapAdmin && (
                        <Badge size="sm" variant="outline">
                          {t('security.bootstrap')}
                        </Badge>
                      )}

                      {/* ⛔ «Подивитися його правами» (`ФВ-6.16`). Бадж
                          симуляції в шапці малювався від Етапу 7, а
                          ввімкнути її не було чим: система вміла показати
                          стан, у який не могла увійти (`A7-39`).

                          ⚠ Себе симулювати не можна — сервер відмовить, і
                          кнопки тут немає навмисно. */}
                      {can(session.data, 'Security.Simulate') &&
                        user.id !== session.data?.userId && (
                          <StartSimulationButton userId={user.id} />
                        )}

                      {/* Блокування й скидання пароля (`BE-12`); право і «не себе» — всередині. */}
                      <UserAdminActions user={user} />
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    {/* ⛔ `null` — «жодного разу не входив», а не «дані ще не
                        приїхали» (`BE-12`): дефолтне тире `Timestamp`
                        перекрито тим самим ключем каталогу, що вже несе це
                        значення для `CollectionScheduleTab.lastRunAt`. */}
                    <Timestamp value={user.lastSignInAt} fallback={t('sources.never')} />
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>

          {/* ⛔ `X-07`: скільки показано — і дія, щоб побачити решту. */}
          {page.nextCursor !== null && (
            <Group gap="sm" mt="md" data-users-more="">
              <Text size="sm" c="dimmed">
                {page.totalCount === null || page.totalCount === undefined
                  ? t('common.shownSoFar', { shown: page.items.length })
                  : t('common.shownOf', { shown: page.items.length, total: page.totalCount })}
              </Text>
              <Button
                size="xs"
                variant="default"
                loading={users.isFetchingNextPage}
                onClick={() => void users.fetchNextPage()}
              >
                {t('documents.more')}
              </Button>
            </Group>
          )}
          </>
          )}
        </AsyncBoundary>
      )}

      <CreateRoleModal
        opened={creatingRole}
        onClose={() => setCreatingRole(false)}
        permissions={permissions}
        permissionCatalogError={permissionCatalog.error}
        onRetry={retryReferences}
      />

      {accessUsed && (
        <Suspense fallback={null}>
          <UserAccessEditor
            user={editingAccess}
            roles={roles.data ?? []}
            onClose={() => setEditingAccess(null)}
          />
        </Suspense>
      )}

      <CreateUserModal
        opened={creatingUser}
        onClose={() => setCreatingUser(false)}
        roles={roles.data ?? []}
        rolesError={roles.error}
        onRetry={retryReferences}
      />
    </>
  );
}
