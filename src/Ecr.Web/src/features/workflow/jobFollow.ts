/**
 * Як часто питати стан фонової задачі.
 *
 * ⚠ Те саме число, що й у `ExportButton.tsx`: обидві задачі однакової
 * природи — фонові, з прогресом, за яким стежить той самий екран.
 */
export const PollMs = 1500;

/**
 * Що показати оператору, коли стеження закінчилося.
 *
 * `partial` — задача-розклад (P4 ФВ-9.8): усі дочірні завершились, але частина з
 * помилками (`effectiveState = SucceededWithErrors`). Це не «виконано».
 */
export type JobOutcome = 'running' | 'succeeded' | 'partial' | 'failed' | 'unknown';

/** Підсумок задачі без розкладу: `partial` у ній не буває. */
export type PlainJobOutcome = Exclude<JobOutcome, 'partial'>;

/**
 * Похідний стан батька-розкладу: дочірні ще рахуються (`FanOutStatus.StateFannedOut`).
 *
 * ⛔ Збережений стан такого батька — вже `Succeeded`: він лише РОЗКЛАВ документні
 * задачі. Хто дивиться лише на `state`, пише «перерахунок завершено», коли не
 * пораховано ще жодного документа.
 */
export const FannedOut = 'FannedOut';

/** Похідний стан: усі дочірні завершились, частина з помилками. */
export const SucceededWithErrors = 'SucceededWithErrors';

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
 * @param effectiveState Похідний стан розкладу (`JobStatus.effectiveState`): поки
 *   `FannedOut`, опитування триває, хоча `state` уже `Succeeded`.
 * @returns Інтервал у мілісекундах або `false` — більше не питати.
 */
export function pollInterval(state: string | undefined, effectiveState?: string | null): number | false {
  return state === 'Queued' || state === 'Running' || effectiveState === FannedOut ? PollMs : false;
}

/**
 * Підсумок стеження за задачею.
 *
 * @param state Стан задачі; `undefined` — відповіді ще немає.
 * @param unreadable Стан прочитати не вдалося.
 * @param effectiveState Похідний стан розкладу (`JobStatus.effectiveState`);
 *   `null`/`undefined` — задача без дочірніх, вирішує `state`.
 *
 * @remarks
 * ⛔ «Стан прочитати не вдалося» — це НЕ «ще виконується».
 * `GET /jobs/{id}` вимагає окремого права (`System.ViewHealth`, `Q-156`), і
 * без нього кнопка крутилася б вічно: оператор бачив би «перераховується» на
 * задачі, стан якої йому просто не показують.
 */
export function outcomeOf(state: string | undefined, unreadable: boolean): PlainJobOutcome;
export function outcomeOf(
  state: string | undefined,
  unreadable: boolean,
  effectiveState: string | null | undefined,
): JobOutcome;
export function outcomeOf(
  state: string | undefined,
  unreadable: boolean,
  effectiveState?: string | null,
): JobOutcome {
  if (unreadable) {
    return 'unknown';
  }

  if (state === undefined || state === 'Queued' || state === 'Running') {
    return 'running';
  }

  if (state !== 'Succeeded') {
    return 'failed';
  }

  // ⛔ «Завершено» — лише коли виконано M = N і помилок K = 0.
  if (effectiveState === FannedOut) {
    return 'running';
  }

  return effectiveState === SucceededWithErrors ? 'partial' : 'succeeded';
}

/**
 * Стан для бейджа задачі: похідний стан розкладу, коли він є.
 *
 * ⛔ Батько-розклад (P4) зберігає `Succeeded` одразу після розкладу; бейдж за
 * `state` писав би «успішно» над документами, яких ще не пораховано.
 */
export function badgeStateOf(status: { readonly state: string; readonly effectiveState?: string | null }): string {
  return status.effectiveState ?? status.state;
}
