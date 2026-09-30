import { lazy, Suspense, useState, type ComponentProps, type JSX } from 'react';
import type { ConfirmModal as ConfirmModalImpl } from '@/shared/ui/ConfirmModal';
import type { ReasonModal as ReasonModalImpl } from '@/shared/ui/ReasonModal';

/**
 * Діалоги підтвердження для екрана документа, що вантажаться при ПЕРШОМУ
 * відкритті, а не разом зі сторінкою (бюджет D-132: `DocumentPage` стоїть
 * упритул до 250 КБ gzip).
 *
 * ⚠ Поведінка та сама: діалог монтується, щойно `opened` став `true`, і
 * лишається змонтованим після закриття, тож анімація закриття й стан полів
 * `Modal` працюють як раніше. Різниця лише в першому відкритті: чанк (~2 КБ)
 * довантажується, доки діалог не намальований.
 *
 * ⛔ Обгортки тут, а не в `shared/ui/**`: там самі діалоги, і їхній статичний
 * імпорт потрібен усім іншим сторінкам, які платять за нього у своєму чанку.
 */
const ConfirmModalChunk = lazy(async () => ({
  default: (await import('@/shared/ui/ConfirmModal')).ConfirmModal,
}));

const ReasonModalChunk = lazy(async () => ({
  default: (await import('@/shared/ui/ReasonModal')).ReasonModal,
}));

/** `true` від першого відкриття й назавжди (до розмонтування власника). */
function useEverOpened(opened: boolean): boolean {
  const [ever, setEver] = useState(opened);
  if (opened && !ever) setEver(true);

  return ever || opened;
}

export function LazyConfirmModal(props: ComponentProps<typeof ConfirmModalImpl>): JSX.Element | null {
  const mounted = useEverOpened(props.opened);
  if (!mounted) return null;

  return (
    <Suspense fallback={null}>
      <ConfirmModalChunk {...props} />
    </Suspense>
  );
}

export function LazyReasonModal(props: ComponentProps<typeof ReasonModalImpl>): JSX.Element | null {
  const mounted = useEverOpened(props.opened);
  if (!mounted) return null;

  return (
    <Suspense fallback={null}>
      <ReasonModalChunk {...props} />
    </Suspense>
  );
}
