import { describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithMantine } from '@/test/render';
import { AddTableWizard } from '../AddTableWizard';
import { permitType, units } from './fixtures';

/** Майстер «Add a table» (PM-08c): три кроки, перевірки, точний склад таблиці, джерела, вихід. */

const k = (key: string): string => `⟦permits.editor.dialogs.${key}⟧`;
const kp = (key: string, params: Record<string, string | number>): string =>
  `⟦permits.editor.dialogs.${key} (${Object.entries(params)
    .map(([name, value]) => `${name}=${String(value)}`)
    .join(', ')})⟧`;

function mount() {
  const onApply = vi.fn();
  const onClose = vi.fn();
  renderWithMantine(
    <AddTableWizard opened type={permitType()} units={units} onApply={onApply} onClose={onClose} />,
  );

  return { onApply, onClose };
}

const next = (): HTMLElement => screen.getByTestId('wizard-next');
const stepError = (): HTMLElement => screen.getByTestId('wizard-step-error');

async function typeName(user: ReturnType<typeof userEvent.setup>, name: string): Promise<void> {
  await user.type(screen.getByLabelText(new RegExp(k('addTable.nameEn'))), name);
}

describe('AddTableWizard — крок «Table»', () => {
  it('порожня назва: банер у кроці, фокус на полі назви, далі не йде', async () => {
    const user = userEvent.setup();
    mount();

    await user.click(next());

    expect(within(stepError()).getByText(k('addTable.nameRequired'))).toBeDefined();
    expect(document.activeElement).toBe(screen.getByLabelText(new RegExp(k('addTable.nameEn'))));
    expect(screen.queryByTestId('wizard-back')).toBeNull();
  });

  it('назва, що вже є в типі (без урахування регістру), відхиляється', async () => {
    const user = userEvent.setup();
    mount();

    await typeName(user, 'LIMITS');
    await user.click(next());

    expect(within(stepError()).getByText(k('addTable.nameTaken'))).toBeDefined();
  });

  it('«копія іншої таблиці» доступна, бо в типі є таблиця', async () => {
    const user = userEvent.setup();
    mount();

    await user.click(screen.getByLabelText(k('addTable.from')));
    const copy = await screen.findByRole('option', { name: k('addTable.fromCopy') });
    expect(copy.getAttribute('data-combobox-disabled')).toBeNull();
  });
});

describe('AddTableWizard — крок «Columns»', () => {
  it('без жодного ключового стовпця — відмова з фокусом на «Key»', async () => {
    const user = userEvent.setup();
    mount();

    await typeName(user, 'Monitoring points');
    await user.click(next());
    await user.click(screen.getByLabelText(k('col.key')));
    await user.click(next());

    expect(within(stepError()).getByText(k('addTable.noKey'))).toBeDefined();
    expect(document.activeElement).toBe(screen.getByLabelText(k('col.key')));
  });

  it('стовпець без назви — відмова з фокусом на його назві', async () => {
    const user = userEvent.setup();
    mount();

    await typeName(user, 'Monitoring points');
    await user.click(next());
    await user.clear(screen.getByLabelText(kp('addTable.columnN', { n: 1 })));
    await user.click(next());

    expect(within(stepError()).getByText(k('addTable.columnUnnamed'))).toBeDefined();
    expect(document.activeElement).toBe(screen.getByLabelText(kp('addTable.columnN', { n: 1 })));
  });

  it('єдиний стовпець не видаляється', async () => {
    const user = userEvent.setup();
    mount();

    await typeName(user, 'Monitoring points');
    await user.click(next());

    expect((screen.getByRole('button', { name: k('col.remove') }) as HTMLButtonElement).disabled).toBe(true);
    await user.click(screen.getByRole('button', { name: k('addTable.addColumn') }));
    expect(
      (screen.getAllByRole('button', { name: k('col.remove') }) as HTMLButtonElement[]).every((b) => !b.disabled),
    ).toBe(true);
  });
});

describe('AddTableWizard — застосування', () => {
  it('з нуля: підсумок і точний склад таблиці', async () => {
    const user = userEvent.setup();
    const { onApply, onClose } = mount();

    await typeName(user, 'Monitoring points');
    await user.type(screen.getByLabelText(k('addTable.nameRu')), 'Точки');
    await user.click(next());

    await user.clear(screen.getByLabelText(kp('addTable.columnN', { n: 1 })));
    await user.type(screen.getByLabelText(kp('addTable.columnN', { n: 1 })), 'Point');
    await user.click(screen.getByRole('button', { name: k('addTable.addColumn') }));
    await user.type(screen.getByLabelText(kp('addTable.columnN', { n: 2 })), 'Annual volume');
    await user.click(screen.getAllByLabelText(k('col.type'))[1] as HTMLElement);
    await user.click(await screen.findByRole('option', { name: k('type.Decimal') }));
    await user.click(screen.getAllByLabelText(k('col.unit'))[1] as HTMLElement);
    await user.click(await screen.findByRole('option', { name: 'thous. m3' }));
    await user.click(next());

    const summary = within(screen.getByTestId('wizard-summary'));
    expect(summary.getByText('PRM_WaterDischarge_MonitoringPoints')).toBeDefined();
    expect(summary.getByText('2: Point, Annual volume')).toBeDefined();
    expect(summary.getByText(kp('addTable.uniqueRow', { keys: 'Point' }))).toBeDefined();

    await user.click(screen.getByTestId('wizard-apply'));

    expect(onApply).toHaveBeenCalledTimes(1);
    expect(onApply).toHaveBeenCalledWith({
      code: 'MonitoringPoints',
      nameL10n: { en: 'Monitoring points', ru: 'Точки' },
      order: 2,
      rules: [],
      columns: [
        {
          code: 'Point', nameL10n: { en: 'Point' }, dataType: 'String', unitId: null, required: true,
          key: true, group: null, precision: null, scale: null, listScope: null, listRegistryCode: null,
          siblingTableCode: null, choices: null, order: 1,
        },
        {
          code: 'Annual_volume', nameL10n: { en: 'Annual volume' }, dataType: 'Decimal', unitId: 3,
          required: false, key: false, group: null, precision: null, scale: null, listScope: null,
          listRegistryCode: null, siblingTableCode: null, choices: null, order: 2,
        },
      ],
    });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('«Add months January to December» додає 12 стовпців групи з одиницею тис. м³', async () => {
    const user = userEvent.setup();
    const { onApply } = mount();

    await typeName(user, 'Volumes');
    await user.click(next());
    await user.clear(screen.getByLabelText(kp('addTable.columnN', { n: 1 })));
    await user.type(screen.getByLabelText(kp('addTable.columnN', { n: 1 })), 'Water body');
    await user.click(screen.getByRole('button', { name: k('addTable.addMonths') }));
    expect(screen.getAllByLabelText(new RegExp('⟦permits.editor.dialogs.addTable.columnN'))).toHaveLength(13);
    await user.click(next());
    await user.click(screen.getByTestId('wizard-apply'));

    const table = onApply.mock.calls[0]?.[0] as { columns: Record<string, unknown>[] };
    expect(table.columns).toHaveLength(13);
    expect(table.columns[1]).toMatchObject({
      code: 'January', group: 'Estimated_volume_by_month', unitId: 3, dataType: 'Decimal', order: 2,
    });
    expect(table.columns[12]).toMatchObject({ code: 'December', order: 13 });
  });

  it('копія іншої таблиці: стовпці, коди й ключ перенесені; код нової таблиці унікальний', async () => {
    const user = userEvent.setup();
    const { onApply } = mount();

    await typeName(user, 'Limits copy');
    await user.click(screen.getByLabelText(k('addTable.from')));
    await user.click(await screen.findByRole('option', { name: k('addTable.fromCopy') }));
    await user.click(next());

    expect(screen.getAllByLabelText(new RegExp('⟦permits.editor.dialogs.addTable.columnN'))).toHaveLength(2);
    await user.click(next());
    await user.click(screen.getByTestId('wizard-apply'));

    const table = onApply.mock.calls[0]?.[0] as { code: string; columns: { code: string; key: boolean }[] };
    expect(table.code).toBe('LimitsCopy');
    expect(table.columns.map((c) => [c.code, c.key])).toEqual([
      ['Name', true],
      ['Annual', false],
    ]);
  });

  it('з CSV: без файлу далі не йде; файл дає стовпці з вгаданими типами й банер', async () => {
    const user = userEvent.setup();
    const { onApply } = mount();

    await typeName(user, 'Imported');
    await user.click(screen.getByLabelText(k('addTable.from')));
    await user.click(await screen.findByRole('option', { name: k('addTable.fromCsv') }));
    await user.click(next());
    expect(within(stepError()).getByText(k('addTable.fileRequired'))).toBeDefined();

    const file = new File(['Water body;Annual volume\nLake;10,5\nRiver;3\n'], 'permits.csv', { type: 'text/csv' });
    await user.upload(screen.getByLabelText(new RegExp(k('addTable.file'))), file);
    expect(await screen.findByText(kp('addTable.fileRead', { file: 'permits.csv', count: 2 }))).toBeDefined();
    await user.click(next());

    expect(screen.getByText(kp('addTable.csvTitle', { file: 'permits.csv' }))).toBeDefined();
    await user.click(next());
    await user.click(screen.getByTestId('wizard-apply'));

    const table = onApply.mock.calls[0]?.[0] as { columns: { code: string; dataType: string; key: boolean }[] };
    expect(table.columns.map((c) => [c.code, c.dataType, c.key])).toEqual([
      ['Water_body', 'String', true],
      ['Annual_volume', 'Decimal', false],
    ]);
  });

  it('порожній CSV: помилка біля поля, стовпців немає', async () => {
    const user = userEvent.setup();
    mount();

    await typeName(user, 'Imported');
    await user.click(screen.getByLabelText(k('addTable.from')));
    await user.click(await screen.findByRole('option', { name: k('addTable.fromCsv') }));
    await user.upload(
      screen.getByLabelText(new RegExp(k('addTable.file'))),
      new File(['\n\n'], 'empty.csv', { type: 'text/csv' }),
    );

    expect(await screen.findByText(kp('addTable.csvEmpty', { file: 'empty.csv' }))).toBeDefined();
  });
});

describe('AddTableWizard — вихід', () => {
  it('без введеного Esc закриває одразу', async () => {
    const { onClose } = mount();

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });

    await waitFor(() => {
      expect(onClose).toHaveBeenCalledTimes(1);
    });
  });

  it('із введеним — спершу підтвердження «Discard»; «Скасувати» лишає майстер, підтвердження закриває', async () => {
    const user = userEvent.setup();
    const { onClose, onApply } = mount();

    await typeName(user, 'Draft table');
    await user.click(screen.getByTestId('wizard-cancel'));

    expect(await screen.findByText(k('addTable.discardTitle'))).toBeDefined();
    expect(onClose).not.toHaveBeenCalled();

    await user.click(screen.getByTestId('confirm-cancel'));
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByLabelText(new RegExp(k('addTable.nameEn')))).toBeDefined();

    await user.click(screen.getByTestId('wizard-cancel'));
    await user.click(await screen.findByRole('button', { name: k('addTable.discardVerb') }));
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(onApply).not.toHaveBeenCalled();
  });
});
