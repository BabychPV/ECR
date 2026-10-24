import { Suspense, lazy, type ComponentProps, type JSX } from 'react';
import type { ConfirmModal } from '@/shared/ui/ConfirmModal';
import type { ReasonModal } from '@/shared/ui/ReasonModal';

/*
 * Підтвердження «Activate»/«Recalculate» і причина «Make current» на сторінці періодів — лінивими чанками
 * (D-132, бюджет маршруту `PeriodsPage`): `ConfirmModal` тягне `List`, а потрібні обидва діалоги лише після кліку.
 *
 * ⚠ Обгортки змонтовані ЗАВЖДИ, як і статичні діалоги до них — НЕ `features/workflow/lazyDialogs.tsx`, що монтує
 * діалог з першого відкриття: закрита Mantine `Modal` нічого не малює, тож розмітка сторінки та сама, чанк
 * вантажиться разом зі сторінкою, а повернення фокуса на кнопку після закриття працює, як і було (Mantine
 * запам'ятовує відкривача на переході `opened` false → true).
 */
const ConfirmModalChunk = lazy(async () => ({
  default: (await import('@/shared/ui/ConfirmModal')).ConfirmModal,
}));

const ReasonModalChunk = lazy(async () => ({
  default: (await import('@/shared/ui/ReasonModal')).ReasonModal,
}));

/** `ConfirmModal` лінивим чанком, змонтований завжди. */
export function MountedLazyConfirmModal(props: ComponentProps<typeof ConfirmModal>): JSX.Element {
  return (
    <Suspense fallback={null}>
      <ConfirmModalChunk {...props} />
    </Suspense>
  );
}

/** `ReasonModal` лінивим чанком, змонтований завжди. */
export function MountedLazyReasonModal(props: ComponentProps<typeof ReasonModal>): JSX.Element {
  return (
    <Suspense fallback={null}>
      <ReasonModalChunk {...props} />
    </Suspense>
  );
}
