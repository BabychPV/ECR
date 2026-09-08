import { describe, it, expect } from 'vitest';
import { PollMs, outcomeOf, pollInterval } from '../jobFollow';

/**
 * Стеження за фоновою задачею (директива №09 `W8` п.7).
 *
 * ⛔ Цього не було зовсім: «Перерахувати» показувало один тост «поставлено в
 * чергу як `4f2c…`» і забувало про задачу назавжди. Оператор отримував GUID і
 * не дізнавався ні коли числа оновилися, ні що задача впала — при тому, що
 * `GET /api/v1/jobs/{id}` існував, а експорт цей самий дефект уже пройшов
 * (`ExportButton.tsx:60-73`).
 */
describe('стеження за фоновою задачею', () => {
  it('незавершену задачу опитує далі', () => {
    expect(pollInterval('Queued')).toBe(PollMs);
    expect(pollInterval('Running')).toBe(PollMs);
  });

  it('на кінцевому стані опитування ЗУПИНЯЄТЬСЯ', () => {
    // ⚠ Інакше готова задача опитувалася б вічно — запит на секунду від
    // кожної відкритої вкладки.
    expect(pollInterval('Succeeded')).toBe(false);
    expect(pollInterval('Failed')).toBe(false);
    expect(pollInterval(undefined)).toBe(false);
  });

  it('розрізняє успіх, збій і «ще виконується»', () => {
    expect(outcomeOf('Queued', false)).toBe('running');
    expect(outcomeOf('Running', false)).toBe('running');
    expect(outcomeOf(undefined, false)).toBe('running');
    expect(outcomeOf('Succeeded', false)).toBe('succeeded');
    expect(outcomeOf('Failed', false)).toBe('failed');
  });

  it('«стан прочитати не вдалося» — це не «ще виконується»', () => {
    // ⛔ `GET /jobs/{id}` вимагає окремого права (`System.ViewHealth`,
    // `Q-156`). Без нього кнопка крутилася б вічно: оператор бачив би
    // «перераховується» на задачі, стан якої йому просто не показують.
    expect(outcomeOf(undefined, true)).toBe('unknown');
    expect(outcomeOf('Running', true)).toBe('unknown');
  });
});
