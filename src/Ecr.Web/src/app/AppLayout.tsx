import {
  Suspense,
  useEffect,
  useState,
  type CSSProperties,
  type JSX,
  type MouseEvent,
} from 'react';
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

/**
 * Ідентифікатор основного вмісту — ціль для «Пропустити навігацію» нижче.
 *
 * ⚠ `tabIndex={-1}` на самому `<AppShell.Main>` (не лише `id`): `<main>` не
 * фокусується від природи, і без цього активація посилання переносить курсор
 * лише у прокрутку/URL (`#main-content`), а фокус лишається на самому
 * посиланні — читалка й далі оголошувала б навігацію.
 */
const MainContentId = 'main-content';

/**
 * «Пропустити навігацію» (`WCAG 2.4.1 Bypass Blocks`, Q-263).
 *
 * ⛔ Не рятує лише те, що `axe`-правило `bypass` не входило в `runOnly`
 * (`src/test/a11y.ts`) — воно й не бачило проблему, тому що виправлення тоді
 * не існувало: сам гейт правило не пише, лише перевіряє. Причина дефекту —
 * `AppShell.Navbar` рендерить до 15 пунктів навігації в DOM РАНІШЕ за
 * `AppShell.Main`, і клавіатурним користувачем без читалки (моторні
 * порушення, `Tab` — єдиний спосіб пересуватись) немає як це оминути:
 * читалка вже проходить повз нав через орієнтир `<main>` (`D`/`R` у
 * NVDA/JAWS), а `Tab` — ні.
 *
 * ⚠ Прихована технікою `clip`/`position: absolute` (той самий підхід, що й
 * `@mantine/core` `VisuallyHidden` — див. `VisuallyHidden.css` пакета), а не
 * `display: none`: остання забирає елемент із дерева фокусу цілком, і
 * посилання взагалі не отримало б фокус клавіатурою. Видиме лише в фокусі —
 * інакше воно виглядало б як зайвий текст перед шапкою для тих, хто його не
 * потребує.
 *
 * ⛔ Текст — не через `t()`. Каталог рядків живе в
 * `Ecr.Infrastructure/Persistence/Sql/09-seed.sql`, а ця картка (Q-263,
 * директива Хвилі 3) навмисно обмежена двома файлами
 * (`AppLayout.tsx`, `test/a11y.ts`) саме для паралельної ізоляції ліній —
 * файл сідів чіпають одразу кілька ліній, і зайва правка тут була б зайвим
 * ризиком конфлікту поза межами картки. Судження зафіксоване тут одним
 * рядком (`CLAUDE.md`): англійський літерал лишається доти, доки окрема
 * картка не заведе ключ у каталозі.
 */
function SkipToContentLink(): JSX.Element {
  const [isFocused, setIsFocused] = useState(false);

  // Та сама техніка приховування, що й `@mantine/core` `VisuallyHidden`
  // (стиль пакета `styles/VisuallyHidden.css`) — лише додано видимий стан
  // на фокус, якого в самому `VisuallyHidden` немає.
  const hiddenStyle: CSSProperties = {
    border: 0,
    clip: 'rect(0 0 0 0)',
    height: 1,
    width: 1,
    margin: -1,
    overflow: 'hidden',
    padding: 0,
    position: 'absolute',
    whiteSpace: 'nowrap',
  };

  const visibleStyle: CSSProperties = {
    position: 'fixed',
    top: 8,
    left: 8,
    zIndex: 1000,
    padding: '8px 16px',
    background: 'var(--mantine-color-body)',
    color: 'var(--mantine-color-text)',
    border: '2px solid var(--mantine-color-brand-6)',
    borderRadius: 4,
    textDecoration: 'none',
    clip: 'auto',
    height: 'auto',
    width: 'auto',
    margin: 0,
    overflow: 'visible',
    whiteSpace: 'normal',
  };

  // ⚠ Клік/`Enter` переносить фокус ЯВНО (`main.focus()`), а не покладається
  // лише на природну навігацію браузера за фрагментом URL: остання рухає
  // фокус у ціль надійно не в кожному браузері (і не в jsdom, де побудований
  // мутаційний тест), а без фокуса на `<main>` посилання саме й не виконує
  // свою обіцянку — стрічка адреси зміниться, курсор читання лишиться на
  // місці.
  const handleActivate = (event: MouseEvent<HTMLAnchorElement>): void => {
    const main = document.getElementById(MainContentId);
    if (main === null) return;

    event.preventDefault();
    main.focus();
    window.location.hash = MainContentId;
  };

  return (
    <a
      href={`#${MainContentId}`}
      onClick={handleActivate}
      onFocus={() => setIsFocused(true)}
      onBlur={() => setIsFocused(false)}
      style={isFocused ? visibleStyle : hiddenStyle}
    >
      Skip to main content
    </a>
  );
}

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
  // ⛔ Було `Period.Manage` — права з такою назвою немає в каталозі
  // (`sec.Permission`); seed заводить лише `Period.Configure` і
  // `Period.Reopen`. Пункт меню не з'являвся НІКОМУ, включно з
  // `PeriodAdministrator`, чий шаблон `Period.%` розгортається проти
  // каталогу й теж не знаходив там нічого (директива №09 `S-02`).
  // `Period.Configure` — базове право сторінки (`PeriodsPage.tsx:187`);
  // саме `Reopen`-кнопка всередині додатково перевіряє `Period.Reopen`.
  { path: '/admin/periods', labelKey: 'nav.periods', permission: 'Period.Configure' },
  { path: '/admin/sources', labelKey: 'nav.sources', permission: 'Integration.Manage' },
  { path: '/admin/mapping', labelKey: 'nav.mapping', permission: 'Integration.Manage' },
  { path: '/admin/jobs', labelKey: 'nav.jobs', permission: 'System.ViewHealth' },
  { path: '/admin/snapshots', labelKey: 'nav.snapshots', permission: 'Report.ViewRegulatory' },
  { path: '/admin/audit', labelKey: 'nav.audit', permission: 'Security.ViewAudit' },
  {
    path: '/admin/ui-strings',
    labelKey: 'nav.uiStrings',
    permission: 'System.ManageLocalization',
  },
  { path: '/admin/health', labelKey: 'nav.health', permission: 'System.ViewHealth' },

  // ⛔ БЕЗ права — і це не пропуск. Пункт відповідає на «чому в мене порожні
  // екрани», тобто потрібен саме тому, у кого прав немає (`H-21`). Закрити
  // його правом означало б показувати відповідь лише тим, хто й так знає.
  { path: '/my-groups', labelKey: 'nav.myGroups' },
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
      {/*
       * ПЕРШИЙ фокусований елемент на сторінці (Q-263) — раніше за Burger,
       * бейдж симуляції й пункти навігації нижче. Порядок у розмітці тут —
       * це і є порядок `Tab`, тож переставляти цей блок нижче за
       * `AppShell.Header`/`AppShell.Navbar` означало б повернути дефект.
       */}
      <SkipToContentLink />

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
                <Badge color="statusWarning" variant="filled">
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

      <AppShell.Main id={MainContentId} tabIndex={-1}>
        {/*
         * `tabIndex={-1}` існує ЛИШЕ заради «Пропустити навігацію» вище
         * (Q-263): `<main>` сам по собі не фокусується, і без цього
         * активація посилання переносила б лише скрол/URL-хеш, а фокус
         * лишався б на самому посиланні.
         */}
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
