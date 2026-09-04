import { describe, it, expect } from 'vitest';

/**
 * Вставка з буфера Excel — критерій FQ-1 №3 і найчастіша причина, з якої
 * grid-бібліотека не підходить.
 */
describe('Вставка з буфера Excel', () => {
  it('розбирає багатоклітинний буфер із табуляціями і переносами рядків', () => {
    expect.fail('not implemented');
  });

  it('розпізнає десяткову кому в локалі користувача', () => {
    expect.fail('not implemented');
  });

  it('вставка 500×60 не блокує UI довше за 200 мс', () => {
    expect.fail('not implemented');
  });

  it('вставка у read-only комірки відхиляє ВЕСЬ батч і показує перелік заборонених', () => {
    expect.fail('not implemented');
  });

  it('копіювання у буфер дає формат, який приймає Excel', () => {
    expect.fail('not implemented');
  });
});
