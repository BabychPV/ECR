import { useCallback, useEffect } from 'react';

/**
 * ✎ DOCUX L7: «Details / Review» повідомлення відкриває вкладку Status інспектора.
 *
 * ⚠ Без маршрутизатора: повідомлення публікують і сітки (лінивий чанк, у тестах — без `Router`), а адресу
 * (`?panel=status`) ставить сам інспектор, підписаний тут (`useStatusRequests`).
 */
const listeners = new Set<() => void>();
/** Запит прийшов, поки інспектора (лінивий чанк) ще немає, — він виконає його, щойно змонтується. */
let pending = false;

export function requestStatusTab(): void {
  if (listeners.size === 0) {
    pending = true;
    return;
  }
  for (const listener of listeners) listener();
}

/** Дія «відкрити Status» для кнопок повідомлень. */
export function useOpenStatus(): () => void {
  return useCallback(() => requestStatusTab(), []);
}

/** Інспектор: відкрити вкладку Status на запит повідомлення. */
export function useStatusRequests(open: () => void): void {
  useEffect(() => {
    listeners.add(open);
    if (pending) {
      pending = false;
      open();
    }
    return () => {
      listeners.delete(open);
    };
  }, [open]);
}
