import { afterEach, describe, expect, it, vi } from 'vitest';
import { useState, type JSX } from 'react';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { DateOnlyInput } from '@/shared/dates/DateOnlyInput';

/**
 * A1-02: поле дати з рядковим значенням `yyyy-MM-dd` — заміна рідного `<input type="date">`
 * (борг D15-09). Розбір — той самий `StrictDateInput`; тут — лише перехід рядок ↔ дата.
 */
const ui = vi.hoisted(() => ({ language: 'ru' }));

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  language: () => ui.language,
}));

afterEach(() => {
  cleanup();
});

function Harness({ initial, onValue }: { initial: string; onValue: (value: string) => void }): JSX.Element {
  const [value, setValue] = useState(initial);

  return (
    <DateOnlyInput
      label="Valid from"
      value={value}
      onChange={(next) => {
        setValue(next);
        onValue(next);
      }}
    />
  );
}

describe('DateOnlyInput (A1-02)', () => {
  it('значення з сервера показане; набране «05.10.2026» — рядок 2026-10-05', async () => {
    const onValue = vi.fn();
    renderWithMantine(<Harness initial="2026-01-15" onValue={onValue} />);

    const input = (await screen.findByLabelText('Valid from')) as HTMLInputElement;
    expect(input.value).toBe('2026-01-15');

    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: '05.10.2026' } });
    fireEvent.blur(input);

    expect(onValue).toHaveBeenLastCalledWith('2026-10-05');
  });

  it('неіснуюча дата — значення НЕ стає порожнім рядком, текст лишається з відмовою', async () => {
    const onValue = vi.fn();
    renderWithMantine(<Harness initial="2026-01-15" onValue={onValue} />);

    const input = (await screen.findByLabelText('Valid from')) as HTMLInputElement;
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: '31.02.2026' } });
    fireEvent.blur(input);

    expect(onValue).not.toHaveBeenCalled();
    expect(input.value).toBe('31.02.2026');
    expect(screen.getByText('⟦dates.invalid (value=31.02.2026)⟧')).toBeTruthy();
  });

  it('очищення — порожній рядок', async () => {
    const onValue = vi.fn();
    renderWithMantine(<Harness initial="2026-01-15" onValue={onValue} />);

    const input = (await screen.findByLabelText('Valid from')) as HTMLInputElement;
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: '' } });
    fireEvent.blur(input);

    expect(onValue).toHaveBeenLastCalledWith('');
  });
});
