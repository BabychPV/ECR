import { describe, it, expect } from 'vitest';
import { createUserBody } from '@/features/security/createUserBody';

/**
 * Тіло запиту на створення користувача.
 *
 * ⛔ Три дефекти жили саме тут, і всі три мовчазні. Перевіряється **склад
 * тіла** — те, що побачить сервер, — а не «форма відкрилася».
 */
describe('Тіло запиту на створення користувача', () => {
  const local = {
    userName: '  ivanov  ',
    displayName: 'Іванов',
    provider: 'Local',
    sid: '',

    // Значення-заповнювач: тест перевіряє, що поле ДОЇЖДЖАЄ до сервера, а не
    // якийсь конкретний пароль.
    oneTimePassword: 'one-time-placeholder',
    email: ' ivanov@ncoc.kz ',
    roleCodes: ['DataEntry'],
  };

  it('A7-60: разовий пароль локального запису йде в запиті', () => {
    // ⛔ До виправлення тут стояв жорсткий `null`, а обробник вимагає пароль
    // для локального запису: вибір «Local» + «Зберегти» давав 422 ЗАВЖДИ.
    expect(createUserBody(local).initialPassword).toBe('one-time-placeholder');
  });

  it('A7-60: доменний запис пароля не несе', () => {
    // ⚠ Пароль доменного користувача живе в каталозі; друга його копія тут
    // була б і зайвою, і небезпечною.
    const domain = { ...local, provider: 'Windows', sid: 'S-1-5-21-1' };

    expect(createUserBody(domain).initialPassword).toBeNull();
    expect(createUserBody(domain).sid).toBe('S-1-5-21-1');
  });

  it('A7-61: ролі йдуть у запиті', () => {
    // ⛔ `null` тут означав «створити без жодного права», а призначити роль
    // потім не було чим.
    expect(createUserBody(local).roleCodes).toEqual(['DataEntry']);
  });

  it('A7-62: адреса йде в запиті й обрізається від пробілів', () => {
    // ⛔ Без адреси `NotificationJob` не має кому надсилати.
    expect(createUserBody(local).email).toBe('ivanov@ncoc.kz');
  });

  it('порожні поля стають null, а не порожніми рядками', () => {
    // ⚠ Порожній рядок і «не задано» — різні стани. Перший пройшов би
    // перевірку «пошта є» і дав би адресата, якому нічого не надсилається.
    const bare = { ...local, email: '   ', roleCodes: [], displayName: '  ' };
    const body = createUserBody(bare);

    expect(body.email).toBeNull();
    expect(body.roleCodes).toBeNull();
    expect(body.displayName).toBeNull();
    expect(body.userName).toBe('ivanov');
  });
});
