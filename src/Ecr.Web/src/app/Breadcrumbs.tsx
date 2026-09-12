import { useEffect, useState, type JSX } from 'react';
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
 * ⚠ НЕ використовується поза цим файлом (свідомо). `useRouteTransitionFocus
 * .ts` (`PR nav-arch #7`) резолвить `document.title` ТИМ САМИМ
 * `buildCrumbChain` (експортовано нижче), але НЕ бере другого екземпляра
 * цієї підписки — під час імплементації друга незалежна підписка на
 * `QueryCache` в тому самому дереві (`AppLayout`, де вже є справжній
 * `useQuery` — `useSession()`) детерміновано зациклювала рендер (RED,
 * відтворено й підтверджено видаленням саме цього виклику). Причина
 * лишається поза межами цієї картки; `useRouteTransitionFocus.ts` називає
 * прогалину прямо (заголовок вкладки не гарантовано оновлюється в ту саму
 * мить, що видимі крихти, лише на наступному з інших причин перемальовуванні).
 */
function useCacheVersion(queryClient: QueryClient): void {
  const [, setVersion] = useState(0);

  // ⚠ Підписка живе в `useEffect`, з відпискою в поверненій функції: без неї
  // кожен новий рендер лишав би по собі ще одного підписника на `QueryCache`,
  // який ніколи не відписується (витік). Залежність — лише `queryClient`
  // (стабільний на весь застосунок): підписка НЕ повинна перестворюватися на
  // кожну зміну самого `version`, бо саме вона його й змінює.
  useEffect(() => {
    return queryClient.getQueryCache().subscribe(() => setVersion((v) => v + 1));
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

  useCacheVersion(queryClient);

  const chain = buildCrumbChain(matches, queryClient);

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
