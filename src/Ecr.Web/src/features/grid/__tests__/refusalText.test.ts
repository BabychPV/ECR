import { describe, expect, it } from 'vitest';
import { EcrApiError, type EcrProblem } from '@/api/client';
import { refusalText } from '../saveErrors';

function problem(overrides: Partial<EcrProblem> = {}): EcrProblem {
  return {
    title: 'Unit is in use',
    status: 409,
    detail: 'Одиниця використовується в 3 колонках.',
    errorCode: 'ECR-REG-0409',
    correlationId: 'c1',
    ...overrides,
  };
}

describe('refusalText (ФВ-14.9a)', () => {
  it('подробиця з messageKey — локалізована сервером, показується', () => {
    const error = new EcrApiError(
      problem({ detail: 'Unit is used by 3 columns.', extensions2: { messageKey: 'err.ECR-REG-0409.entryReferenced' } }),
    );

    expect(refusalText(error)).toBe('Unit is used by 3 columns.');
  });

  it('подробиця без messageKey — сире речення ховається, лишається назва', () => {
    // ⛔ Мутація «`return error.message`» повертає тут українське речення.
    expect(refusalText(new EcrApiError(problem()))).toBe('Unit is in use');
  });

  it('не наша відмова — не `String(error)`, а назва з каталогу', () => {
    expect(refusalText(new TypeError('Failed to fetch'))).not.toContain('Failed to fetch');
  });
});
