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

/**
 * PA3-U-02: загальна умова - область фокусована, КОЛИ вміст прокручується (`scrollWidth > clientWidth`), незалежно від кількості
 * стовпців; вузька (нічого не прокручується) зайвого `tabIndex` не має. Розкладки в jsdom немає, тож розміри підміняємо.
 *
 * Мутаційний доказ (вручну, відновлено): `focusable = columns.length === 0` (стара умова) - тест «широкий» червоний.
 */
const columns = [
  {
    code: 'Name',
    nameL10n: { en: 'Name' },
    dataType: 'String',
    unitId: null,
    required: true,
    key: true,
    group: null,
    precision: null,
    scale: null,
    listScope: null,
    listRegistryCode: null,
    siblingTableCode: null,
    choices: null,
    order: 1,
  },
] as const;

function stubWidths(scroll: number, client: number): void {
  Object.defineProperty(HTMLElement.prototype, 'scrollWidth', { configurable: true, get: () => scroll });
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get: () => client });
}

function renderRows(): void {
  render(
    <TestProviders>
      <ColumnsTable columns={columns} tables={[]} selectedCode={null} unitCode={() => ''} onSelect={() => undefined} />
    </TestProviders>,
  );
}

describe('ColumnsTable: область з рядками', () => {
  afterEach(() => {
    delete (HTMLElement.prototype as { scrollWidth?: number }).scrollWidth;
    delete (HTMLElement.prototype as { clientWidth?: number }).clientWidth;
  });

  it('широкий вміст (scrollWidth > clientWidth) - іменована область у Tab, навіть зі стовпцями', () => {
    stubWidths(582, 214);
    renderRows();

    const region = screen.getByRole('region');
    expect(region.getAttribute('tabindex')).toBe('0');
    expect(region.getAttribute('aria-label')).toBeTruthy();
  });

  it('вузький вміст - без зайвого tabIndex і role=region', () => {
    stubWidths(200, 214);
    renderRows();

    expect(screen.queryByRole('region')).toBeNull();
    expect(document.querySelector('.pt-table-wrap')?.hasAttribute('tabindex')).toBe(false);
  });
});
