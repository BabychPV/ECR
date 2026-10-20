import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { TestProviders } from '@/test/render';
import { ColumnsTable } from '../ColumnsTable';

/**
 * PA2-U-03: прокручувана обгортка таблиці стовпців порожнього типу (на ширині 500 вона вужча за таблицю) без
 * фокусованого вмісту - axe serious `scrollable-region-focusable`. Порожня таблиця: область з іменем, що бере Tab;
 * зі стовпцями фокусовані самі рядки, область не потрібна.
 *
 * Мутаційний доказ: прибрати умовний `tabIndex`/`role` в `ColumnsTable` - червоний перший тест.
 */
afterEach(() => cleanup());

describe('ColumnsTable: область прокрутки', () => {
  it('порожня таблиця - іменована область, досяжна з клавіатури', () => {
    render(
      <TestProviders>
        <ColumnsTable columns={[]} tables={[]} selectedCode={null} unitCode={() => ''} onSelect={() => undefined} />
      </TestProviders>,
    );

    const region = screen.getByRole('region');
    expect(region.getAttribute('tabindex')).toBe('0');
    expect(region.getAttribute('aria-label')).toBeTruthy();
  });
});
