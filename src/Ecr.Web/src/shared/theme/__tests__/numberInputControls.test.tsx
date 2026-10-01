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
});