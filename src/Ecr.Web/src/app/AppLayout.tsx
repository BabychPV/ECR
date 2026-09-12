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
import { Link, Navigate, Outlet, ScrollRestoration, useLocation } from 'react-router-dom';
import { Breadcrumbs } from './Breadcrumbs';
import { navRoutes } from './routes';
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
                // ⚠ Пункт показується, лише якщо право є: користувач не має
                // тиснути те, що все одно дасть 403.
                <NavLink
                  key={route.path}
                  component={Link}
                  to={route.path}
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

          <Suspense fallback={<RouteFallback />}>
            <Outlet />
          </Suspense>
        </AppShell.Main>
      </AppShell>
    </>
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
