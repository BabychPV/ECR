import { describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithMantine } from '@/test/render';
import { AddRuleDialog } from '../AddRuleDialog';
import { permitType, table } from './fixtures';

/** Діалог «Add a check» (PM-08c): обов'язкові поля, точний склад перевірки, куди вона лягає. */

const k = (key: string): string => `⟦permits.editor.dialogs.addRule.${key}⟧`;

/** Без каталогу `t()` друкує параметри в дужках: так видно, що саме підставлено. */
const kp = (key: string, params: Record<string, string | number>): string =>
  `⟦permits.editor.dialogs.addRule.${key} (${Object.entries(params)
    .map(([name, value]) => `${name}=${String(value)}`)
    .join(', ')})⟧`;

function mount(overrides: Partial<Parameters<typeof AddRuleDialog>[0]> = {}) {
  const onApply = vi.fn();
  const onClose = vi.fn();
  const type = permitType({
    rules: [{ code: 'Months_add_up', nameL10n: { en: 'x' }, expression: 'TRUE', severity: 'Warn' }],
    tables: [table(), table({ code: 'Storage', nameL10n: { en: 'Storage' }, order: 2 })],
  });
  renderWithMantine(
    <AddRuleDialog opened type={type} onApply={onApply} onClose={onClose} {...overrides} />,
  );

  return { onApply, onClose };
}

describe('AddRuleDialog', () => {
  it('початковий фокус — на «What it checks»', async () => {
    mount();
    await waitFor(() => {
      expect(document.activeElement).toBe(screen.getByLabelText(new RegExp(k('name'))));
    });
  });

  it('порожня назва: помилка й фокус на назві; порожній вираз: помилка й фокус на виразі', async () => {
    const user = userEvent.setup();
    const { onApply } = mount();
    const apply = screen.getByRole('button', { name: k('apply') });

    await user.click(apply);
    expect(await screen.findByText(k('nameRequired'))).toBeDefined();
    expect(screen.getByText(k('expressionRequired'))).toBeDefined();
    expect(document.activeElement).toBe(screen.getByLabelText(new RegExp(k('name'))));

    await user.type(screen.getByLabelText(new RegExp(k('name'))), 'Months add up');
    await user.click(apply);
    expect(document.activeElement).toBe(screen.getByLabelText(new RegExp(k('expression'))));
    expect(onApply).not.toHaveBeenCalled();
  });

  it('за замовчуванням — весь дозвіл (null), Warn; код унікальний серед усіх перевірок типу', async () => {
    const user = userEvent.setup();
    const { onApply, onClose } = mount();

    await user.type(screen.getByLabelText(new RegExp(k('name'))), 'Months add up');
    await user.type(screen.getByLabelText(new RegExp(k('expression'))), 'ROW.Annual >= 0');
    await user.click(screen.getByRole('button', { name: k('apply') }));

    expect(onApply).toHaveBeenCalledWith(
      {
        code: 'Months_add_up_2',
        nameL10n: { en: 'Months add up' },
        expression: 'ROW.Annual >= 0',
        severity: 'Warn',
      },
      null,
    );
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('таблиця й «Block saving» потрапляють у виклик', async () => {
    const user = userEvent.setup();
    const { onApply } = mount();

    await user.type(screen.getByLabelText(new RegExp(k('name'))), 'Non-negative');
    await user.click(screen.getByLabelText(k('where')));
    await user.click(await screen.findByRole('option', { name: kp('table', { table: 'Storage' }) }));
    await user.type(screen.getByLabelText(new RegExp(k('expression'))), 'ROW.A >= 0');
    await user.click(screen.getByRole('radio', { name: k('block') }));
    await user.click(screen.getByRole('button', { name: k('apply') }));

    expect(onApply).toHaveBeenCalledTimes(1);
    const [rule, where] = onApply.mock.calls[0] as [Record<string, unknown>, string | null];
    expect(rule).toMatchObject({ code: 'Non_negative', severity: 'Block' });
    expect(where).toBe('Storage');
  });

  it('defaultTableCode обирає таблицю наперед', async () => {
    const user = userEvent.setup();
    const { onApply } = mount({ defaultTableCode: 'Storage' });

    await user.type(screen.getByLabelText(new RegExp(k('name'))), 'A');
    await user.type(screen.getByLabelText(new RegExp(k('expression'))), 'TRUE');
    await user.click(screen.getByRole('button', { name: k('apply') }));

    expect(onApply.mock.calls[0]?.[1]).toBe('Storage');
  });

  it('скасування й Escape не додають перевірки', async () => {
    const user = userEvent.setup();
    const { onApply, onClose } = mount();

    await user.click(screen.getByRole('button', { name: '⟦common.cancel⟧' }));
    expect(onClose).toHaveBeenCalledTimes(1);

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await waitFor(() => {
      expect(onClose.mock.calls.length).toBeGreaterThanOrEqual(2);
    });
    expect(onApply).not.toHaveBeenCalled();
  });
});
