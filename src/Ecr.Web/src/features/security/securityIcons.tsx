import type { JSX } from 'react';

/**
 * Лінійні іконки екрана безпеки — шляхи з набору макета (`kit.js`, `E.icon`):
 * `lock`, `pencil`, `shieldAlert`, `check`, `minus`, `chevronR`.
 *
 * ⚠ Рукописний inline SVG, як `app/navIcons.tsx`: нова npm-залежність заради
 * шести значків — це `package-lock.json` (гарячий файл) і бюджет маршруту.
 * Кожна — `aria-hidden`: поруч завжди стоїть слово, яке і є доступним ім'ям.
 */
const Paths = {
  lock: 'M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 0 1 7 0v3',
  pencil: 'M4 20l4-1L19 8l-3-3L5 16z',
  shieldAlert: 'M12 3l7 3v5c0 4.5-3 8.2-7 10-4-1.8-7-5.5-7-10V6zM12 8v4.5M12 15.5v.5',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  minus: 'M5 12h14',
  chevronR: 'M9 6l6 6-6 6',
} as const;

export type SecurityIconName = keyof typeof Paths;

export function SecurityIcon({ name, size = 14 }: { readonly name: SecurityIconName; readonly size?: number }): JSX.Element {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      data-icon={name}
    >
      <path d={Paths[name]} />
    </svg>
  );
}
