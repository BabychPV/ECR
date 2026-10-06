import { afterEach, describe, expect, it, vi } from 'vitest';
import { useState, type JSX } from 'react';
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { formatDateOnly } from '@/shared/format';
import { StrictDateInput } from '@/shared/dates/StrictDateInput';

/**
 * A2-09 (= A1-20): календар поля дати шапки перекривав кнопку «Save» і після введення дати, і після
 * Enter — закривався лише виходом із поля. Тепер календар закривається вибором дня, Enter і Escape,
 * а фокус лишається на полі (відкривачі), тож наступний Tab веде одразу до наступного елемента.
 *
 * ⚠ Наявність календаря перевіряємо за `[data-dates-dropdown]` — атрибутом випадного блоку
 * `@mantine/dates`; закритий `Popover` блоку в DOM не лишає.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  language: () => 'en',
}));

afterEach(() => cleanup());

function Harness({ onValue }: { onValue: (value: string | null) => void }): JSX.Element {
  const [value, setValue] = useState<Date | null>(new Date('2026-10-05T00:00:00'));

  return (
    <>
      <StrictDateInput
        label="Date"
        clearable
        value={value}
        onChange={(next) => {
          setValue(next);
          onValue(next === null ? null : formatDateOnly(next));
        }}
      />
      <button type="button">Save</button>
    </>
  );
}

function input(): HTMLInputElement {
  return screen.getByLabelText('Date');
}

function dropdownOpen(): boolean {
  return document.querySelector('[data-dates-dropdown]') !== null;
}

/** Відкриття й закриття `Popover` іде через перехід — чекаємо, поки стан встановиться. */
async function expectOpen(open: boolean): Promise<void> {
  await waitFor(() => expect(dropdownOpen()).toBe(open));
}

function openByFocus(): void {
  input().focus();
  fireEvent.focus(input());
  fireEvent.click(input());
}

describe('StrictDateInput: календар не перекриває форму (A2-09)', () => {
  it('набрана дата + Enter — календар закривається, значення прийняте, фокус на полі', async () => {
    const onValue = vi.fn();
    renderWithMantine(<Harness onValue={onValue} />);

    openByFocus();
    fireEvent.change(input(), { target: { value: '2026-10-07' } });
    await expectOpen(true);

    fireEvent.keyDown(input(), { key: 'Enter' });
    await expectOpen(false);

    expect(onValue).toHaveBeenLastCalledWith('2026-10-07');
    expect(document.activeElement).toBe(input());
  });

  it('вибір дня в календарі — календар закривається, фокус на полі', async () => {
    const onValue = vi.fn();
    renderWithMantine(<Harness onValue={onValue} />);

    openByFocus();
    await expectOpen(true);

    const day = screen.getByRole('button', { name: /12 October 2026/i });
    fireEvent.mouseDown(day);
    fireEvent.click(day);
    await expectOpen(false);

    expect(onValue).toHaveBeenLastCalledWith('2026-10-12');
    expect(document.activeElement).toBe(input());
  });

  it('Escape — календар закривається, значення не змінюється, фокус на полі', async () => {
    const onValue = vi.fn();
    renderWithMantine(<Harness onValue={onValue} />);

    openByFocus();
    await expectOpen(true);

    fireEvent.keyDown(input(), { key: 'Escape' });
    await expectOpen(false);

    expect(onValue).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(input());
  });

  it('після закриття Enter набір знову відкриває календар', async () => {
    renderWithMantine(<Harness onValue={vi.fn()} />);

    openByFocus();
    await expectOpen(true);
    fireEvent.keyDown(input(), { key: 'Enter' });
    await expectOpen(false);

    fireEvent.change(input(), { target: { value: '2026-10-0' } });
    await expectOpen(true);
  });

  it('після закриття Enter клік по полю знову відкриває календар', async () => {
    renderWithMantine(<Harness onValue={vi.fn()} />);

    openByFocus();
    await expectOpen(true);
    fireEvent.keyDown(input(), { key: 'Enter' });
    await expectOpen(false);

    fireEvent.click(input());
    await expectOpen(true);
  });
});
