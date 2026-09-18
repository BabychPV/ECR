import { useEffect, useRef, useState, type JSX } from 'react';
import { Anchor, Breadcrumbs as MantineBreadcrumbs, Skeleton, Text, UnstyledButton } from '@mantine/core';
import type { QueryClient } from '@tanstack/react-query';
import { useQueryClient } from '@tanstack/react-query';
import { Link, useMatches } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { resolveCrumbValue, type CrumbParams } from './breadcrumbResolvers';
import { routeList, type RouteHandle } from './routes';

/**
 * Breadcrumbs, керовані даними маршруту (`PR nav-arch #3`, `handle.crumb` +
 * `useMatches()`).
 *
 * ⛔ Один екземпляр, у `AppLayout` (той самий аргумент, що й для
 * `<ScrollRestoration/>` у `PR #2`): `useMatches()` сам стежить за ВСІМ
 * деревом збігів поточної адреси — другий екземпляр глибше в дереві
 * (`AdminLayout`, `TemplateVersionLayout`) не додав би нової інформації,
 * лише повторно прочитав би той самий масив.
 *
 * ⛔ **Нуль нових HTTP-запитів** — це вимога №1 директиви для цієї картки, не
 * побажання. Компонент і резолвер (`breadcrumbResolvers.ts`) читають кеш
 * ЛИШЕ через `queryClient.getQueryData`/`getQueryState`; жодного `useQuery` з
 * `queryFn`, що щось запитує. Доведено тестом, що ставить шпигуна на
 * `globalThis.fetch` (`Breadcrumbs.test.tsx`), а не лише оглядом коду.
 *
 * ⚠ Кеш не є реактивним сам по собі (`getQueryData` — одноразове читання):
 * без явної підписки на `QueryCache` крихта, що застала кеш холодним при
 * першому рендері (`Skeleton`), НІКОЛИ не оновилася б до реальної назви,
 * навіть коли сторінка, що ділить `queryClient`, сам запит завершить.
 * `useCacheVersion` нижче форсує перерендер на кожній зміні кешу — це
 * підписка на ПОДІЮ кешу, не запит.
 *
 * ⚠ Q-305: `setVersion` у підписці ВІДКЛАДЕНО через `queueMicrotask`, а не
 * викликається напряму з колбека `subscribe`. Причина — не стиль, а
 * відтворена й підтверджена вада: `QueryCache.notify()` (`query-core`)
 * викликає підписників СИНХРОННО всередині `notifyManager.batch()`, без
 * будь-якого відкладення. Кожен `useQuery`, що вперше монтується для ключа,
 * якого ще нема в кеші (перехід на будь-яку сторінку з власним запитом:
 * `RouteGuard.useSession()`, `DocumentsPage`, `CreateDocumentModal`, …),
 * створює `QueryObserver` через `useState(() => new Observer(...))` —
 * ЛІНИВИЙ ІНІЦІАЛІЗАТОР, що виконується ПІД ЧАС рендера цього ж компонента
 * (`useBaseQuery.ts`, `@tanstack/react-query`). Конструктор одразу викликає
 * `queryCache.build()` → `add()` → синхронний `notify()` — тобто підписник
 * `Breadcrumbs` отримує подію й викликає `setVersion` ПІД ЧАС рендера ІНШОГО
 * компонента. React це ловить (`scheduleUpdateOnFiber`) і видає «Cannot
 * update a component while rendering a different component» — саме
 * попередження цієї картки, на кожному переході сторінки, що монтує новий
 * запит.
 *
 * ⛔ `useSyncExternalStore` (природний перший вибір для зовнішньої підписки,
 * той самий приём, що й `useCatalog.ts`) ПЕРЕВІРЕНО й НЕ рятує: його
 * внутрішній `forceStoreRerender` викликає ТОЙ САМИЙ `scheduleUpdateOnFiber`,
 * що й `dispatchSetState`, — попередження лишається (відтворено вручну
 * live-стендом, стек показує `forceStoreRerender` замість `dispatchSetState`,
 * результат ідентичний). Причина в тому, що попередження прив'язане не до
 * конкретного хука, а до самого факту виклику `scheduleUpdateOnFiber` під
 * час рендера ІНШОГО файбера — `queueMicrotask` єдиний з розглянутих
 * варіантів, що виносить виклик ЗА межі поточного синхронного стека
 * рендера/коміту, а не підмінює механізм оновлення.
 *
 * ⚠ НЕ використовується поза цим файлом (свідомо). `useRouteTransitionFocus
 * .ts` (`PR nav-arch #7`) резолвить `document.title` ТИМ САМИМ
 * `buildCrumbChain` (експортовано нижче), але НЕ бере другого екземпляра
 * цієї підписки — під час імплементації друга незалежна підписка на
 * `QueryCache` в тому самому дереві (`AppLayout`, де вже є справжній
 * `useQuery` — `useSession()`) детерміновано зациклювала рендер (RED,
 * відтворено й підтверджено видаленням саме цього виклику). Причина названа
 * нижче (`useCacheVersion`) і тепер усунена — але сам виклик у
 * `useRouteTransitionFocus.ts` лишається відсутнім: це окрема картка.
 *
 * ### ✎ Livelock рендера: чому відкладення через `queueMicrotask` було
 * ### половиною виправлення
 *
 * ⛔ Q-305 (вище) прибрав ПОПЕРЕДЖЕННЯ React, але не прибрав зворотний
 * зв'язок, що його викликав, — лише переніс його на мікрозадачу. Повний
 * цикл, відтворений на живому стенді (`tools/e2e-stand.ps1`, dev-сервер
 * Vite, роль `e2e-admin`, маршрут `/admin/units`):
 *
 *   1. React починає КОНКУРЕНТНИЙ рендер піддерева маршруту й віддає
 *      керування кожні ~5 мс (`renderRootConcurrent` → `shouldYield`).
 *   2. Під час цього рендера кожен `useQuery`/`useMutation` сторінки
 *      створює свій `QueryObserver` лінивим ініціалізатором
 *      `useState(() => new Observer(...))` — тобто ПІД ЧАС рендера, —
 *      а конструктор синхронно кличе `queryCache.notify()`.
 *   3. Підписник нижче ставить `setVersion` у мікрозадачу. Мікрозадачі
 *      виконуються ОДРАЗУ після поточного зрізу роботи React і ДО
 *      наступного — тобто оновлення приходить рівно в паузу між зрізами.
 *   4. React відкидає незавершений рендер і починає його спочатку. Крок 2
 *      повторюється — бо ініціалізатори `useState` невиконаного рендера
 *      виконуються знову. ПЕРЕХІД ДО 1.
 *
 * ⚠ Цикл розривається лише випадково — якщо піддерево встигне
 * відрендеритися ЦІЛКОМ за один зріз. Тому дефект і виглядав як «іноді
 * повільно»: у виробничій збірці рендер укладається в зріз завжди
 * (виміряно: `/admin/units` — 294–426 мс, 6 прогонів із 6), а на dev-сервері
 * (React у режимі розробки, ~на порядок повільніший) сторінка з трьома
 * полями вводу в шапці не встигає НІКОЛИ: 6 прогонів із 6 — понад 40 с,
 * 15 000 рендерів `RouteGuard` і ЖОДНОГО коміту (`commitRoot` не
 * викликався). Саме це й валило `e2e/screenshots.spec.ts` на `units` під
 * роллю `admin` — і лише під нею: оператор на `/admin/*` бачить
 * `AccessDeniedPage` (заголовок є одразу), тож до важкого піддерева не
 * доходить.
 *
 * ⛔ Виправлення — НЕ прибрати підписку (вона й далі потрібна: крихта, що
 * застала кеш холодним, інакше довіку лишиться `Skeleton`) і НЕ «почекати
 * довше». Підписка тепер перерендерює ЛИШЕ тоді, коли змінилося те, що
 * крихти реально показують (`crumbSignature`). Подія «з'явився спостерігач
 * чужого запиту» крихт не змінює — і рендер більше не переривається.
 */
function crumbSignature(chain: readonly CrumbEntry[]): string {
  // ⚠ Розділювачі — недруковані символи: назва довідника чи версії приходить
  // із бази, і будь-який звичайний роздільник (`|`, `::`) у ній зустрічається
  // легально. Збіг відбитків для РІЗНИХ ланцюжків означав би пропущене
  // оновлення — тобто вічний `Skeleton` замість назви.
  return chain
    .map(
      (entry) =>
        `${entry.key}${entry.text ?? ' '}${entry.href ?? ' '}${
          entry.current ? '1' : '0'
        }`,
    )
    .join('');
}

function useCacheVersion(
  queryClient: QueryClient,
  matches: readonly CrumbMatch[],
  chain: readonly CrumbEntry[],
): void {
  const [, setVersion] = useState(0);

  // ⚠ Відбиток того, що ЗАРАЗ НА ЕКРАНІ, і матчі, з яких він побудований.
  // Пишуться в ефекті (після коміту), а не під час рендера: рендер, який
  // React відкинув, нічого не показав, і брати його за «показане» означало б
  // пропустити справжнє оновлення.
  const painted = useRef<string | null>(null);
  const currentMatches = useRef(matches);

  useEffect(() => {
    painted.current = crumbSignature(chain);
    currentMatches.current = matches;
  });

  // ⚠ Підписка живе в `useEffect`, з відпискою в поверненій функції: без неї
  // кожен новий рендер лишав би по собі ще одного підписника на `QueryCache`,
  // який ніколи не відписується (витік). Залежність — лише `queryClient`
  // (стабільний на весь застосунок): підписка НЕ повинна перестворюватися на
  // кожну зміну самого `version`, бо саме вона його й змінює.
  useEffect(() => {
    // ⛔ `cancelled` — не про повторний рендер, а про те, що мікрозадача,
    // поставлена В ЧЕРГУ до розмонтування, все одно виконається ПІСЛЯ нього
    // (`queueMicrotask` не скасовується відпискою нижче). Без цього прапорця
    // `setVersion` на розмонтованому компоненті — не катастрофа (React 18
    // мовчки ігнорує таке оновлення), але й не задокументована поведінка,
    // на яку варто покладатися.
    let cancelled = false;
    const unsubscribe = queryClient.getQueryCache().subscribe(() => {
      // Q-305: без цього відкладення `setVersion` виконується СИНХРОННО
      // всередині `notify()`, який сам може бути викликаний під час рендера
      // ІНШОГО компонента (будь-який `useQuery`, що вперше монтується для
      // нового ключа кешу).
      queueMicrotask(() => {
        if (cancelled) return;

        // ⛔ І лише тут — умова, якої бракувало (див. «Livelock рендера»
        // вище). Читання з кешу, без жодного запиту: `buildCrumbChain`
        // ходить виключно через `getQueryData`/`getQueryState`.
        const next = crumbSignature(buildCrumbChain(currentMatches.current, queryClient));
        if (next === painted.current) return;

        // ⚠ Відбиток оновлюється ТУТ, а не тільки в ефекті після коміту:
        // інакше друга така сама подія, що прийшла до коміту, замовила б
        // другий рендер із тим самим результатом — тобто рівно той цикл, що
        // виправляється.
        painted.current = next;
        setVersion((v) => v + 1);
      });
    });
    return () => {
      cancelled = true;
      unsubscribe();
    };
  }, [queryClient]);
}

/** Один елемент ланцюжка breadcrumbs — уже вирішений (текст/посилання/позиція). */
export interface CrumbEntry {
  /** Стабільний ключ для списку React і для тестів. */
  key: string;
  /** `null` — назва ще резолвиться (кеш теплий, запит іде): показати `Skeleton`. */
  text: string | null;
  /** `undefined` — не посилання (поточна позиція або немає чинної цілі). */
  href: string | undefined;
  /** Остання крихта ланцюжка — `aria-current="page"`, ніколи не посилання. */
  current: boolean;
}

/** Форма одного елемента `useMatches()`, звужена до того, що тут потрібно. */
export interface CrumbMatch {
  pathname: string;
  params: CrumbParams;
  handle: unknown;
}

/** Звужує `unknown` (форма `RouteObject.handle` за задумом React Router) до `RouteHandle`. */
export function isRouteHandle(handle: unknown): handle is RouteHandle {
  return (
    typeof handle === 'object' &&
    handle !== null &&
    typeof (handle as { labelKey?: unknown }).labelKey === 'string'
  );
}

/** Підставляє `:name` реєстрового шляху значеннями параметрів найглибшого матчу. */
function fillParams(path: string, params: CrumbParams): string {
  return path.replace(/:([A-Za-z0-9_]+)/g, (literal, name: string) => params[name] ?? literal);
}

function crumbTextAndHref(
  handle: RouteHandle,
  ownPathname: string,
  params: CrumbParams,
  queryClient: QueryClient,
): { text: string | null; href: string } {
  const crumb = handle.crumb;
  let text: string | null = t(handle.labelKey);

  if (crumb?.resolveWith !== undefined) {
    const resolution = resolveCrumbValue(queryClient, crumb.resolveWith, params);
    if (resolution.status === 'resolved') text = resolution.text;
    else if (resolution.status === 'loading') text = null;
    // 'unavailable' — лишається статичний `labelKey` з `t()` вище: не вічний
    // скелет і не сирий `:id`, коли кеш холодний і жоден запит не йде.
  }

  return { text, href: crumb?.linkTo ?? ownPathname };
}

/**
 * Будує вирішений ланцюжок крихт із матчів поточного маршруту — чиста
 * функція, окрема від рендера саме заради мутаційного тесту (підмінити
 * `queryClient` на порожній і довести, що RED, без jsdom/Mantine).
 */
export function buildCrumbChain(matches: readonly CrumbMatch[], queryClient: QueryClient): CrumbEntry[] {
  const entries: CrumbEntry[] = [];
  const leafParams = matches.length > 0 ? matches[matches.length - 1]!.params : ({} as CrumbParams);

  for (const match of matches) {
    if (!isRouteHandle(match.handle)) continue;
    const handle = match.handle;

    for (const ancestorId of handle.crumb?.ancestorIds ?? []) {
      const ancestorRoute = routeList.find((route) => route.id === ancestorId);
      // ⛔ Реєстр і його посилання на предків — той самий модуль
      // (`routes.ts`): якщо `ancestorId` осиротів (запис перейменували чи
      // видалили), це помилка реєстру, а не рантайму користувача — мовчки
      // пропустити тут означало б показати обрізаний ланцюжок без пояснення.
      // `routeConfig.test.ts` (`PR #1`) уже стереже унікальність `id`; тест
      // цієї картки (`Breadcrumbs.test.tsx`) додатково стереже, що жоден
      // `ancestorIds` не веде в порожнечу.
      if (ancestorRoute === undefined) continue;

      const { text, href } = crumbTextAndHref(
        ancestorRoute.handle,
        fillParams(ancestorRoute.path, leafParams),
        leafParams,
        queryClient,
      );
      entries.push({ key: `ancestor:${ancestorRoute.id}`, text, href, current: false });
    }

    const { text, href } = crumbTextAndHref(handle, match.pathname, leafParams, queryClient);
    entries.push({ key: `match:${match.pathname}`, text, href, current: false });
  }

  const lastIndex = entries.length - 1;
  if (lastIndex >= 0) {
    // ⛔ Остання крихта — ЗАВЖДИ поточна позиція, ніколи посилання
    // (акцептанс, `aria-current="page"`): навіть якщо її власний `handle`
    // ніс `linkTo` чи звичайний `pathname`, тут це перекривається.
    entries[lastIndex] = { ...entries[lastIndex]!, href: undefined, current: true };
  }

  return entries;
}

/** З якого розміру ланцюжка усікати середину (директива: «довгі ланцюги (≥4 рівні)»). */
const TruncateFrom = 4;

function CrumbLabel({ entry }: { entry: CrumbEntry }): JSX.Element {
  if (entry.text === null) {
    // ⚠ Вузький скелет, не порожнє місце і не сирий `:id`/GUID (акцептанс
    // п.3): ширина — орієнтовна довжина короткої назви, не на всю крихту.
    return <Skeleton height={14} width={64} radius="sm" data-testid="crumb-skeleton" />;
  }

  if (entry.current) {
    return (
      <Text component="span" fw={600} aria-current="page">
        {entry.text}
      </Text>
    );
  }

  if (entry.href === undefined) {
    // Немає чинної цілі (не мало б статися для не-останньої крихти за
    // поточним реєстром, але текст усе одно не повинен виглядати як мертве
    // посилання, якщо колись станеться).
    return <Text component="span">{entry.text}</Text>;
  }

  return (
    <Anchor component={Link} to={entry.href} underline="hover">
      {entry.text}
    </Anchor>
  );
}

/**
 * Розкриваюча кнопка усіченого ланцюжка (акцептанс: «усікати середину... з
 * розкриттям, а не ламати верстку»).
 *
 * ⛔ Текст — не через `t()`. Той самий компроміс, що й `SkipToContentLink`
 * (`AppLayout.tsx`, Q-263): каталог рядків живе в
 * `Ecr.Infrastructure/Persistence/Sql/09-seed.sql`, а ця картка (як і Q-263)
 * навмисно обмежена файлами клієнта — новий ключ каталогу вимагав би правки
 * seed-файлу, спільного з паралельними лініями, поза межами картки.
 * Судження зафіксоване тут одним рядком: англійський літерал лишається доти,
 * доки окрема картка не заведе ключ.
 */
function ExpandButton({ onClick }: { onClick: () => void }): JSX.Element {
  return (
    <UnstyledButton
      onClick={onClick}
      aria-label="Show all breadcrumbs"
      aria-expanded={false}
      c="dimmed"
      px="xs"
    >
      …
    </UnstyledButton>
  );
}

/** Каркас застосунку рендерить це РІВНО один раз (`AppLayout.tsx`). */
export function Breadcrumbs(): JSX.Element | null {
  const matches = useMatches();
  const queryClient = useQueryClient();
  const [expanded, setExpanded] = useState(false);

  const chain = buildCrumbChain(matches, queryClient);

  // ⚠ Після `buildCrumbChain`, не перед: підписці потрібен саме той ланцюжок,
  // який цей рендер збирається показати (умова «змінилося те, що видно»).
  // Обидва виклики — беззастережні й до єдиного раннього `return` нижче.
  useCacheVersion(queryClient, matches, chain);

  // Акцептанс: видимі на маршрутах глибиною ≥2. «Глибина» тут — кількість
  // РЕЗОЛВЛЕНИХ крихт (після `ancestorIds`), не кількість сегментів URL:
  // `/admin/templates` сам по собі дає РІВНО одну крихту (`AdminLayout` —
  // свідомо без власної, `Q-277`), і ланцюжок з одного елемента без предка
  // не несе жодного орієнтира понад те, що вже каже заголовок сторінки.
  if (chain.length < 2) return null;

  const visible =
    chain.length >= TruncateFrom && !expanded
      ? [chain[0]!, undefined, chain[chain.length - 2]!, chain[chain.length - 1]!]
      : chain;

  return (
    <MantineBreadcrumbs mb="sm" data-testid="breadcrumbs">
      {visible.map((entry) =>
        entry === undefined ? (
          <ExpandButton key="ellipsis" onClick={() => setExpanded(true)} />
        ) : (
          <CrumbLabel key={entry.key} entry={entry} />
        ),
      )}
    </MantineBreadcrumbs>
  );
}
