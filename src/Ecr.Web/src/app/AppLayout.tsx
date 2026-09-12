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
  ScrollArea,
  Skeleton,
  SimpleGrid,
  Stack,
  Text,
} from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { Navigate, Outlet, ScrollRestoration, useLocation, useMatches } from 'react-router-dom';
import { Breadcrumbs, isRouteHandle } from './Breadcrumbs';
import { NavRouteLink } from './NavRouteLink';
import { navRoutes, type RouteHandle } from './routes';
import { routeTransitionClassName } from './motionTokens';
import { useRouteTransitionFocus } from './useRouteTransitionFocus';
import { EndSimulationButton } from '@/features/security/SimulationPanel';
import { can, useSession } from '@/shared/session/useSession';
import { isCatalogResolved, language, loadCatalog, t } from '@/shared/i18n';
import { useCatalog } from '@/shared/i18n/useCatalog';
import { RouteAnnouncer } from '@/shared/ui/RouteAnnouncer';
import { UserMenu } from '@/shared/ui/UserMenu';

import './routeTransition.css';

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

/**
 * Навігація.
 *
 * ⛔ Розділу «Звіти» тут немає і не буде: звітність лишається в SSRS (D-52).
 * Пункт меню, що веде в порожнечу, — це обіцянка, якої система не виконує.
 *
 * ⛔ Перелік пунктів і їхній порядок раніше жили тут власним масивом
 * (`Items`), окремо від шляхів у `router.tsx` — той самий шлях
 * (`/admin/templates`) набирався рядковим літералом у ДВОХ місцях, і нічого
 * не заважало їм розійтися. `navRoutes` (`./routes`, `PR nav-arch #1`) —
 * тепер ЄДИНИЙ реєстр: `router.tsx` бере звідти `path`/`handle` для
 * `createBrowserRouter`, навбар нижче — той самий реєстр, відфільтрований за
 * `showInNav`. Порядок пунктів — порядок оголошення в реєстрі.
 */

/** Каркас застосунку: навігація, профіль, вміст сторінки. */
export function AppLayout(): JSX.Element {
  const [opened, { toggle }] = useDisclosure();
  const session = useSession();
  const location = useLocation();

  /*
   * Побічні ефекти зміни маршруту (`PR nav-arch #7`): `document.title`,
   * резервний фокус на `<main>`, CSS-перехід контейнера `<Outlet/>`.
   *
   * ⛔ Викликається БЕЗУМОВНО, до обох ранніх `return` нижче (правила
   * хуків) — навіть коли сесія ще вантажиться чи помилка (нижче), сам гак
   * не звертається до `me`/`session.data`, лише до `useMatches()`/
   * `useLocation()`/`useQueryClient()`, тож викликати його для «порожнього»
   * проміжного рендера безпечно.
   */
  const transitionRef = useRouteTransitionFocus(MainContentId);

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
    <>
      {/*
       * Відновлення позиції скролу (`PR nav-arch #2`).
       *
       * ⚠ Один екземпляр на весь застосунок, на КОРЕНЕВОМУ layout-маршруті
       * (`AppLayout` — елемент `path: '/'` у `router.tsx`), а не на кожному
       * вкладеному рівні: `ScrollRestoration` сам стежить за ВСІМА змінами
       * локації через контекст роутера, і другий екземпляр глибше в дереві
       * (`AdminLayout`, `TemplateVersionLayout`) не додав би нової поведінки
       * — лише повторно підписався б на той самий стан. `RouterProvider`
       * (`App.tsx`) сам по собі НЕ layout-маршрут (не рендерить `Outlet`),
       * тому компонент не може стояти там: йому потрібен контекст усередині
       * дерева маршрутів, а `AppLayout` — єдиний батько, що завжди
       * змонтований для будь-якого автентифікованого маршруту застосунку
       * (`/login` — виняток, поза цим деревом, і не має довгих списків для
       * відновлення).
       *
       * ⛔ Не всередині `<Suspense>` навколо `<Outlet/>` нижче: інакше під
       * час підвантаження чанка нового маршруту компонент на мить
       * розмонтовувався б разом із дочірнім деревом і саме в цю мить
       * пропускав би подію зміни локації, яку мав відновити.
       */}
      <ScrollRestoration />

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
            {navRoutes
              .filter((route) => route.handle.permission === undefined || can(me, route.handle.permission))
              .map((route) => (
                // тиснути те, що все одно дасть 403. Той самий фільтр
                // одночасно захищає прогрів за наміром (`PR nav-arch #5`):
                // пункту без права тут просто НЕМА в дереві, тож немає й
                // елемента, на який можна навести курсор/фокус, — прогрів
                // для нього фізично не може спрацювати. Іконка (`PR
                // nav-icons`, `handle.icon`/`navIcons.tsx`) — тепер
                // відповідальність самого `NavRouteLink`, не цього рендера:
                // компонент, що керує `leftSection`, і компонент, що
                // прикріплює обробники наміру, — один і той самий елемент
                // `NavLink`, тож два окремих місця виклику розійшлися б.
                <NavRouteLink
                  key={route.path}
                  route={route}
                  label={t(route.handle.labelKey)}
                  active={location.pathname === route.path}
                />
              ))}
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
          {/*
           * Breadcrumbs (`PR nav-arch #3`) — ОДИН екземпляр, тут, а не в
           * `AdminLayout`/`TemplateVersionLayout`: `useMatches()` усередині
           * компонента сам читає ПОВНЕ дерево збігів поточної адреси (той
           * самий аргумент, що й для `<ScrollRestoration/>` вище, `PR #2`).
           * ПОЗА `<Suspense>` навколо `<Outlet/>` навмисно: інакше на кожному
           * підвантаженні чанка нового маршруту крихти зникали б і з'являлися
           * знову разом із дочірнім деревом, хоча дані для їхнього резолву
           * (кеш TanStack Query) нікуди не зникають.
           */}
          <Breadcrumbs />

          {/*
           * Контейнер переходу (`PR nav-arch #7`, директива B6/D) — ВСЕРЕДИНІ
           * межі очікування вище нема сенсу: анімується лише зміна МАРШРУТУ
           * (`useRouteTransitionFocus`, ключ — `location.pathname`), а
           * `Breadcrumbs`/`ScrollRestoration` навмисно лишаються поза цим
           * div (той самий аргумент, що й для меж очікування вище — інакше
           * кожен перехід чіпав би те, що не повинен).
           *
           * ⚠ Клас-«вмикач» (`ecr-route-transition--enter`) додається й
           * знімається САМИМ гаком через `ref`, не пропом: React не
           * перемальовує розмітку заради самої лише анімації, а
           * `classList`/`style.setProperty` тут — навмисний вихід за межі
           * React, той самий клас прийомів, що й `applyDensity()`
           * (`shared/theme/preferences.ts`).
           */}
          <div ref={transitionRef} className={routeTransitionClassName}>
            <Suspense fallback={<RouteFallback />}>
              <Outlet />
            </Suspense>
          </div>
        </AppShell.Main>
      </AppShell>
    </>
  );
}

/**
 * Заглушка на час завантаження чанка маршруту (`PR nav-arch #6`, розділи
 * B3/C1 директиви: «skeleton у формі майбутнього layout'а», не один
 * загальний вигляд на всі ~23 маршрути).
 *
 * ⚠ Форма читається з `handle.skeletonShape` НАЙГЛИБШОГО матчу поточної
 * адреси (`useMatches()`), а не переданого пропа: той самий прийом, що вже
 * несуть `<Breadcrumbs/>` і `<ScrollRestoration/>` поруч (`PR #2`/`#3`) — під
 * час підвантаження чанка `useMatches()` вже знає, ЯКИЙ маршрут зіставлено
 * (зіставлення за шаблоном шляху не чекає на компонент), тож форму можна
 * визначити ще до того, як сама сторінка домонтується.
 *
 * ⚠ Записи БЕЗ `skeletonShape` (більшість реєстру — картка свідомо охопила
 * представницьку вибірку, не всі маршрути, `Q-282`) отримують той самий
 * ЗАГАЛЬНИЙ скелет (`GenericRouteSkeleton`), що існував до цієї картки:
 * відсутність поля — не регрес.
 */
function RouteFallback(): JSX.Element {
  const matches = useMatches();
  const shape = deepestSkeletonShape(matches);

  if (shape === 'table') return <TableRouteSkeleton />;
  if (shape === 'form') return <FormRouteSkeleton />;
  if (shape === 'dashboard') return <DashboardRouteSkeleton />;

  return <GenericRouteSkeleton />;
}

/**
 * Форма скелета найглибшого матчу, що її оголосив (перший знайдений, рахуючи
 * від листа до кореня): той самий порядок пошуку, що логічно веде
 * breadcrumbs-резолвер — найближчий до листа запис реєстру описує сторінку
 * точніше за проміжний layout (`AdminLayout`/`TemplateVersionLayout` не
 * несуть власного `handle.skeletonShape` — голий `Outlet`, нічого показувати).
 */
function deepestSkeletonShape(
  matches: ReturnType<typeof useMatches>,
): RouteHandle['skeletonShape'] {
  for (let index = matches.length - 1; index >= 0; index -= 1) {
    const handle = matches[index]?.handle;
    if (isRouteHandle(handle) && handle.skeletonShape !== undefined) {
      return handle.skeletonShape;
    }
  }

  return undefined;
}

/**
 * Скелет-«заголовок + перелік» — найпоширеніша форма застосунку (`home`,
 * `myGroups`, `adminSecurity`): рядок заголовка й дій (`PageHeader.tsx`:
 * `Group justify="space-between"`), під ним — рядки таблиці.
 */
function TableRouteSkeleton(): JSX.Element {
  return (
    <Stack gap="xs" aria-busy="true" data-testid="route-skeleton-table">
      <Group justify="space-between" mb="xs">
        <Skeleton height={28} width="30%" radius="sm" />
        <Skeleton height={28} width={96} radius="sm" />
      </Group>
      <Skeleton height={24} radius="sm" />
      {Array.from({ length: 6 }, (_, index) => (
        <Skeleton key={index} height={20} radius="sm" />
      ))}
    </Stack>
  );
}

/**
 * Скелет-«форма/деталь» (`adminTemplateVersion`): заголовок і кілька
 * товщих блоків замість тонких рядків таблиці — наближено до розгорнутих
 * секцій (`Accordion`) `TemplateVersionPage`, чия власна `AsyncBoundary`
 * теж позначена `skeleton="form"`.
 */
function FormRouteSkeleton(): JSX.Element {
  return (
    <Stack gap="sm" aria-busy="true" data-testid="route-skeleton-form">
      <Group justify="space-between" mb="xs">
        <Skeleton height={28} width="30%" radius="sm" />
        <Skeleton height={28} width={160} radius="sm" />
      </Group>
      {Array.from({ length: 3 }, (_, index) => (
        <Skeleton key={index} height={64} radius="sm" />
      ))}
    </Stack>
  );
}

/**
 * Скелет-«дашборд» (`adminHealth`): сітка карток, не таблиця — єдиний
 * маршрут застосунку такої форми (`SimpleGrid`, `HealthPage.tsx`), і саме
 * тому представницька вибірка цієї картки включає його окремо.
 */
function DashboardRouteSkeleton(): JSX.Element {
  return (
    <Stack gap="md" aria-busy="true" data-testid="route-skeleton-dashboard">
      <Group justify="space-between" mb="xs">
        <Skeleton height={28} width="30%" radius="sm" />
        <Skeleton height={22} width={72} radius="xl" />
      </Group>
      <SimpleGrid cols={{ base: 1, md: 3 }}>
        {Array.from({ length: 3 }, (_, index) => (
          <Skeleton key={index} height={72} radius="sm" />
        ))}
      </SimpleGrid>
      <Skeleton height={20} width="20%" radius="sm" />
      <Skeleton height={96} radius="sm" />
    </Stack>
  );
}

/**
 * Загальний скелет — незмінний вигляд, що існував до цієї картки
 * (`Q-281`): заголовок і три смуги, для КОЖНОГО маршруту без явного
 * `handle.skeletonShape`.
 *
 * ⚠ Скелет, а не спінер (`ФВ-14.25`): майже кожен екран системи — це заголовок
 * і таблиця під ним, і показати саме цю форму чесніше, ніж крутити коло.
 */
function GenericRouteSkeleton(): JSX.Element {
  return (
    <Stack gap="xs" aria-busy="true" data-testid="route-skeleton-generic">
      <Skeleton height={28} width="30%" radius="sm" />
      <Skeleton height={24} radius="sm" />
      <Skeleton height={24} radius="sm" />
      <Skeleton height={24} radius="sm" />
    </Stack>
  );
}
