import { describe, it, expect } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider, NumberInput } from '@mantine/core';
import { theme } from '../theme';

/** Стрілки NumberInput не мають імені (a11y) — тема їх не малює; ввід працює. */
describe('NumberInput: без безіменних стрілок', () => {
  it('тема ховає кнопки data-direction, поле вводу лишається', () => {
    const { container } = render(
      <MantineProvider theme={theme}>
        <NumberInput label="N" defaultValue={1} />
      </MantineProvider>,
    );

    expect(container.querySelector('[data-direction]')).toBeNull();
    const input = screen.getByRole('textbox', { name: 'N' });
    fireEvent.change(input, { target: { value: '5' } });
    expect((input as HTMLInputElement).value).toBe('5');
  });

  /*
   * D10: прибрані ▲▼ (`a1afe830`) були мишачим дублем клавіатури. Крок значення лишається
   * лише клавішами ↑/↓ (`withKeyboardEvents`) — якщо тема чи наступне оновлення Mantine його
   * вимкне, числове поле втратить крок узагалі.
   */
  it('крок значення клавішами ↑/↓ працює за темою (заміна прибраних стрілок)', () => {
    render(
      <MantineProvider theme={theme}>
        <NumberInput label="N" defaultValue={1} min={0} max={3} />
      </MantineProvider>,
    );

    const input = screen.getByRole('textbox', { name: 'N' }) as HTMLInputElement;
    input.focus();

    fireEvent.keyDown(input, { key: 'ArrowUp' });
    expect(input.value).toBe('2');

    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    expect(input.value).toBe('0');

    // Межа `min` тримається і з клавіатури.
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    expect(input.value).toBe('0');
  });
});