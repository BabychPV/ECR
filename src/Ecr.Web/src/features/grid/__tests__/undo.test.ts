import { describe, it, expect } from 'vitest';

/**
 * Undo/Redo ≥50 кроків — критерій FQ-1 №6. Потребує **власної моделі
 * команд**: не всі grid-бібліотеки дозволяють її вбудувати без форку, і саме
 * це перевіряється прототипом на Етапі 0.
 */
describe('Undo/Redo', () => {
  it('скасовує останню зміну', () => {
    expect.fail('not implemented');
  });

  it('тримає щонайменше 50 кроків історії', () => {
    expect.fail('not implemented');
  });

  it('повторює скасовану зміну', () => {
    expect.fail('not implemented');
  });

  it('скидає історію при переході на іншу таблицю', () => {
    expect.fail('not implemented');
  });

  it('вставка діапазону — це ОДИН крок історії, а не сотні', () => {
    expect.fail('not implemented');
  });
});
