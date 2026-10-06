import { useMediaQuery } from '@mantine/hooks';

/**
 * Вузький екран (телефон) — межа макета `docs/design/hybrid/index.html` `@media (max-width:640px)`.
 *
 * ⛔ `DIRECTIVE-15-FRONTEND.md` («Вузький екран»): «Сітка документа на телефоні — читання, не
 * редагування (банер `docs-narrow-note`)». Тому документ на такому екрані — лише для читання
 * (`UI-42`), і межа одна на весь застосунок.
 */
export const NarrowScreenQuery = '(max-width: 640px)';

/** Синхронно з першого рендера: інакше сітка на мить ставала б редагованою на телефоні. */
export function useNarrowScreen(): boolean {
  return useMediaQuery(NarrowScreenQuery, false, { getInitialValueInEffect: false }) === true;
}
