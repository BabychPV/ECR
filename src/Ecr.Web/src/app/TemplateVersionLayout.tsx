import type { JSX } from 'react';
import { Outlet } from 'react-router-dom';

/**
 * Layout-маршрут секції `admin/templates/:id/*` (`PR nav-arch #2`).
 *
 * ⚠ Другий кандидат на власний layout, названий директивою прямо
 * (`templates/:id/*` як «секція з власними вкладками версій/зв'язків»):
 * об'єднує `TemplateVersionPage`
 * (`admin/templates/:id/versions/:versionId`) і `TableRelationsPage`
 * (`admin/templates/:id/versions/:versionId/relations`) — обидві належать
 * ОДНОМУ шаблону/версії (`:id`/`:versionId` спільні для обох), на відміну
 * від `/admin/registries` і `/admin/methodologies`, де в кожної секції
 * рівно один дочірній деталь-маршрут і спільного піддерева немає (див.
 * обґрунтування в Q-N цієї картки — не додано layout без реальної спільної
 * вкладеності).
 *
 * ⛔ Свідомо БЕЗ tab-бару між версією й зв'язками в цій картці: перемикання
 * вимагало б або дублювати навігацію, яку `TableRelationsPage` вже дає
 * (посилання назад на версію), або чекати на `handle`/`useMatches()`
 * (`PR #3`), щоб tab-бар знав людинозрозумілу назву поточного рівня, а не
 * лише сирий `:id`/`:versionId`. Layout поки лише вкладає обидва маршрути в
 * спільний контекст (`Outlet`) — сама вкладка навігації секції — природне
 * продовження, коли з'явиться `useMatches()`.
 */
export function TemplateVersionLayout(): JSX.Element {
  return <Outlet />;
}
