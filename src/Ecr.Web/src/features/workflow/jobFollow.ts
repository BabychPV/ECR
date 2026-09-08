/**
 * Як часто питати стан фонової задачі.
 *
 * ⚠ Те саме число, що й у `ExportButton.tsx`: обидві задачі однакової
 * природи — фонові, з прогресом, за яким стежить той самий екран.
 */
export const PollMs = 1500;

/** Що показати оператору, коли стеження закінчилося. */
export type JobOutcome = 'running' | 'succeeded' | 'failed' | 'unknown';

/**
 * Чи опитувати задачу далі і як скоро.
 *
 * ⛔ Правило винесене з компонента, бо саме його не було: «Перерахувати»
 * показувало один тост «поставлено в чергу як `4f2c…`» і забувало про задачу
 * назавжди (директива №09 `W8` п.7). Оператор отримував GUID і не дізнавався
 * ні коли числа оновилися, ні що задача впала. Експорт цей самий дефект уже
 * пройшов (`ExportButton.tsx`), і прийом тут той самий.
 *
 * ⚠ Опитування ЗУПИНЯЄТЬСЯ на кінцевому стані: нескінченне опитування готового
 * результату — це запит на секунду від кожної відкритої вкладки.
 *
 * @param state Стан задачі; `undefined` — відповіді ще немає.
 * @returns Інтервал у мілісекундах або `false` — більше не питати.
 */
export function pollInterval(state: string | undefined): number | false {
  return state === 'Queued' || state === 'Running' ? PollMs : false;
}

/**
 * Підсумок стеження за задачею.
 *
 * @param state Стан задачі; `undefined` — відповіді ще немає.
 * @param unreadable Стан прочитати не вдалося.
 *
 * @remarks
 * ⛔ «Стан прочитати не вдалося» — це НЕ «ще виконується».
 * `GET /jobs/{id}` вимагає окремого права (`System.ViewHealth`, `Q-154`), і
 * без нього кнопка крутилася б вічно: оператор бачив би «перераховується» на
 * задачі, стан якої йому просто не показують.
 */
export function outcomeOf(state: string | undefined, unreadable: boolean): JobOutcome {
  if (unreadable) {
    return 'unknown';
  }

  if (state === undefined || state === 'Queued' || state === 'Running') {
    return 'running';
  }

  return state === 'Succeeded' ? 'succeeded' : 'failed';
}
