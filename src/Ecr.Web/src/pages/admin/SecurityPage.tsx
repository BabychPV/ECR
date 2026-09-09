import { useState, type JSX } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  Group,
  Modal,
  MultiSelect,
  PasswordInput,
  ScrollArea,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Tooltip,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { createUserBody } from '@/features/security/createUserBody';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';
import type {
  CreateRoleRequest,
  PermissionCatalogItem,
  RoleIdResponse,
  RoleView,
  SetAlertsRequest,
  UserIdResponse,
  UserPage,
  UserView,
} from '@/api/types';
import { StartSimulationButton } from '@/features/security/SimulationPanel';
import { GrantsPanel } from '@/pages/admin/GrantsPanel';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { PageHeader } from '@/shared/ui/PageHeader';
import { showApiError, showDone } from '@/shared/ui/notify';
import { useUrlState } from '@/shared/ui/useUrlState';
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
  // ⚠ Вкладка — теж в адресі: «подивись гранти цієї ролі» без неї означає
  // «відкрий безпеку і перемкнись на другу вкладку».
  const [rawTab, setTab] = useUrlState('tab');
  const tab = rawTab ?? 'roles';
  const queryClient = useQueryClient();
  const session = useSession();

  const [creatingRole, setCreatingRole] = useState(false);
  const [roleCode, setRoleCode] = useState('');
  const [roleName, setRoleName] = useState<LocalizedValue>({});
  const [rolePermissions, setRolePermissions] = useState<string[]>([]);

  const [creatingUser, setCreatingUser] = useState(false);
  const [userName, setUserName] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [provider, setProvider] = useState('Windows');
  const [sid, setSid] = useState('');

  // ⛔ Разовий пароль ВВОДИТЬ адміністратор (`A7-60`). Сервер його не
  // генерує і не має генерувати: повернути пароль у відповіді API прямо
  // заборонено (`D-11`, `ФВ-6.11`), а надіслати листом нікуди — адреси в
  // щойно створеного запису ще немає. Ризик — адміністратор знає пароль —
  // знімається обов'язковою зміною при першому вході (`ФВ-6.18`).
  const [oneTimePassword, setOneTimePassword] = useState('');
  const [email, setEmail] = useState('');
  const [newUserRoles, setNewUserRoles] = useState<string[]>([]);

  // Кого редагуємо: `null` — діалог закритий.
  const [editingAccess, setEditingAccess] = useState<UserView | null>(null);

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

  /**
   * Створення ролі (`ФВ-6.3`).
   *
   * ⛔ Дії не було в інтерфейсі: сторож вважав `POST /roles` досяжним, бо
   * клієнт читає `GET /roles` тією самою адресою (`A7-42`). Тобто матриця
   * прав показувала ролі й не давала завести жодної нової — а рольова
   * модель без цього зводиться до вбудованих ролей назавжди.
   *
   * ⚠ Права обираються з тих, що вже оголошені: вигадати право на клієнті
   * не можна, сервер приймає лише коди з каталогу.
   */
  const createRole = useMutation({
    mutationFn: () =>
      apiFetch<RoleIdResponse>('/api/v1/roles', {
        method: 'POST',
        body: JSON.stringify({
          code: roleCode.trim(),
          nameL10n: roleName,
          permissionCodes: rolePermissions,
        } satisfies CreateRoleRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['roles'] });
      setCreatingRole(false);
      setRoleCode('');
      setRoleName({});
      setRolePermissions([]);
      showDone(t('security.roleCreated'));
    },
    onError: showApiError,
  });

  /**
   * Заведення користувача.
   *
   * ⛔ Так само недосяжна дія (`A7-42`). Доменні користувачі з'являються
   * після першого входу самі, а локальні — лише тут; без цього екрана
   * локального облікового запису не існувало б узагалі.
   *
   * ⛔ Пароль тут НЕ вводиться. Локальний обліковий запис створюється
   * сервером із разовим паролем і прапорцем `MustChangePassword`
   * (`ФВ-6.18`): пароль, який знає той, хто його видав, — це не пароль.
   */
  const createUser = useMutation({
    mutationFn: () =>
      apiFetch<UserIdResponse>('/api/v1/users', {
        method: 'POST',
        // ⛔ Склад тіла — окремою чистою функцією (`createUserBody`). Саме
        // тут жили `A7-60`, `A7-61` і `A7-62`, і перевірити їх рендером
        // Mantine у jsdom неможливо за прийнятний час.
        body: JSON.stringify(
          createUserBody({
            userName,
            displayName,
            provider,
            sid,
            oneTimePassword,
            email,
            roleCodes: newUserRoles,
          }),
        ),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['users'] });
      setCreatingUser(false);
      setUserName('');
      setDisplayName('');
      setSid('');
      setOneTimePassword('');
      setEmail('');
      setNewUserRoles([]);
      showDone(t('security.userCreated'));
    },
    onError: showApiError,
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

  const permissions = [...(permissionCatalog.data ?? [])].sort((a, b) =>
    a.code.localeCompare(b.code),
  );

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
          isPending={roles.isPending}
          error={roles.error}
          data={roles.data}
          isEmpty={(all) => all.length === 0}
          emptyTitle={t('security.noRoles')}
          emptyHint={t('security.noRolesHint')}
          skeleton="table"
          onRetry={() => void roles.refetch()}
        >
          {(all) => (
          <Table striped withTableBorder className="ecr-sticky-head ecr-sticky-first" style={{ overflowX: 'auto' }}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('security.role')}</Table.Th>
                {permissions.map((permission) => (
                  <Table.Th key={permission.code}>
                    <Text size="xs">{permission.code}</Text>
                  </Table.Th>
                ))}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {all.map((role) => (
                <Table.Tr key={role.id} opacity={role.isActive ? 1 : 0.5}>
                  <Table.Td>
                    {role.code}
                    {role.isBuiltIn && (
                      <Badge ml="xs" size="xs" variant="light">
                        {t('security.builtIn')}
                      </Badge>
                    )}

                    {/* ⚠ Небезпечні права показуються ОКРЕМО: у складені ролі
                        вони не входять навмисно, і адміністратор має бачити
                        різницю (ФВ-6.12, D-40). */}
                    {role.dangerousPermissions.length > 0 && (
                      <Badge ml="xs" size="xs" color="statusError" variant="light">
                        {t('security.dangerous', { count: role.dangerousPermissions.length })}
                      </Badge>
                    )}
                  </Table.Td>
                  {permissions.map((permission) => (
                    <Table.Td key={permission.code}>
                      {role.permissions.includes(permission.code) ? '✓' : ''}
                    </Table.Td>
                  ))}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
          )}
        </AsyncBoundary>
      )}

      {/* ⛔ Гранти — окрема вкладка, а не колонка в матриці прав. Права
          відповідають на питання «що людина вміє», гранти — «до чого саме»;
          без другої відповіді перша не відкриває нічого (`A7-22`). */}
      {tab === 'grants' && <GrantsPanel roles={roles.data ?? []} />}

      {tab === 'users' && (
        <AsyncBoundary<UserPage>
          isPending={users.isPending}
          error={users.error}
          data={users.data}
          isEmpty={(page) => page.items.length === 0}
          emptyTitle={t('security.noUsers')}
          emptyHint={t('security.noUsersHint')}
          skeleton="table"
          onRetry={() => void users.refetch()}
        >
          {(page) => (
          <Table striped className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('security.login')}</Table.Th>
                <Table.Th>{t('security.name')}</Table.Th>
                <Table.Th>{t('security.kind')}</Table.Th>
                <Table.Th>{t('security.userState')}</Table.Th>
                <Table.Th>{t('security.alerts')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {page.items.map((user) => (
                <Table.Tr key={user.id} opacity={user.isActive ? 1 : 0.5}>
                  <Table.Td>{user.userName}</Table.Td>
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
                      onClick={() => setEditingAccess(user)}
                    >
                      {t('security.access')}
                    </Button>
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
                        aria-label={`${t('security.alerts')} · ${user.userName}`}
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
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
          )}
        </AsyncBoundary>
      )}

      <Modal
        opened={creatingRole}
        onClose={() => setCreatingRole(false)}
        title={t('security.createRole')}
        size="lg"
      >
        <TextInput
          label={t('security.roleCode')}
          description={t('security.roleCodeHint')}
          value={roleCode}
          onChange={(event) => setRoleCode(event.currentTarget.value)}
          data-autofocus
        />

        <LocalizedInput label={t('security.roleName')} value={roleName} onChange={setRoleName} />

        <Text size="sm" mt="sm" fw={600}>
          {t('security.permissions')}
        </Text>
        <Text size="xs" c="dimmed" mb="xs">
          {t('security.permissionsHint')}
        </Text>

        {/* ⚠ Перелік — це ПОВНИЙ каталог (директива №11, T2), а не перетин
            того, що вже оголошено в наявних ролях: право без жодного носія
            інакше не можна було б призначити НІКОМУ. Вигадати право на
            клієнті все одно не можна — сервер приймає лише коди з каталогу,
            і показувати поле вільного вводу означало б обіцяти те, що
            завершиться відмовою. */}
        <ScrollArea h={220}>
          <Stack gap="xs">
            {permissions.map((permission) => (
              <Checkbox
                key={permission.code}
                label={
                  <Group gap="xs" wrap="nowrap">
                    <Text size="sm">{permission.code}</Text>
                    {/* ⚠ Небезпечні позначені ОКРЕМО (ФВ-6.12, D-40): їх
                        видають поіменно, і адміністратор має бачити, яке саме
                        право це таке, ще до того, як позначить прапорець. */}
                    {permission.isDangerous && (
                      <Badge size="xs" color="statusError" variant="light">
                        {t('security.dangerous', { count: 1 })}
                      </Badge>
                    )}
                  </Group>
                }
                checked={rolePermissions.includes(permission.code)}
                onChange={(event) =>
                  setRolePermissions((current) =>
                    event.currentTarget.checked
                      ? [...current, permission.code]
                      : current.filter((code) => code !== permission.code),
                  )
                }
              />
            ))}
          </Stack>
        </ScrollArea>

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setCreatingRole(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={roleCode.trim().length === 0 || !hasAnyText(roleName)}
            loading={createRole.isPending}
            onClick={() => createRole.mutate()}
          >
            {t('common.save')}
          </Button>
        </Group>
      </Modal>

      <UserAccessEditor
        user={editingAccess}
        roles={roles.data ?? []}
        onClose={() => setEditingAccess(null)}
      />

      <Modal
        opened={creatingUser}
        onClose={() => setCreatingUser(false)}
        title={t('security.createUser')}
      >
        <Select
          label={t('security.kind')}
          description={t('security.kindHint')}
          data={['Windows', 'Local']}
          value={provider}
          onChange={(value) => setProvider(value ?? 'Windows')}
          allowDeselect={false}
        />

        <TextInput
          mt="sm"
          label={t('security.login')}
          value={userName}
          onChange={(event) => setUserName(event.currentTarget.value)}
          data-autofocus
        />

        <TextInput
          mt="sm"
          label={t('security.name')}
          value={displayName}
          onChange={(event) => setDisplayName(event.currentTarget.value)}
        />

        {provider === 'Windows' && (
          <TextInput
            mt="sm"
            label={t('security.sid')}
            description={t('security.sidHint')}
            value={sid}
            onChange={(event) => setSid(event.currentTarget.value)}
          />
        )}

        {/* ⛔ Разовий пароль вводить адміністратор. Сервер його НЕ генерує:
            повернути пароль у відповіді API заборонено (`D-11`, `ФВ-6.11`), а
            надіслати листом нікуди — адреси ще немає. Обов'язкова зміна при
            першому вході (`ФВ-6.18`) робить його справді разовим. */}
        {provider === 'Local' && (
          <>
            <PasswordInput
              mt="sm"
              label={t('security.oneTimePassword')}
              description={t('security.oneTimePasswordHint')}
              value={oneTimePassword}
              onChange={(event) => setOneTimePassword(event.currentTarget.value)}
            />
            <Text size="xs" c="dimmed" mt="xs">
              {t('security.localHint')}
            </Text>
          </>
        )}

        {/* ⛔ Адреса: без неї сповіщення не надходять нікому (`ФВ-12`). */}
        <TextInput
          mt="sm"
          label={t('security.email')}
          description={t('security.emailHint')}
          value={email}
          onChange={(event) => setEmail(event.currentTarget.value)}
        />

        {/* ⛔ Ролі задаються ОДРАЗУ. Обліковий запис без жодної ролі
            виглядає працездатним і не може нічого. */}
        <MultiSelect
          mt="sm"
          label={t('security.roles')}
          description={t('security.rolesHint')}
          data={(roles.data ?? []).map((r) => r.code)}
          value={newUserRoles}
          onChange={setNewUserRoles}
          searchable
        />

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setCreatingUser(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={
              userName.trim().length === 0
              || (provider === 'Local' && oneTimePassword.length === 0)
            }
            loading={createUser.isPending}
            onClick={() => createUser.mutate()}
          >
            {t('common.save')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}
