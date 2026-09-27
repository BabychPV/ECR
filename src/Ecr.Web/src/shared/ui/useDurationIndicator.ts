import { useEffect, useState } from 'react';

/**
 * Фаза індикації дії за її тривалістю (`ФВ-14.26`).
 *
 * - `none` — дія триває менше `inline` мс або не триває зовсім: нічого;
 * - `inline` — індикатор на самому елементі керування (стан кнопки);
 * - `progress` — прогрес із текстом (`DurationProgress`).
 *
 * ⚠ Четвертої фази («> 10 с — фонова задача з `jobId`», `ФВ-14.8`) тут немає
 * навмисно: її вирішує СЕРВЕР відповіддю `202` з `jobId`, а не таймер клієнта.
 * Синхронний запит, що пережив 10 с, лишається в `progress` — перевести його у
 * фон клієнт не може, бо задачі на сервері для нього немає.
 */
export type DurationPhase = 'none' | 'inline' | 'progress';

/**
 * Межі `ФВ-14.26` у мілісекундах; збігаються з бюджетами `tz/08` §8.2.
 *
 * ⛔ Єдине джерело чисел: компоненти беруть фазу з хука, а не пишуть власні
 * `setTimeout(…, 100)`, інакше межі розійдуться мовчки.
 */
export const DurationThresholdsMs = {
  /** Від цієї тривалості — індикатор на елементі. */
  inline: 100,
  /** Від цієї тривалості — прогрес із текстом. */
  progress: 1000,
  /** Від цієї тривалості — фонова задача з `jobId` (вирішує сервер). */
  background: 10_000,
} as const;

/**
 * Фаза індикації для дії, що зараз `pending`.
 *
 * ⛔ Без мерехтіння: фаза вмикається лише ПІСЛЯ свого порогу. Дія, що
 * завершилась за 80 мс, не показує нічого — навіть на один кадр. Спінер, що
 * блимнув і зник, відволікає сильніше, ніж відсутність індикації (саме тому
 * `ФВ-14.26` і має нижню межу).
 *
 * ⚠ Поки фаза `none`, елемент керування НЕ заблокований станом `loading` —
 * захист від повторного натискання лежить на споживачі (перевірка
 * `isPending` в обробнику), а не на вигляді кнопки.
 */
export function useDurationIndicator(pending: boolean): DurationPhase {
  const [phase, setPhase] = useState<DurationPhase>('none');

  useEffect(() => {
    if (!pending) return undefined;

    const toInline = setTimeout(() => setPhase('inline'), DurationThresholdsMs.inline);
    const toProgress = setTimeout(() => setPhase('progress'), DurationThresholdsMs.progress);

    return () => {
      clearTimeout(toInline);
      clearTimeout(toProgress);
      // Скидання — у прибиранні, а не в тілі ефекту: наступна дія
      // починається з `none`, а не з фази попередньої.
      setPhase('none');
    };
  }, [pending]);

  // ⚠ Завершена дія гасить індикацію в ТОМУ Ж рендері, а не після ефекту:
  // інакше між `pending: false` і прибиранням був би кадр зі спінером.
  return pending ? phase : 'none';
}
