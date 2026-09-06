import { describe, it, expect } from 'vitest';
import { createProjectBody } from '@/features/projects/CreateProjectModal';

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
