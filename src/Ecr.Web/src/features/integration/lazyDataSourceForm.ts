import { lazy } from 'react';

/**
 * Форма з'єднання — за `import()`.
 *
 * ⚠ Бюджет маршруту `/admin/sources` (`D-132`, 250 КБ gzip): статичний імпорт
 * форми (`Select`, `NumberInput`) вивів маршрут на 252.3 КБ. Форму відкривають
 * рідко, тож чекати її першого показу дешевше, ніж тягнути її з кожним
 * відкриттям екрана.
 */
export const DataSourceFormModal = lazy(() =>
  import('./DataSourceFormModal').then((module) => ({ default: module.DataSourceFormModal })),
);

/**
 * Вкладка розкладів шухляди з'єднання — за `import()` з тієї ж причини:
 * редактор розкладу (`CollectionScheduleTab`, розбір cron) потрібен лише
 * тому, хто відкрив вкладку.
 */
export const DataSourceScheduleTab = lazy(() =>
  import('./DataSourceScheduleTab').then((module) => ({ default: module.DataSourceScheduleTab })),
);

/**
 * Вкладка сутностей збору (`ФВ-13.11`) — за `import()` з тієї ж причини:
 * форма з каталогу й вибір довідника потрібні лише тому, хто її відкрив.
 */
export const SourceEntitiesTab = lazy(() =>
  import('./SourceEntitiesTab').then((module) => ({ default: module.SourceEntitiesTab })),
);

/**
 * Вкладка «Події з PI» (HSE301 A6) — за `import()` з тієї ж причини: таблиця подій, мапінги й форма мапінгу
 * потрібні лише тому, хто її відкрив, і в чанк сторінки джерел (а тим паче в `DocumentPage`) не входять.
 */
export const SourceEventsTab = lazy(() =>
  import('@/features/sources/SourceEventsTab').then((module) => ({ default: module.SourceEventsTab })),
);
