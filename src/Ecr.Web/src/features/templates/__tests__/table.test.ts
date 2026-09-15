import { describe, it, expect } from 'vitest';
import { emptyDraft, whyCannotSave } from '../table';

/**
 * Аудит-пас 8, lane7, п.10: «Add table» казало «дай коду код» навіть ПІСЛЯ
 * введення коду, якщо той містив недопустимі символи (`[`, `]`, `-`) —
 * `CodeEmpty` і `CodeInvalid` були ОДНИМ значенням блокувальника (`'Code'`).
 *
 * ⛔ Мутаційний доказ: повернення `whyCannotSave` для порожнього коду і для
 * недопустимого коду мають РІЗНИТИСЯ. Злиття їх назад в одне значення (як
 * було до фіксу) зробить перший `expect` нижче червоним — обидва виклики
 * повернуть те саме.
 */
describe('whyCannotSave (TableDraft)', () => {
  it('порожній код і недопустимий код — РІЗНІ причини', () => {
    const empty = whyCannotSave({ ...emptyDraft(1), code: '' });
    const invalid = whyCannotSave({ ...emptyDraft(1), code: 'ab-[cd]' });

    expect(empty).toBe('CodeEmpty');
    expect(invalid).toBe('CodeInvalid');
    expect(empty).not.toBe(invalid);
  });

  it('код лише з пробілів — теж «порожній», а не «недопустимий»', () => {
    expect(whyCannotSave({ ...emptyDraft(1), code: '   ' })).toBe('CodeEmpty');
  });

  it('код, що починається з цифри — недопустимий', () => {
    expect(whyCannotSave({ ...emptyDraft(1), code: '1abc' })).toBe('CodeInvalid');
  });

  it('чинний код і порожня назва — блокує назва, а не код', () => {
    expect(whyCannotSave({ ...emptyDraft(1), code: 'VALID_CODE' })).toBe('Name');
  });

  it('чинний код і назва — нічого не блокує', () => {
    expect(
      whyCannotSave({ ...emptyDraft(1), code: 'VALID_CODE', nameL10n: { en: 'Table' } }),
    ).toBeNull();
  });
});
