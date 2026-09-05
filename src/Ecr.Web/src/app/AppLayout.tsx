import { useEffect, type JSX } from 'react';
import { AppShell, Badge, Burger, Group, NavLink, ScrollArea, Text } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { Link, Navigate, Outlet, useLocation } from 'react-router-dom';
import { can, useSession } from '@/shared/session/useSession';
import { language, loadCatalog, t } from '@/shared/i18n';

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
  { path: '/admin/security', labelKey: 'nav.security', permission: 'Security.ManageRoles' },
  { path: '/admin/periods', labelKey: 'nav.periods', permission: 'Period.Manage' },
  { path: '/admin/sources', labelKey: 'nav.sources', permission: 'Integration.Manage' },
  { path: '/admin/jobs', labelKey: 'nav.jobs', permission: 'System.ViewHealth' },
  { path: '/admin/health', labelKey: 'nav.health', permission: 'System.ViewHealth' },
];

/** Каркас застосунку: навігація, профіль, вміст сторінки. */
export function AppLayout(): JSX.Element {
  const [opened, { toggle }] = useDisclosure();
  const session = useSession();
  const location = useLocation();

  const me = session.data;

  // Приватний каталог рядків тягнеться після входу і мовою профілю (D-114).
  useEffect(() => {
    if (me === undefined) return;

    void loadCatalog(me.language.length > 0 ? me.language : language(), 'private');
  }, [me]);

  if (session.isPending) return <Text p="md">{t('app.loading')}</Text>;

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
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Group gap="sm">
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" />
            <Text fw={700}>ECR</Text>
          </Group>

          <Group gap="xs">
            {/* ⚠ Сеанс симуляції видно ЗАВЖДИ і помітно: адміністратор, який
                забув, що дивиться чужими правами, ухвалює рішення про чужий
                доступ, дивлячись не на свої можливості (ФВ-6.16a). */}
            {me.simulation !== null && (
              <Badge color="orange" variant="filled">
                {t('app.simulating', { name: me.simulation.targetName })}
              </Badge>
            )}
            <Text size="sm">{me.displayName}</Text>
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
        <Outlet />
      </AppShell.Main>
    </AppShell>
  );
}
