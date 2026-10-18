/**
 * Адреси розділів Permits для компонентів `features/permits`.
 *
 * ⛔ Не `routes.adminPermits.path`: шари клієнта йдуть лише вниз (`features ↛ app`,
 * `ClientLayerRulesTests`), тож `features` не імпортує реєстр маршрутів. Збіг цих адрес із
 * реєстром стереже `app/__tests__/permitsRoutes.test.tsx`.
 */
export const PermitsPaths = {
  permits: '/admin/permits',
  types: '/admin/permits/types',
  lists: '/admin/permits/lists',
  bindings: '/admin/permits/bindings',
} as const;
