import { afterEach, describe, expect, it, vi } from 'vitest';
import { useState, type JSX } from 'react';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { formatDateOnly } from '@/shared/format';
import { StrictDateInput } from '@/shared/dates/StrictDateInput';

/**
 * A1-02: поле дати продукту — строгий розбір набраного тексту, відмова під полем, БЕЗ перекочування
 * і БЕЗ мовчазного очищення.
 *
 * ⚠ Мова інтерфейсу підмінена (`language()`): каталог у тестах не вантажиться, тож `t()` повертає
 * позначений ключ із підстановкою — `⟦dates.invalid (value=…)⟧`; цього досить, щоб знати, ЯКА відмова
 * показана і з яким текстом.
 */
const ui = vi.hoisted(() => ({ language: 'ru' }));

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  language: () => ui.language,
}));

afterEach(() => {
  cleanup();
  ui.language = 'ru';
});

function Harness({
  initial,
  onValue,
  onInvalid,
  maxDate,
  clearable,
}: {
  initial: string | null;
  onValue: (value: string | null) => void;
  onInvalid?: (invalid: boolean) => void;
  maxDate?: Date;
  clearable?: boolean;
}): JSX.Element {
  const [value, setValue] = useState<Date | null>(initial === null ? null : new Date(`${initial}T00:00:00`));

  return (
    <StrictDateInput
      label="Date"
      clearable={clearable === true}
      {...(maxDate === undefined ? {} : { maxDate })}
      value={value}
      onChange={(next) => {
        setValue(next);
        onValue(next === null ? null : formatDateOnly(next));
      }}
      onInvalidChange={onInvalid}
    />
  );
}

function input(): HTMLInputElement {
  return screen.getByLabelText('Date');
}

function type(text: string): void {
  fireEvent.focus(input());
  fireEvent.change(input(), { target: { value: text } });
  fireEvent.blur(input());
}

describe('StrictDateInput (A1-02)', () => {
  it.each(['ru', 'kz'])('%s: 05.10.2026 — 5 жовтня, не 10 травня; поле показує дату у форматі поля', (language) => {
    ui.language = language;
    const onValue = vi.fn();
    renderWithMantine(<Harness initial={null} onValue={onValue} />);

    type('05.10.2026');

    expect(onValue).toHaveBeenLastCalledWith('2026-10-05');
    expect(input().value).toBe('2026-10-05');
    expect(input().getAttribute('aria-invalid')).not.toBe('true');
  });

  it('2026-13-45 — НЕ 14 лютого 2027: значення не змінюється, текст лишається, під полем відмова', () => {
    const onValue = vi.fn();
    const onInvalid = vi.fn();
    renderWithMantine(<Harness initial="2026-10-01" onValue={onValue} onInvalid={onInvalid} />);

    type('2026-13-45');

    expect(onValue).not.toHaveBeenCalled();
    expect(input().value).toBe('2026-13-45');
    expect(input().getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByText('⟦dates.invalid (value=2026-13-45)⟧')).toBeTruthy();
    expect(onInvalid).toHaveBeenLastCalledWith(true);
  });

  it('нерозібраний текст у порожньому полі не стирається мовчки', () => {
    const onValue = vi.fn();
    renderWithMantine(<Harness initial={null} onValue={onValue} clearable />);

    type('31.02.2026');

    expect(onValue).not.toHaveBeenCalled();
    expect(input().value).toBe('31.02.2026');
    expect(screen.getByText('⟦dates.invalid (value=31.02.2026)⟧')).toBeTruthy();
  });

  it('en: 05.10.2026 неоднозначна — відмова; yyyy-MM-dd приймається', () => {
    ui.language = 'en';
    const onValue = vi.fn();
    renderWithMantine(<Harness initial={null} onValue={onValue} />);

    type('05.10.2026');
    expect(onValue).not.toHaveBeenCalled();
    expect(screen.getByText('⟦dates.invalid (value=05.10.2026)⟧')).toBeTruthy();

    type('2026-10-05');
    expect(onValue).toHaveBeenLastCalledWith('2026-10-05');
    expect(screen.queryByText(/dates\.invalid/)).toBeNull();
    expect(input().value).toBe('2026-10-05');
  });

  it('виправлений текст знімає відмову і повідомляє форму', () => {
    const onValue = vi.fn();
    const onInvalid = vi.fn();
    renderWithMantine(<Harness initial={null} onValue={onValue} onInvalid={onInvalid} />);

    type('99.99.2026');
    expect(onInvalid).toHaveBeenLastCalledWith(true);

    type('07.10.2026');
    expect(onValue).toHaveBeenLastCalledWith('2026-10-07');
    expect(onInvalid).toHaveBeenLastCalledWith(false);
    expect(screen.queryByText(/dates\.invalid/)).toBeNull();
  });

  it('дата поза maxDate — окрема відмова, значення не змінюється', () => {
    const onValue = vi.fn();
    renderWithMantine(<Harness initial={null} onValue={onValue} maxDate={new Date(2026, 9, 5)} />);

    type('06.10.2026');

    expect(onValue).not.toHaveBeenCalled();
    expect(input().value).toBe('06.10.2026');
    expect(screen.getByText('⟦dates.outOfRange (value=06.10.2026)⟧')).toBeTruthy();
  });

  it('очищення поля з clearable — порожнє значення, без відмови', () => {
    const onValue = vi.fn();
    renderWithMantine(<Harness initial="2026-10-01" onValue={onValue} clearable />);

    type('');

    expect(onValue).toHaveBeenLastCalledWith(null);
    expect(screen.queryByText(/dates\./)).toBeNull();
  });

  it('проміжний текст під час набору не змінює значення і не показує відмову до виходу з поля', () => {
    const onValue = vi.fn();
    renderWithMantine(<Harness initial={null} onValue={onValue} />);

    fireEvent.focus(input());
    for (const text of ['0', '05', '05.', '05.1', '05.10', '05.10.', '05.10.2', '05.10.20', '05.10.202']) {
      fireEvent.change(input(), { target: { value: text } });
    }

    expect(onValue).not.toHaveBeenCalled();
    expect(screen.queryByText(/dates\./)).toBeNull();

    fireEvent.change(input(), { target: { value: '05.10.2026' } });
    expect(onValue).toHaveBeenLastCalledWith('2026-10-05');
  });
});
