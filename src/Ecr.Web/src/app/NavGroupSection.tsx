import type { JSX, ReactNode } from 'react';
import { Box, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { NavGroup } from './routes';

/**
 * Одна група бічного меню (UI-12, макет `docs/design/hybrid/index.html`,
 * `.rail-g`): підпис групи над пунктами, коли меню розгорнуте; тонкий
 * роздільник між групами, коли меню згорнуте до іконок.
 *
 * ⚠ `role="group"` з іменем групи в ОБОХ станах: у згорнутому меню підпису
 * не видно, але читалка й далі чує «Configure, group», а не пласкі 16 пунктів.
 *
 * ⛔ Обгортка не додає зупинок `Tab`: підпис — текст, роздільник — `aria-hidden`.
 * Порядок обходу лишається порядком пунктів.
 */
export function NavGroupSection({
  group,
  first,
  collapsed,
  children,
}: {
  group: NavGroup;
  /** Перша видима група: роздільника над нею немає (макет: `.rail-g:first-child`). */
  first: boolean;
  collapsed: boolean;
  children: ReactNode;
}): JSX.Element {
  const label = t(group.labelKey);
  const labelId = `nav-group-${group.id}`;

  return (
    <div
      role="group"
      aria-labelledby={collapsed ? undefined : labelId}
      aria-label={collapsed ? label : undefined}
      data-nav-group={group.id}
    >
      {collapsed ? (
        !first && (
          <Box aria-hidden="true" data-nav-group-divider="" h={1} mx="sm" my="xs" bg="var(--mantine-color-default-border)" />
        )
      ) : (
        <Text
          id={labelId}
          size="xs"
          fw={500}
          tt="uppercase"
          c="dimmed"
          px="sm"
          pt={first ? 0 : 'sm'}
          pb="xs"
          style={{ letterSpacing: '0.06em' }}
        >
          {label}
        </Text>
      )}
      {children}
    </div>
  );
}
