import type { JSX } from 'react';
import { NavLink } from '@mantine/core';
import { Link } from 'react-router-dom';
import type { RouteEntry } from './routes';
import { useRoutePrefetch } from './useRoutePrefetch';

/**
 * Один пункт навбару (`PR nav-arch #5`) — окремий компонент, не інлайн
 * усередині `.map()` у `AppLayout`.
 *
 * ⚠ `useRoutePrefetch` — хук: React забороняє викликати хуки всередині
 * callback-функції масиву (Rules of Hooks), а кожному пункту навбару
 * потрібен ВЛАСНИЙ таймер наміру й ВЛАСНИЙ прапорець «уже прогрів» — не
 * один спільний на весь навбар, інакше прогрів одного пункту скасовував би
 * таймер сусіднього.
 */
export function NavRouteLink({
  route,
  label,
  active,
}: {
  route: RouteEntry;
  label: string;
  active: boolean;
}): JSX.Element {
  const prefetch = useRoutePrefetch(route.id);

  return (
    <NavLink
      component={Link}
      to={route.path}
      label={label}
      active={active}
      onMouseEnter={prefetch.onMouseEnter}
      onMouseLeave={prefetch.onMouseLeave}
      onFocus={prefetch.onFocus}
      onBlur={prefetch.onBlur}
    />
  );
}
