import type { JSX } from 'react';
import { useRouteError } from 'react-router-dom';
import { RenderErrorScreen } from './RenderErrorScreen';

/**
 * `errorElement` дочірніх маршрутів `AppLayout` (`D14-11`).
 *
 * ⚠ Межа стоїть НЕ на корені `/`, а на безшляховому маршруті-обгортці між
 * `AppLayout` і його дітьми (`router.tsx`, `withRenderErrorBoundary`) — саме
 * тому меню й шапка лишаються живими, а замінюється сама лише область
 * змісту. Межа на корені зробила б із помилки однієї сторінки повну втрату
 * застосунку: рівно те, що було до цієї зміни.
 *
 * ⚠ `useRouteError()` віддає і помилки рендера, і відхилені `import()`
 * лінивих чанків (`DAT-08`) — React Router не розрізняє їх, і розрізняє їх
 * `RenderErrorScreen` за станом `isStaleVersion()`.
 */
export function RouteErrorPage(): JSX.Element {
  const error = useRouteError();

  return <RenderErrorScreen error={error} />;
}
