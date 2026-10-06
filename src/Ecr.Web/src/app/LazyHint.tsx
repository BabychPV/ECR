import { lazy, Suspense, type ComponentProps, type JSX } from 'react';
import type { FloatingPosition } from '@mantine/core';
import type { Hint as HintComponent } from '@/shared/ui/Hint';

const Hint = lazy(() => import('@/shared/ui/Hint').then((module) => ({ default: module.Hint })));

/**
 * `Hint` для каркаса (меню), підвантажений окремим чанком.
 *
 * ⛔ Не статичний імпорт: каркас — у графі КОЖНОГО маршруту, і підказка там
 * додавала ~0.8 КБ gzip до `DocumentPage`/`PeriodsPage` і 1.5 КБ до
 * `PipelinePage` (бюджет `D-132`). Поки чанк їде, тригер рендериться сам —
 * доступне ім'я в нього власне (`aria-label`), бракує лише видимої підказки.
 */
export function LazyHint({
  label,
  position,
  children,
}: {
  label: string;
  position: FloatingPosition;
  children: ComponentProps<typeof HintComponent>['children'];
}): JSX.Element {
  return (
    <Suspense fallback={children}>
      <Hint label={label} position={position}>
        {children}
      </Hint>
    </Suspense>
  );
}
