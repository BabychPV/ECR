import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { JSX } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { Checkbox, MantineProvider, TextInput } from '@mantine/core';
import { FilterBar, FilterInline, FilterRow } from '@/shared/ui/FilterBar';
import { testTheme } from '@/test/render';

/**
 * `FilterRow` / `FilterInline` — одна будова ряду фільтрів на весь застосунок.
 *
 * ⛔ Перевіряється сама умова вирівнювання: ряд ставить нижні межі полів на
 * одну лінію (`align-items: flex-end`), а коробка `FilterInline` має РІВНО
 * висоту поля свого ряду — тоді прапорець стоїть на середній лінії поля, а не
 * на його підлозі («Late edits only», «Clear filters», «Unresolved only» —
 * живий стенд, 2026-10-06). Висота, яка не збігається з полем, — це рівно той
 * дефект, тож вона й порівнюється з тим самим числом, що в Mantine
 * (`--input-height-xs` = 1.875rem, `--input-height-sm` = 2.25rem).
 */

function show(node: JSX.Element): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/list?q=x']}>{node}</MemoryRouter>
    </MantineProvider>,
  );
}

/** Висота коробки `FilterInline`, у якій лежить вузол. */
function inlineHeight(node: HTMLElement): string {
  const box = node.closest<HTMLElement>('[data-filter-inline]');
  expect(box, 'вузол не в FilterInline').not.toBeNull();

  return box?.style.height ?? '';
}

afterEach(cleanup);

describe('FilterRow', () => {
  it('ряд вирівнює за нижньою межею, коробка FilterInline — висотою поля xs', () => {
    show(
      <FilterRow data-testid="row">
        <TextInput size="xs" label="Rule" />
        <FilterInline>
          <Checkbox label="Unresolved only" />
        </FilterInline>
      </FilterRow>,
    );

    const row = screen.getByTestId('row');
    expect(row.style.getPropertyValue('--group-align')).toBe('flex-end');
    expect(row.getAttribute('data-filter-row')).toBe('xs');
    expect(inlineHeight(screen.getByLabelText('Unresolved only'))).toContain('1.875rem');
  });

  it('висота коробки йде за розміром ряду (sm), а не стала', () => {
    show(
      <FilterRow size="sm">
        <TextInput label="Job id" />
        <FilterInline>
          <Checkbox label="Only my jobs" />
        </FilterInline>
      </FilterRow>,
    );

    expect(inlineHeight(screen.getByLabelText('Only my jobs'))).toContain('2.25rem');
  });

  it('кнопка скидання FilterBar стоїть у коробці висотою поля його ряду (sm)', () => {
    show(<FilterBar search={{ label: 'Search' }} clearLabel="Clear filters" />);

    expect(inlineHeight(screen.getByRole('button', { name: 'Clear filters' }))).toContain('2.25rem');
  });
});
