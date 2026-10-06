import type { JSX } from 'react';
import { NavLink } from '@mantine/core';
import { t } from '@/shared/i18n';
import { LazyHint } from './LazyHint';
import { IconOnlyNavLinkStyles } from './NavRouteLink';
import { setNavbarCollapsed } from '@/shared/theme/navbarCollapse';

/**
 * Кнопка «Згорнути меню / Розгорнути меню» внизу бічної панелі.
 *
 * ⚠ `aria-expanded` — про САМЕ меню: розгорнуте (`true`) показує назви
 * пунктів, згорнуте — лише іконки. Назва кнопки міняється разом зі станом, бо
 * кнопка-перемикач із незмінним ім'ям не каже, що станеться після натискання.
 *
 * ⛔ Лише на ширині від `sm`: нижче меню — шухляда на всю ширину за бургером,
 * і «лише іконки» там зробили б з повноцінного меню вузьку смужку на
 * порожньому полі.
 */
export function NavbarCollapseToggle({
  collapsed,
  controls,
}: {
  collapsed: boolean;
  /** Ідентифікатор списку пунктів меню (`aria-controls`). */
  controls: string;
}): JSX.Element {
  const label = t(collapsed ? 'nav.expand' : 'nav.collapse');

  // ⛔ `NavLink`, а не `ActionIcon`: `NavLink` уже у вхідному чанку (пункти
  // меню), а `ActionIcon` тягнув би туди власний чанк (+1.2 КБ gzip до
  // кожного маршруту, бюджет `D-132`). Заодно кнопка виглядає як пункт меню
  // і в розгорнутому стані має видимий текст.
  const button = (
    <NavLink
      component="button"
      type="button"
      label={collapsed ? undefined : label}
      aria-label={collapsed ? label : undefined}
      aria-expanded={!collapsed}
      aria-controls={controls}
      c="dimmed"
      styles={collapsed ? IconOnlyNavLinkStyles : {}}
      leftSection={
        <svg
          width={20}
          height={20}
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth={1.75}
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden="true"
          focusable="false"
        >
          {/* Подвійний шеврон: «назад» — згорнути, «вперед» — розгорнути. */}
          <path d={collapsed ? 'm7 7 5 5-5 5M13 7l5 5-5 5' : 'm17 7-5 5 5 5M11 7l-5 5 5 5'} />
        </svg>
      }
      onClick={() => {
        setNavbarCollapsed(!collapsed);
      }}
    />
  );

  // Підказка потрібна лише без видимого тексту. ⛔ Обгортка — в обох станах
  // (`disabled`): умовна перемонтовувала б кнопку на кожному натисканні, і
  // фокус клавіатури після Enter падав би на `body` (рев'ю 06.10, P2-1).
  return (
    <LazyHint label={label} position="right" disabled={!collapsed}>
      {button}
    </LazyHint>
  );
}
