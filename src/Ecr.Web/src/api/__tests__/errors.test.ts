import { describe, it, expect } from 'vitest';

/**
 * Клієнт розрізняє причини **за кодом**, а не за текстом: текст локалізований
 * і може змінюватися, код — ні.
 */
describe('Обробка помилок API', () => {
  it('розбирає EcrProblemDetails і зберігає код помилки', () => {
    expect.fail('not implemented');
  });

  it('конфлікт розпізнається за кодом і містить перелік розбіжностей', () => {
    expect.fail('not implemented');
  });

  it('401 веде на сторінку входу без спроби мовчазного повторного входу', () => {
    expect.fail('not implemented');
  });

  it('4xx не повторюється автоматично', () => {
    expect.fail('not implemented');
  });

  it('користувачеві показується текст сервера, а не власний узагальнений', () => {
    expect.fail('not implemented');
  });
});
