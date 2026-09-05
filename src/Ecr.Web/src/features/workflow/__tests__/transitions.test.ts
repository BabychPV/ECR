import { describe, it, expect } from 'vitest';
import { AllowedIn, isAllowed, needsReason, type SheetState } from '../transitions';

/**
 * Таблиця переходів робочого процесу.
 *
 * ⚠ Тести не про те, «як влаштована таблиця», а про те, що **кнопка є там, де
 * дія працює, і немає там, де сервер відмовить**. До аудиту (`A7-39`) половини
 * цих кнопок не існувало взагалі: `Approved` був недосяжним станом, і система
 * не робила того, заради чого існує.
 */
const AllStates: readonly SheetState[] = ['Draft', 'Submitted', 'Approved', 'Rejected'];

describe('Погодження (ФВ-5.13, ФВ-5.14)', () => {
  it('ФВ-5.14: затвердити можна лише поданий аркуш', () => {
    // ⛔ `Approved` — це стан, у якому дані стають дійсними і потрапляють у
    // звіти для регулятора (`ФВ-10.11`). Дійти до нього можна ЛИШЕ з
    // `Submitted`: домен відмовляє `ECR-DOC-0409` звідусіль інде.
    expect(isAllowed('approve', 'Submitted')).toBe(true);

    for (const state of AllStates.filter((s) => s !== 'Submitted')) {
      expect(isAllowed('approve', state), state).toBe(false);
    }
  });

  it('ФВ-5.13: подати можна чернетку і відхилений, але не подане', () => {
    expect(isAllowed('submit', 'Draft')).toBe(true);

    // ⚠ Відхилений подається знову — інакше зауваження нічим виправити.
    expect(isAllowed('submit', 'Rejected')).toBe(true);

    // ⛔ Повторне подання поданого — не «нічого не змінилося», а спроба
    // перезаписати момент і автора подання, на яких тримається зріз (ФВ-5.7).
    expect(isAllowed('submit', 'Submitted')).toBe(false);
    expect(isAllowed('submit', 'Approved')).toBe(false);
  });
});

describe('Відхилення і повернення (ФВ-5.15, ФВ-5.20a)', () => {
  it('ФВ-5.15: відхилення вимагає причини', () => {
    // Відхилення без пояснення повертає роботу тому, хто не знає, що
    // виправляти, — і цикл повторюється. Домен вимагає коментаря
    // (`ECR-DOC-0422`), інтерфейс питає його ДО надсилання.
    expect(needsReason('reject')).toBe(true);
    expect(isAllowed('reject', 'Submitted')).toBe(true);
    expect(isAllowed('reject', 'Draft')).toBe(false);
  });

  it('ФВ-5.20a: повернути в роботу можна подане і затверджене', () => {
    expect(isAllowed('reopen', 'Submitted')).toBe(true);
    expect(isAllowed('reopen', 'Approved')).toBe(true);

    // ⚠ Чернетку «повертати в роботу» немає звідки: вона вже в роботі.
    expect(isAllowed('reopen', 'Draft')).toBe(false);
    expect(isAllowed('reopen', 'Rejected')).toBe(false);

    // Причина обов'язкова і тут: повторне подання створює НОВИЙ зріз, а
    // старий лишається `Submitted` назавжди (ФВ-9.17).
    expect(needsReason('reopen')).toBe(true);
  });

  it('подання і затвердження причини не потребують', () => {
    // ⚠ Зайве обов'язкове поле коштує не менше за відсутнє: щоденне подання
    // з обов'язковим коментарем перетворюється на «.» у кожному рядку аудиту.
    expect(needsReason('submit')).toBe(false);
    expect(needsReason('approve')).toBe(false);
  });
});

describe('Повнота таблиці', () => {
  it('кожна дія названа хоча б в одному стані', () => {
    // Дія з порожнім переліком станів — це кнопка, якої не буде ніколи;
    // мовчазний спосіб прибрати з інтерфейсу цілий крок процесу.
    for (const [action, states] of Object.entries(AllowedIn)) {
      expect(states.length, action).toBeGreaterThan(0);
    }
  });

  it('у стані Draft доступне рівно подання', () => {
    // ⚠ Перевірка знизу вгору: не «чи дозволена дія», а «що бачить оператор,
    // відкривши чернетку». Саме тут виявилося б, що затвердження просочилося
    // в стан, у якому воно не має сенсу.
    const inDraft = Object.keys(AllowedIn).filter((action) =>
      isAllowed(action as keyof typeof AllowedIn, 'Draft'),
    );

    expect(inDraft).toEqual(['submit']);
  });

  it('у стані Submitted доступні погодження, відхилення і повернення', () => {
    const inSubmitted = Object.keys(AllowedIn).filter((action) =>
      isAllowed(action as keyof typeof AllowedIn, 'Submitted'),
    );

    expect([...inSubmitted].sort()).toEqual(['approve', 'reject', 'reopen']);
  });
});
