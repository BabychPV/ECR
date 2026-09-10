import { describe, it, expect } from 'vitest';
import {
  createProjectBody,
  createProjectIncomplete,
} from '@/features/projects/CreateProjectModal';

/**
 * Тіло запиту на створення проєкту — перший крок роботи із системою.
 *
 * ⛔ `A7-56`: кнопка була, форма відкривалася, і **жодна спроба не могла
 * пройти**. У тілі не було ані версії шаблону, ані політики періодів, а
 * сервер відхиляє створення без них (`ECR-TMPL-0404`, `ECR-PRD-0422`).
 * Сторож «кожна дія сервера має споживача» був зелений і правий: споживач
 * справді був, просто ніколи не спрацьовував.
 *
 * ⛔ `D-5`: часовий пояс надсилався мовчки з браузера конфігуратора. Він стає
 * **вічною** властивістю проєкту — після відкриття першого періоду його не
 * змінити (`ФВ-1.1a`), — а конфігуратор часто сидить не там, де майданчик.
 *
 * ⚠ Перевіряється САМЕ ТІЛО, а не «форма відкрилася»: перше — те, що побачить
 * сервер, друге не доводить нічого. Рендер Mantine у jsdom для цієї форми йде
 * понад дві хвилини, тобто такий тест однаково вимкнули б.
 */
describe('Тіло запиту на створення проєкту', () => {
  const form = {
    code: '  KASH_2026  ',
    name: { en: 'Kashagan' },
    periodKind: 'Monthly',
    timeZoneId: 'Asia/Almaty',
    versionId: '42',
    policyId: '7',
  };

  it('A7-56: містить версію шаблону і політику періодів', () => {
    const body = createProjectBody(form);

    // ⛔ Головне твердження: обох полів у тілі не було, і сервер відхиляв
    // КОЖНУ спробу створити проєкт з інтерфейсу.
    expect(body.templateVersionId).toBe(42);
    expect(body.periodPolicyId).toBe(7);
  });

  it('D-5: пояс іде саме той, що обраний у формі', () => {
    expect(createProjectBody(form).timeZoneId).toBe('Asia/Almaty');

    // ⚠ І не підмінюється поясом браузера: у формі його ЗМІНЮЮТЬ, і зміна
    // мусить доїхати до сервера.
    expect(createProjectBody({ ...form, timeZoneId: 'UTC' }).timeZoneId).toBe('UTC');
  });

  it('числа приходять числами, а не рядками', () => {
    // `Select` віддає рядок; `"42"` у полі `templateVersionId` дало б 400 ще
    // до обробника.
    const body = createProjectBody(form);

    expect(typeof body.templateVersionId).toBe('number');
    expect(typeof body.periodPolicyId).toBe('number');
  });

  it('код обрізається від пробілів', () => {
    // Код — бізнес-ключ: пробіл на кінці зробив би два різні проєкти
    // однаковими на вигляд і різними для системи.
    expect(createProjectBody(form).code).toBe('KASH_2026');
  });
});

/**
 * Кількість періодів для `Custom` (T6/#36).
 *
 * ⛔ Домен розумів `PeriodKind.Custom` — `PeriodCalendar.CountFor` приймав
 * `customCount` від самого початку, — а форма не мала звідки взяти це число:
 * запит завжди йшов без нього, і сервер відмовляв би `ECR-PRD-4224`
 * (`0` поза межами `1..12`) для БУДЬ-ЯКОГО `Custom`-проєкту.
 */
describe('Кількість періодів для Custom (T6/#36)', () => {
  const base = {
    code: 'KASH_2026',
    name: { en: 'Kashagan' },
    timeZoneId: 'Asia/Almaty',
    versionId: '42',
    policyId: '7',
  };

  it('надсилається для Custom', () => {
    const body = createProjectBody({ ...base, periodKind: 'Custom', customPeriodCount: 6 });

    expect(body.customPeriodCount).toBe(6);
  });

  it('НЕ надсилається для решти видів, навіть якщо число лишилося у формі', () => {
    // ⚠ Стан форми зберігається між перемиканнями `Select`: користувач міг
    // спершу обрати Custom і ввести число, а тоді передумати. Старе число не
    // має доїхати до сервера як властивість Monthly-проєкту.
    const body = createProjectBody({ ...base, periodKind: 'Monthly', customPeriodCount: 6 });

    expect(body.customPeriodCount).toBeNull();
  });

  it('без введеного числа Custom-форма НЕ готова', () => {
    // ⛔ Головне твердження D-134 на клієнті: замість гарантованої відмови
    // `ECR-PRD-4224` (`0` поза межами `1..12`) кнопка лишається неактивною.
    expect(createProjectIncomplete({
      ...base, periodKind: 'Custom', customPeriodCount: null,
    })).toBe(true);
  });

  it('з числом Custom-форма готова', () => {
    expect(createProjectIncomplete({
      ...base, periodKind: 'Custom', customPeriodCount: 6,
    })).toBe(false);
  });

  it('для Monthly відсутність числа не блокує форму', () => {
    expect(createProjectIncomplete({
      ...base, periodKind: 'Monthly', customPeriodCount: null,
    })).toBe(false);
  });
});

/**
 * Готовність форми до надсилання.
 *
 * ⛔ Директива ПК-1 №06 §3 скасувала `D1-09` — «пояс браузера як ПОЧАТКОВЕ
 * значення». Поле починається порожнім, і саме тому воно має бути в переліку
 * обов'язкових: доки пояс підставлявся сам, форму можна було надіслати, не
 * подивившись на нього жодного разу. Конфігуратор в Астані (`Asia/Almaty`,
 * +06:00) заводив би проєкт для Актау (`Asia/Aqtau`, +05:00), і кожен період
 * закривався б на годину раніше, ніж чекають на місці. Виправити це вже не
 * можна: після відкриття першого періоду пояс не змінюється (`ФВ-1.1a`).
 */
describe('Готовність форми створення проєкту', () => {
  const ready = {
    code: 'KASH_2026',
    name: { en: 'Kashagan' },
    timeZoneId: 'Asia/Aqtau',
    versionId: '42',
    policyId: '7',
  };

  it('заповнена форма готова', () => {
    expect(createProjectIncomplete(ready)).toBe(false);
  });

  it('без поясу форма НЕ готова', () => {
    // ⛔ Головне твердження кроку: пояс — обов'язковий і видимий, а не
    // мовчазна підстановка з браузера.
    expect(createProjectIncomplete({ ...ready, timeZoneId: null })).toBe(true);
  });

  it.each([
    ['код', { code: '   ' }],
    ['назву', { name: {} }],
    ['версію шаблону', { versionId: null }],
    ['політику періодів', { policyId: null }],
  ])('без обов’язкового поля «%s» форма НЕ готова', (_name, patch) => {
    expect(createProjectIncomplete({ ...ready, ...patch })).toBe(true);
  });
});
