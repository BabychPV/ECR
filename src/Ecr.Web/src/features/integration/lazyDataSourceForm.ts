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
