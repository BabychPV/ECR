import { describe, it, expect } from 'vitest';
import { emptyValidationRuleDraft, whyCannotSaveValidationRule } from '../validationRule';

/**
 * Той самий дефект, що й `table.test.ts` (Q-336, `table.ts`): форма правила
 * валідації казала «дай коду код» навіть ПІСЛЯ введення коду, якщо той
 * містив недопустимі символи (`[`, `]`, `-`) — `CodeEmpty` і `CodeInvalid`
 * були ОДНИМ значенням блокувальника (`'Code'`).
 *
 * ⛔ Мутаційний доказ: повернення `whyCannotSaveValidationRule` для
 * порожнього коду і для недопустимого коду мають РІЗНИТИСЯ. Злиття їх назад
 * в одне значення (як було до фіксу) зробить перший `expect` нижче червоним —
 * обидва виклики повернуть те саме.
 */
describe('whyCannotSaveValidationRule (ValidationRuleDraft)', () => {
  it('порожній код і недопустимий код — РІЗНІ причини', () => {
    const empty = whyCannotSaveValidationRule({ ...emptyValidationRuleDraft(), code: '' });
    const invalid = whyCannotSaveValidationRule({
      ...emptyValidationRuleDraft(),
      code: 'ab-[cd]',
    });

    expect(empty).toBe('CodeEmpty');
    expect(invalid).toBe('CodeInvalid');
    expect(empty).not.toBe(invalid);
  });

  it('код лише з пробілів — теж «порожній», а не «недопустимий»', () => {
    expect(whyCannotSaveValidationRule({ ...emptyValidationRuleDraft(), code: '   ' })).toBe(
      'CodeEmpty',
    );
  });

  it('код, що починається з цифри — недопустимий', () => {
    expect(whyCannotSaveValidationRule({ ...emptyValidationRuleDraft(), code: '1abc' })).toBe(
      'CodeInvalid',
    );
  });

  it('чинний код і порожній вираз — блокує вираз, а не код', () => {
    expect(
      whyCannotSaveValidationRule({ ...emptyValidationRuleDraft(), code: 'VALID_CODE' }),
    ).toBe('Expression');
  });

  it('чинний код, вираз і повідомлення — нічого не блокує', () => {
    expect(
      whyCannotSaveValidationRule({
        ...emptyValidationRuleDraft(),
        code: 'VALID_CODE',
        expression: '[Amount] > 0',
        messageL10n: { en: 'Amount must be positive' },
      }),
    ).toBeNull();
  });
});
