import { describe, it, expect } from 'vitest';
import { emptyColumnDraft, whyCannotSaveColumn } from '../column';

/**
 * Той самий дефект, що й `table.test.ts` (Q-336, `table.ts`): форма колонки
 * казала «дай коду код» навіть ПІСЛЯ введення коду, якщо той містив
 * недопустимі символи (`[`, `]`, `-`) — `CodeEmpty` і `CodeInvalid` були
 * ОДНИМ значенням блокувальника (`'Code'`).
 *
 * ⛔ Мутаційний доказ: повернення `whyCannotSaveColumn` для порожнього коду і
 * для недопустимого коду мають РІЗНИТИСЯ. Злиття їх назад в одне значення (як
 * було до фіксу) зробить перший `expect` нижче червоним — обидва виклики
 * повернуть те саме.
 */
describe('whyCannotSaveColumn (ColumnDraft)', () => {
  it('порожній код і недопустимий код — РІЗНІ причини', () => {
    const empty = whyCannotSaveColumn({ ...emptyColumnDraft(1), code: '' });
    const invalid = whyCannotSaveColumn({ ...emptyColumnDraft(1), code: 'ab-[cd]' });

    expect(empty).toBe('CodeEmpty');
    expect(invalid).toBe('CodeInvalid');
    expect(empty).not.toBe(invalid);
  });

  it('код лише з пробілів — теж «порожній», а не «недопустимий»', () => {
    expect(whyCannotSaveColumn({ ...emptyColumnDraft(1), code: '   ' })).toBe('CodeEmpty');
  });

  it('код, що починається з цифри — недопустимий', () => {
    expect(whyCannotSaveColumn({ ...emptyColumnDraft(1), code: '1abc' })).toBe('CodeInvalid');
  });

  it('чинний код і порожній заголовок — блокує заголовок, а не код', () => {
    expect(whyCannotSaveColumn({ ...emptyColumnDraft(1), code: 'VALID_CODE' })).toBe('Header');
  });

  it('чинний код і заголовок — нічого не блокує', () => {
    expect(
      whyCannotSaveColumn({
        ...emptyColumnDraft(1),
        code: 'VALID_CODE',
        headerL10n: { en: 'Column' },
      }),
    ).toBeNull();
  });
});
