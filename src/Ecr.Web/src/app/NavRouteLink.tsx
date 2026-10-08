import type { JSX } from 'react';
import { Badge, NavLink } from '@mantine/core';
import { LazyHint } from './LazyHint';
import { Link } from 'react-router-dom';
import { NavIcon } from '@/shared/ui/navIcons';
import type { RouteEntry } from './routes';
import { useRoutePrefetch } from './useRoutePrefetch';
import { useStaleResultsNavCount } from './useStaleResultsNavCount';
import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';

/**
 * Один пункт навбару (`PR nav-arch #5`) — окремий компонент, не інлайн
 * усередині `.map()` у `AppLayout`.
 *
 * ⚠ `useRoutePrefetch` — хук: React забороняє викликати хуки всередині
 * callback-функції масиву (Rules of Hooks), а кожному пункту навбару
 * потрібен ВЛАСНИЙ таймер наміру й ВЛАСНИЙ прапорець «уже прогрів» — не
 * один спільний на весь навбар, інакше прогрів одного пункту скасовував би
 * таймер сусіднього.
 *
 * ⚠ `leftSection`/`NavIcon` (картка «іконки навбару», злито сюди при
 * розв'язанні конфлікту з цією карткою): обидві картки чіпають той самий
 * `<NavLink>` — одна додає обробники наміру, друга додає слот іконки.
 * `AppLayout.tsx` рендерить РІВНО один компонент на пункт навбару, тож
 * обидві відповідальності мусять зійтись в ОДНОМУ елементі, а не в двох
 * незалежних рендерах, що конкурували б за той самий `<NavLink>`.
 */
/**
 * Пункт меню без тексту: іконка по центру вузької колонки. Без цього
 * порожнє тіло (`flex: 1`) і відступ секції притискали іконку ліворуч.
 */
export const IconOnlyNavLinkStyles = {
  root: { justifyContent: 'center' },
  section: { marginInlineEnd: 0 },
  body: { display: 'none' },
} as const;

export function NavRouteLink({
  route,
  label,
  active,
  collapsed = false,
}: {
  route: RouteEntry;
  label: string;
  active: boolean;
  /**
   * Меню згорнуте до іконок: назва — доступне ім'я (`aria-label`) і підказка
   * при наведенні та фокусі, бо видимого тексту поруч з іконкою немає.
   */
  collapsed?: boolean;
}): JSX.Element {
  const prefetch = useRoutePrefetch(route.id);
  const staleCount = useStaleResultsNavCount(route.id === 'home');
  const staleLabel = staleCount > 0 ? t('nav.documents.staleCount', { count: staleCount }) : null;

  const link = (
    <NavLink
      component={Link}
      to={route.path}
      label={collapsed ? undefined : label}
      aria-label={collapsed ? (staleLabel === null ? label : `${label}: ${staleLabel}`) : undefined}
      leftSection={<NavIcon name={route.handle.icon} />}
      active={active}
      rightSection={
        staleLabel === null || collapsed ? undefined : (
          <Badge size="xs" circle color="statusWarning" title={staleLabel} aria-label={staleLabel} data-nav-stale-count={staleCount}>
            {formatNumber(staleCount)}
          </Badge>
        )
      }
      styles={collapsed ? IconOnlyNavLinkStyles : {}}
      onMouseEnter={prefetch.onMouseEnter}
      onMouseLeave={prefetch.onMouseLeave}
      onFocus={prefetch.onFocus}
      onBlur={prefetch.onBlur}
    />
  );

  // ⚠ Підказка і на фокусі (`Hint`): клавіатурний користувач без миші інакше
  // не дізнався б назви пункту, бачачи лише іконку. Доступне ім'я дає
  // `aria-label` вище. ⛔ `Hint` (на `Popover`, лінивим чанком), а не `Tooltip` Mantine: той
  // тягне взаємодії `@floating-ui/react` у вхідний чанк, +8 КБ gzip до КОЖНОГО
  // маршруту, і `PipelinePage` виходила за межу `D-132`. Обгортка — в обох
  // станах (`disabled`), щоб пункт не перемонтовувався при перемиканні.
  return (
    <LazyHint label={label} position="right" disabled={!collapsed}>
      {link}
    </LazyHint>
  );
}
