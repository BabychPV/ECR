import type { JSX } from 'react';
import { NavLink } from '@mantine/core';
import { Link } from 'react-router-dom';
import { NavIcon } from './navIcons';
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
 *
 * ⚠ `leftSection`/`NavIcon` (картка «іконки навбару», злито сюди при
 * розв'язанні конфлікту з цією карткою): обидві картки чіпають той самий
 * `<NavLink>` — одна додає обробники наміру, друга додає слот іконки.
 * `AppLayout.tsx` рендерить РІВНО один компонент на пункт навбару, тож
 * обидві відповідальності мусять зійтись в ОДНОМУ елементі, а не в двох
 * незалежних рендерах, що конкурували б за той самий `<NavLink>`.
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
      leftSection={<NavIcon name={route.handle.icon} />}
      active={active}
      onMouseEnter={prefetch.onMouseEnter}
      onMouseLeave={prefetch.onMouseLeave}
      onFocus={prefetch.onFocus}
      onBlur={prefetch.onBlur}
    />
  );
}
