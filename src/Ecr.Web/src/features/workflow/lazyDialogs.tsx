import { lazy, Suspense, useState, type ComponentProps, type JSX } from 'react';
import type { ConfirmModal as ConfirmModalImpl } from '@/shared/ui/ConfirmModal';
import type { ReasonModal as ReasonModalImpl } from '@/shared/ui/ReasonModal';
import type { SourceImpactModal as SourceImpactModalImpl } from './SourceImpactModal';

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

/**
 * ⛔ B145: закритий діалог не підтверджує. `Modal` після `opened = false` ще ~200 мс лишається в DOM
 * (анімація закриття), і кнопка підтвердження далі викликала `onConfirm`. Власник у цю мить дію вже
 * завершив і зайнятість зняв (`useSettledAction`, `isPending`), тож клік по діалогу, що закривається,
 * запускав дію ВДРУГЕ: зонд дав 5 `POST …/submit` замість одного. Людині досить подвійного кліку,
 * повільнішого за відповідь сервера.
 *
 * ⚠ Тут, а не у власниках: відкритість знає сам діалог, а умова одна для всіх, хто ним користується
 * (подання, погодження, повернення, видалення документа/рядка/копії аркуша).
 */
function ignore(): void {}

export function LazyConfirmModal(props: ComponentProps<typeof ConfirmModalImpl>): JSX.Element | null {
  const mounted = useEverOpened(props.opened);
  if (!mounted) return null;

  return (
    <Suspense fallback={null}>
      <ConfirmModalChunk {...props} onConfirm={props.opened ? props.onConfirm : ignore} />
    </Suspense>
  );
}

export function LazyReasonModal(props: ComponentProps<typeof ReasonModalImpl>): JSX.Element | null {
  const mounted = useEverOpened(props.opened);
  if (!mounted) return null;

  return (
    <Suspense fallback={null}>
      <ReasonModalChunk {...props} onConfirm={props.opened ? props.onConfirm : ignore} />
    </Suspense>
  );
}

const SourceImpactModalChunk = lazy(async () => ({
  default: (await import('./SourceImpactModal')).SourceImpactModal,
}));

/**
 * ✎ B80 N5 (D-554): діалог впливу дії над джерелом на залежні документи — окремим чанком (бюджет `D-132`): потрібен лише
 * коли сервер каже, що вплив є. Закритий діалог не підтверджує (B145), як у решти.
 */
export function LazySourceImpactModal(props: ComponentProps<typeof SourceImpactModalImpl>): JSX.Element | null {
  const mounted = useEverOpened(props.opened);
  if (!mounted) return null;

  return (
    <Suspense fallback={null}>
      <SourceImpactModalChunk {...props} onConfirm={props.opened ? props.onConfirm : ignore} />
    </Suspense>
  );
}
