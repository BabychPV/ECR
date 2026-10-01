import { useEffect, useState } from 'react';
import { DurationThresholdsMs } from '@/shared/ui/useDurationIndicator';

/**
 * Стан `loading` кнопки за тривалістю дії (`ФВ-14.26`).
 *
 * ⛔ Не `loading={mutation.isPending}`: так спінер вмикається з ПЕРШОГО кадру,
 * і дія на 80 мс блимає ним — рівно те, проти чого `ФВ-14.26` має нижню межу
 * («`< 100 мс` — нічого»). Тут `loading` вмикається лише після порогу
 * `DurationThresholdsMs.inline` (одне джерело чисел з `useDurationIndicator`).
 *
 * ⚠ Один таймер, а не `useDurationIndicator` цілком: кнопці потрібна лише межа
 * «є індикатор / немає», а хук тягнув би в спільний чанк DocumentPage/
 * PeriodsPage другий таймер, якого тут ніхто не читає (бюджет `D-132`).
 *
 * ⚠ Доки порогу не досягнуто, Mantine НЕ блокує кнопку станом `loading`, тож
 * повторне натискання стримує обробник споживача (`if (x.isPending) return`).
 */
export function usePendingLoading(pending: boolean): boolean {
  const [late, setLate] = useState(false);

  useEffect(() => {
    if (!pending) return undefined;

    const timer = setTimeout(() => setLate(true), DurationThresholdsMs.inline);

    return () => {
      clearTimeout(timer);
      setLate(false);
    };
  }, [pending]);

  // Завершена дія гасить спінер у тому ж рендері, а не після ефекту.
  return pending && late;
}
