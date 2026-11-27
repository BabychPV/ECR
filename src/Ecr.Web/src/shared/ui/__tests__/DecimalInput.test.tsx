import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { useState, type JSX } from 'react';
import { DefaultLanguage, setLanguage } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { DecimalInput } from '../DecimalInput';
import { DecimalText } from '../DecimalText';

/**
 * Поле й текст десяткового (людина 25.11, LIVE): без фокуса — читабельне подання з точним у підказці, на фокусі — повний
 * збережений рядок; незмінене значення не перетворюється.
 *
 * ⛔ Доказ: показувати `readable.text` і на фокусі → «на фокусі повний рядок» червоний; писати в `onValueChange`
 * подання на blur → «незмінене значення» червоний.
 */

beforeEach(() => setLanguage('en'));
afterEach(() => {
  cleanup();
  setLanguage(DefaultLanguage);
  localStorage.clear();
});

function Harness({ initial, spy }: { initial: string; spy: (v: string) => void }): JSX.Element {
  const [value, setValue] = useState(initial);
  return (
    <MantineProvider theme={testTheme}>
      <DecimalInput
        label="T, °C"
        value={value}
        onValueChange={(next) => {
          spy(next);
          setValue(next);
        }}
      />
      <output data-testid="stored">{value}</output>
    </MantineProvider>
  );
}

describe('DecimalInput', () => {
  it('без фокуса — читабельне подання, точне в підказці', () => {
    render(<Harness initial="98.0835451945116000" spy={vi.fn()} />);
    const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'T, °C' });
    expect(input.value).toBe('98.08355');
    expect(input.title).toBe('98.0835451945116');
    expect(input.getAttribute('inputmode')).toBe('decimal');
  });

  it('на фокусі повний рядок; blur без правки не змінює збережене значення', () => {
    const spy = vi.fn();
    render(<Harness initial="0.0000000000004013000" spy={spy} />);
    const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'T, °C' });
    expect(input.value).toBe('0.0000000000004013');

    fireEvent.focus(input);
    expect(input.value).toBe('0.0000000000004013000');
    fireEvent.blur(input);

    expect(spy).not.toHaveBeenCalled();
    expect(screen.getByTestId('stored').textContent).toBe('0.0000000000004013000');
    expect(input.value).toBe('0.0000000000004013');
  });

  // AC-2 (картка number-format-consistent). ⛔ Доказ: прибрати розгортання на blur → «1e-9» червоний; повернути
  // науковий запис/групування в полі → «без експоненти й розділювача» червоний.
  it('AC-2: без фокуса — без експоненти й без розділювача тисяч', () => {
    render(<Harness initial="75435.9612345" spy={vi.fn()} />);
    const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'T, °C' });
    expect(input.value).toBe('75435.96');
    expect(input.title).toBe('75435.9612345');
    cleanup();
    render(<Harness initial="0.000000001397" spy={vi.fn()} />);
    expect(screen.getByRole<HTMLInputElement>('textbox', { name: 'T, °C' }).value).toBe('0.000000001397');
  });

  it('AC-2: набране «1e-9» приймається — на blur те саме значення звичайним десятковим', () => {
    const spy = vi.fn();
    render(<Harness initial="" spy={spy} />);
    const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'T, °C' });
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: '1e-9' } });
    fireEvent.blur(input);
    expect(spy).toHaveBeenLastCalledWith('0.000000001');
    expect(screen.getByTestId('stored').textContent).toBe('0.000000001');
    expect(input.value).toBe('0.000000001');
  });

  it('на фокусі новий текст уже в полі й виділений цілком — набране замінює його, а не зливається', () => {
    render(<Harness initial="97.0000000000000000" spy={vi.fn()} />);
    const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'T, °C' });
    expect(input.value).toBe('97');
    input.focus();
    expect(input.value).toBe('97.0000000000000000');
    expect([input.selectionStart, input.selectionEnd]).toEqual([0, input.value.length]);
  });

  it('правка йде як набрано; нечислове показується як є', () => {
    const spy = vi.fn();
    render(<Harness initial="1" spy={spy} />);
    const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'T, °C' });
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: '1.2345678901' } });
    expect(spy).toHaveBeenLastCalledWith('1.2345678901');
    fireEvent.change(input, { target: { value: '1,2x' } });
    fireEvent.blur(input);
    expect(input.value).toBe('1,2x');
  });
});

describe('DecimalText', () => {
  it('читабельне подання з точним у title; порожнє — тире', () => {
    render(
      <MantineProvider theme={testTheme}>
        <DecimalText value="5.231988885816670" data-testid="a" />
        <DecimalText value="2.5" data-testid="b" />
        <DecimalText value={null} data-testid="c" />
      </MantineProvider>,
    );
    expect(screen.getByTestId('a').textContent).toBe('5.231989');
    expect(screen.getByTestId('a').getAttribute('title')).toBe('5.23198888581667');
    expect(screen.getByTestId('b').getAttribute('title')).toBeNull();
    expect(screen.getByTestId('c').textContent).toBe('—');
  });
});
