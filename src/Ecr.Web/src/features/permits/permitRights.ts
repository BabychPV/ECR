/**
 * Право на керування типами пермітів (маршрути `/admin/permits/types[/:code]`, смуга розділів, кнопка «New type»).
 *
 * ⚠ ТИМЧАСОВО `Registry.EditDefinition`: Аудит вирішив, що типами керують нові права `Permit.EditType` /
 * `Permit.PublishType` (сідить PM-04). Після мержу PM-04 оркестратор міняє значення ТУТ — один рядок; решта
 * (routes.ts, PermitsSubnav, PermitTypesPage, тести) читає цю константу.
 */
export const PermitTypesPermission = 'Registry.EditDefinition';
