import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { renderWithMantine } from '@/test/render';

/**
 * «Period»: поки поле у фокусі, у ньому РІВНО набране — зовнішній `value` у
 * нього не пише.
 *
 * ⛔ Живий стенд (2026-09-24, `drop-repro.mjs`, 5 з 5): на `/` Ctrl+A, Delete і
 * набір `202608` давали в полі `202608202609`. Автовибір періоду дописав
 * `202609` у поле, в якому людина вже друкувала, — а поле його прийняло, бо
 * чернетка «застарівала» зі зміною `value`. Тут це відтворено без сторінки:
 * між натисканнями `value` змінюється ззовні (відлуння адреси `null`, потім
 * «автовибір» `202609`, потім запізніле відлуння проміжного значення).
 *
 * ⛔ Мутаційний доказ (перевірено): у `useFieldDraft.ts` замінити
 * `if (!focused) setValue(external)` на `setValue(external)` — тобто
 * повернути запис зовнішнього значення в поле під час набору — червоніють
 * обидва перші випадки (у полі `202609…`/`202512`, а не набране).
 */

afterEach(() => {
  cleanup();
});

const periodInput = (): HTMLInputElement =>
  screen.getByRole<HTMLInputElement>('textbox', { name: '⟦documents.period⟧' });

describe('PeriodPicker: зовнішній value не перебиває набір', () => {
  it('очистити → ззовні підставили 202609 → набір 202608 дає в полі рівно 202608', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    const { rerender } = renderWithMantine(<PeriodPicker value={202609} onChange={onChange} />);
    const input = periodInput();

    await user.clear(input);
    expect(onChange.mock.calls).toEqual([[null]]);

    // Відлуння адреси (`null`), а за ним — автовибір поточного періоду.
    rerender(<PeriodPicker value={null} onChange={onChange} />);
    rerender(<PeriodPicker value={202609} onChange={onChange} />);
    expect(input.value).toBe('');

    await user.type(input, '2026');
    // Запізніле зовнішнє значення посеред набору.
    rerender(<PeriodPicker value={202512} onChange={onChange} />);
    await user.type(input, '08');

    expect(input.value).toBe('202608');
    expect(onChange.mock.calls.at(-1)).toEqual([202608]);
  });

  it('повний набір, а value ще старе (адреса запізнюється) — поле лишає набране й після blur', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    const { rerender } = renderWithMantine(<PeriodPicker value={202512} onChange={onChange} />);
    const input = periodInput();

    await user.type(input, '202608', { initialSelectionStart: 0, initialSelectionEnd: 6 });
    // Відлуння ще не долетіло, зате долетіло «щось старе» ззовні.
    rerender(<PeriodPicker value={202511} onChange={onChange} />);
    expect(input.value).toBe('202608');

    fireEvent.blur(input);
    expect(input.value).toBe('202608');
    expect(onChange.mock.calls).toEqual([[202608]]);

    // А тепер адреса наздогнала — те саме значення, поле без змін.
    rerender(<PeriodPicker value={202608} onChange={onChange} />);
    expect(input.value).toBe('202608');
  });

  it('поле НЕ у фокусі — зміна value ззовні (навігація, «Назад») видна одразу', () => {
    const { rerender } = renderWithMantine(<PeriodPicker value={202609} onChange={vi.fn()} />);

    act(() => {
      rerender(<PeriodPicker value={202610} onChange={vi.fn()} />);
    });

    expect(periodInput().value).toBe('202610');
    expect(screen.getByText('October 2026')).toBeTruthy();
  });
});
