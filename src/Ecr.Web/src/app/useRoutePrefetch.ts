import { useCallback, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { prefetchRoute } from './routePrefetch';

/**
 * Затримка наміру перед прогрівом (`PR nav-arch #5`, директива C2: «лише те,
 * на що користувач реально навів курсор/фокус», не кожен піксель проїзду
 * курсора повз пункт навбару дорогою кудись-інде).
 */
const IntentDelayMs = 150;

export interface RoutePrefetchHandlers {
  onMouseEnter: () => void;
  onMouseLeave: () => void;
  onFocus: () => void;
  onBlur: () => void;
}

/**
 * Обробники наміру (hover/focus) для прогріву чанка й даних ОДНОГО пункту
 * навбару (`PR nav-arch #5`).
 *
 * ⚠ Прогрів запускається через невеликий таймер ({@link IntentDelayMs}), не
 * миттєво на `mouseenter`: курсор, що просто ПРОЇЖДЖАЄ через пункт навбару
 * дорогою до іншого місця екрана (скрол, наведення на сусідній пункт), — не
 * намір перейти саме сюди. `onMouseLeave`/`onBlur` скасовують таймер, якщо
 * він не встиг спрацювати, — директива прямо забороняє агресивний prefetch
 * «усього підряд» (C2), а без скасування короткий проїзд курсора однаково
 * зарахувався б наміром через `IntentDelayMs`.
 *
 * ⛔ Прогрів спрацьовує НАЙБІЛЬШЕ ОДИН РАЗ на маршрут за життя компонента
 * (`triggeredRef`): TanStack Query сам дедуплікує паралельні запити на той
 * самий ключ, а `import()` кешується модульним завантажувачем, тож повторний
 * виклик нешкідливий — але заводити новий `setTimeout` на КОЖНЕ повторне
 * наведення того самого пункту, поки людина просто вагається (наприклад,
 * рухає мишею над тим самим елементом), немає сенсу.
 */
export function useRoutePrefetch(routeId: string | undefined): RoutePrefetchHandlers {
  const queryClient = useQueryClient();
  const timerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const triggeredRef = useRef(false);

  const cancel = useCallback((): void => {
    if (timerRef.current === undefined) return;

    clearTimeout(timerRef.current);
    timerRef.current = undefined;
  }, []);

  const start = useCallback((): void => {
    if (routeId === undefined || triggeredRef.current) return;

    cancel();
    timerRef.current = setTimeout(() => {
      timerRef.current = undefined;
      triggeredRef.current = true;
      prefetchRoute(routeId, queryClient);
    }, IntentDelayMs);
  }, [routeId, queryClient, cancel]);

  return { onMouseEnter: start, onMouseLeave: cancel, onFocus: start, onBlur: cancel };
}
