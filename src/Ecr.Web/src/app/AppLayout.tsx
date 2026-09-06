import { Suspense, useEffect, type JSX } from 'react';
import {
  AppShell,
  Badge,
  Burger,
  Center,
  Group,
  Loader,
  NavLink,
  ScrollArea,
  Skeleton,
  Stack,
  Text,
} from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { Link, Navigate, Outlet, useLocation } from 'react-router-dom';
import { EndSimulationButton } from '@/features/security/SimulationPanel';
import { can, useSession } from '@/shared/session/useSession';
import { isCatalogResolved, language, loadCatalog, t } from '@/shared/i18n';
import { useCatalog } from '@/shared/i18n/useCatalog';
import { RouteAnnouncer } from '@/shared/ui/RouteAnnouncer';
import { UserMenu } from '@/shared/ui/UserMenu';

/** Пункт навігації разом із правом, яке його відкриває. */
interface NavItem {
  path: string;
  labelKey: string;
  permission?: string;
}

/**
 * Навігація.
 *
 * ⛔ Розділу «Звіти» тут немає і не буде: звітність лишається в SSRS (D-52).
 * Пункт меню, що веде в порожнечу, — це обіцянка, якої система не виконує.
 */
const Items: NavItem[] = [
  { path: '/', labelKey: 'nav.documents' },
  { path: '/admin/templates', labelKey: 'nav.templates', permission: 'Template.Edit' },
  { path: '/admin/registries', labelKey: 'nav.registries', permission: 'Registry.View' },
  { path: '/admin/methodologies', labelKey: 'nav.methodologies', permission: 'Calculation.View' },
  { path: '/admin/expressions', labelKey: 'nav.expressions', permission: 'Calculation.View' },
  { path: '/admin/units', labelKey: 'nav.units', permission: 'Calculation.View' },
  { path: '/admin/security', labelKey: 'nav.security', permission: 'Security.ManageRoles' },
  { path: '/admin/periods', labelKey: 'nav.periods', permission: 'Period.Manage' },
  { path: '/admin/sources', labelKey: 'nav.sources', permission: 'Integration.Manage' },
  { path: '/admin/jobs', labelKey: 'nav.jobs', permission: 'System.ViewHealth' },
  { path: '/admin/snapshots', labelKey: 'nav.snapshots', permission: 'Report.ViewRegulatory' },
  { path: '/admin/audit', labelKey: 'nav.audit', permission: 'Security.ViewAudit' },
  {
    path: '/admin/ui-strings',
    labelKey: 'nav.uiStrings',
    permission: 'System.ManageLocalization',
  },
  { path: '/admin/health', labelKey: 'nav.health', permission: 'System.ViewHealth' },
];

/** Каркас застосунку: навігація, профіль, вміст сторінки. */
export function AppLayout(): JSX.Element {
  const [opened, { toggle }] = useDisclosure();
  const session = useSession();
  const location = useLocation();

  // Перемальовує каркас і сторінку, коли приватний каталог доїхав.
  useCatalog();

  const me = session.data;

  // Приватний каталог рядків тягнеться після входу і мовою профілю (D-114).
  useEffect(() => {
    if (me === undefined) return;

    void loadCatalog(me.language.length > 0 ? me.language : language(), 'private');
  }, [me]);

  // ⛔ Ані профіль, ані приватний каталог іще не приїхали — тексту немає.
  // `t('app.loading')` тут показав би `⟦app.loading⟧`: цей ключ живе в
  // ПУБЛІЧНІЙ області, якої на цьому шляху ніхто не вантажив (`D-138`).
  const catalogReady =
    me === undefined ||
    isCatalogResolved(me.language.length > 0 ? me.language : language(), 'private');

  if (session.isPending || !catalogReady) {
    return (
      <Center p="md">
        <Loader size="sm" />
      </Center>
    );
  }

  if (session.isError || me === undefined) {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }

  // ⛔ Разовий пароль закриває все, крім його зміни (ФВ-6.18): інакше
  // користувач працював би з тимчасовим паролем, який знає той, хто його видав.
  if (me.mustChangePassword && location.pathname !== '/change-password') {
    return <Navigate to="/change-password" replace />;
  }

  return (
    <AppShell
      header={{ height: 56 }}
      navbar={{ width: 260, breakpoint: 'sm', collapsed: { mobile: !opened } }}
      padding="md"
    >
      {/* Одна область оголошень на весь застосунок (ФВ-14.19). */}
      <RouteAnnouncer />

      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Group gap="sm">
            <Burger
              opened={opened}
              onClick={toggle}
              hiddenFrom="sm"
              size="sm"
              aria-label={t('nav.menu')}
            />
            <Text fw={700}>ECR</Text>
          </Group>

          <Group gap="xs">
            {/* ⚠ Сеанс симуляції видно ЗАВЖДИ і помітно: адміністратор, який
                забув, що дивиться чужими правами, ухвалює рішення про чужий
                доступ, дивлячись не на свої можливості (ФВ-6.16a). */}
            {me.isSimulation && (
              <>
                <Badge color="orange" variant="filled">
                  {t('app.simulating', { user: me.simulatedForUserId ?? '—' })}
                </Badge>

                {/* ⛔ Вихід стоїть ПОРУЧ із баджем. Саме тут користувач
                    помічає, що дивиться чужими правами, і саме тут має
                    бути вихід: інакше єдиним способом завершити сеанс
                    лишався б вихід із системи. */}
                <EndSimulationButton />
              </>
            )}
            <UserMenu userName={me.userName ?? '—'} />
          </Group>
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="xs">
        <ScrollArea>
          {Items.filter((item) => item.permission === undefined || can(me, item.permission)).map(
            (item) => (
              // ⚠ Пункт показується, лише якщо право є: користувач не має
              // тиснути те, що все одно дасть 403.
              <NavLink
                key={item.path}
                component={Link}
                to={item.path}
                label={t(item.labelKey)}
                active={location.pathname === item.path}
              />
            ),
          )}
        </ScrollArea>
      </AppShell.Navbar>

      <AppShell.Main>
        {/*
         * ⚠ Власна межа очікування, а не запасна. Без неї застосунок НЕ
         * падає — `RouterProvider` має власну, — але її запасним вмістом є
         * порожнеча: при завантаженні чанка маршруту область змісту просто
         * зникає. Перевірено прибиранням цієї межі: сторінка рендериться, і
         * саме тому дефект такого роду не помітили б у тесті.
         *
         * ⚠ Межа стоїть НАВКОЛО `<Outlet/>`, а не навколо всього застосунку:
         * інакше кожен перехід гасив би шапку й навігацію разом зі змістом, і
         * екран блимав би цілком там, де змінюється сама лише середина.
         */}
        <Suspense fallback={<RouteFallback />}>
          <Outlet />
        </Suspense>
      </AppShell.Main>
    </AppShell>
  );
}

/**
 * Заглушка на час завантаження чанка маршруту.
 *
 * ⚠ Скелет, а не спінер (`ФВ-14.25`): майже кожен екран системи — це заголовок
 * і таблиця під ним, і показати саме цю форму чесніше, ніж крутити коло.
 */
function RouteFallback(): JSX.Element {
  return (
    <Stack gap="xs" aria-busy="true">
      <Skeleton height={28} width="30%" radius="sm" />
      <Skeleton height={24} radius="sm" />
      <Skeleton height={24} radius="sm" />
      <Skeleton height={24} radius="sm" />
    </Stack>
  );
}
