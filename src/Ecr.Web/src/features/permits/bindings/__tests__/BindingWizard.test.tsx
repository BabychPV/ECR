import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { EcrApiError } from '@/api/client';
import { renderWithQuery } from '@/test/render';
import * as headerFieldApi from '@/features/templates/headerFieldApi';
import * as bindingsApi from '../../api/bindingsApi';
import * as typesApi from '../../api/typesApi';
import { BindingWizard } from '../BindingWizard';
import { airType, airTypeItem, binding, k, kp, sheetTable } from './fixtures';

/**
 * Майстер прив'язки (PM-11): кроки, перевірки, тіло запиту, відмова сервера, правка.
 *
 * Мутаційний доказ (виконано, відновлено): у `draftProblem` прибрати гілку `rowsNeedDynamic` валить «Fixed»;
 * у `toRequest` віддавати `draft.mapping` і в режимі Formulas валить «Formulas без стовпців»; у `BindingWizard`
 * `existing === null` у `locked` замінити на `false` валить «правка»; у `onError` прибрати `api.fail` валить
 * «відмову сервера».
 */
vi.mock('@/features/permits/api/typesApi', () => ({ listPermitTypes: vi.fn(), getPermitType: vi.fn() }));
vi.mock('@/features/permits/api/bindingsApi', () => ({ savePermitBinding: vi.fn() }));
vi.mock('@/features/templates/headerFieldApi', () => ({ getHeaderFields: vi.fn() }));
vi.mock('@/shared/ui/notify', () => ({ showDone: vi.fn(), showApiError: vi.fn() }));

const next = (): HTMLElement => screen.getByTestId('wizard-next');
const stepError = (): HTMLElement => screen.getByTestId('wizard-step-error');

beforeEach(() => {
  vi.mocked(typesApi.listPermitTypes).mockResolvedValue({ items: [airTypeItem(), airTypeItem({ code: 'DraftOnly', version: 1, status: 'Draft' })] } as never);
  vi.mocked(typesApi.getPermitType).mockResolvedValue(airType());
  vi.mocked(headerFieldApi.getHeaderFields).mockResolvedValue([
    { id: 3, code: 'Permit', dataType: 'Lookup', isRequired: false, labelL10n: { values: { en: 'Permit' } }, lookupRegistryDefId: 8, ordinal: 1 },
    { id: 4, code: 'Facility', dataType: 'String', isRequired: false, labelL10n: { values: { en: 'Facility' } }, lookupRegistryDefId: null, ordinal: 2 },
  ] as never);
  vi.mocked(bindingsApi.savePermitBinding).mockResolvedValue(binding());
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function mount(props: Partial<Parameters<typeof BindingWizard>[0]> = {}) {
  const onClose = vi.fn();
  renderWithQuery(<BindingWizard opened templateVersionId={7} table={sheetTable()} existing={null} onClose={onClose} {...props} />);

  return { onClose };
}

async function pickType(user: ReturnType<typeof userEvent.setup>): Promise<void> {
  await user.click(await screen.findByRole('textbox', { name: k('wizard.type') }));
  await user.click(await screen.findByRole('option', { name: 'Air permit' }));
}

/** Крок 1 → крок 2 з обраним типом. */
async function toSource(user: ReturnType<typeof userEvent.setup>): Promise<void> {
  await user.click(next());
  await pickType(user);
}

describe('BindingWizard — кроки', () => {
  it('крок 1 показує таблицю аркуша, з якої відкрито майстер', async () => {
    mount();
    expect((await screen.findByTestId('binding-sheet-table')).textContent).toBe('Emission sources');
  });

  it('без типу далі не йде: банер у кроці й фокус на полі типу', async () => {
    const user = userEvent.setup();
    mount();
    await user.click(next());
    await user.click(next());

    expect(within(stepError()).getByText(k('problem.typeRequired'))).toBeDefined();
    expect(document.activeElement).toBe(screen.getByRole('textbox', { name: k('wizard.type') }));
  });

  it('у переліку лише опубліковані типи', async () => {
    const user = userEvent.setup();
    mount();
    await user.click(next());
    await user.click(await screen.findByRole('textbox', { name: k('wizard.type') }));

    expect(await screen.findByRole('option', { name: 'Air permit' })).toBeDefined();
    expect(screen.queryByRole('option', { name: /DraftOnly/ })).toBeNull();
  });

  it('таблиця аркуша з фіксованими рядками: режим Rows відхиляється, Formulas — ні', async () => {
    const user = userEvent.setup();
    mount({ table: sheetTable({ rowMode: 'Fixed' }) });
    await toSource(user);
    await user.click(next());

    expect(within(stepError()).getByText(kp('problem.rowsNeedDynamic', { tableCode: 'Emissions' }))).toBeDefined();

    await user.click(screen.getByRole('radio', { name: k('mode.formulas') }));
    await user.click(next());
    expect(screen.queryByTestId('wizard-step-error')).toBeNull();
  });

  it('FromHeaderLookup без поля шапки не проходить; у переліку лише поля-посилання на реєстр', async () => {
    const user = userEvent.setup();
    mount();
    await toSource(user);
    await user.click(next());

    await user.click(screen.getByRole('radio', { name: new RegExp(k('rule.fromHeader')) }));
    await user.click(next());
    expect(within(stepError()).getByText(k('problem.headerFieldRequired'))).toBeDefined();

    await user.click(screen.getByRole('textbox', { name: k('rule.headerField') }));
    expect(await screen.findByRole('option', { name: 'Permit' })).toBeDefined();
    expect(screen.queryByRole('option', { name: 'Facility' })).toBeNull();
  });
});

describe('BindingWizard — стовпці й збереження', () => {
  async function toColumns(user: ReturnType<typeof userEvent.setup>): Promise<void> {
    await toSource(user);
    await user.click(next());
    await user.click(next());
    await screen.findByRole('checkbox', { name: kp('map.useColumn', { name: 'Source' }) });
  }

  it('пропонує розкладку: ключі → RowKey, лише read-only стовпці аркуша; створення йде PUT за кодом з Id таблиці', async () => {
    const user = userEvent.setup();
    const { onClose } = mount();
    await toColumns(user);

    expect((screen.getByRole('checkbox', { name: kp('map.useColumn', { name: 'Source' }) }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole('checkbox', { name: kp('map.useColumn', { name: 'Note' }) }) as HTMLInputElement).checked).toBe(false);

    await user.click(next());
    await user.click(screen.getByTestId('wizard-apply'));

    await waitFor(() => expect(bindingsApi.savePermitBinding).toHaveBeenCalledTimes(1));
    expect(bindingsApi.savePermitBinding).toHaveBeenCalledWith(7, 'PB41_Emissions', {
      permitTypeCode: 'AirPermit',
      permitTableCode: 'AirEmissions',
      sheetTableDefId: 41,
      mode: 'Rows',
      selectionRule: 'ByFacilityPeriodEnd',
      headerFieldDefId: null,
      isActive: true,
      columns: [
        { permitFieldCode: 'Source', role: 'RowKey', sheetColumnDefId: 1 },
        { permitFieldCode: 'Substance', role: 'RowKey', sheetColumnDefId: 2 },
        { permitFieldCode: 'Permitted_t_year', role: 'Limit', sheetColumnDefId: 3 },
      ],
    });
    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it('без жодного ключа рядка крок «Columns» не пропускає', async () => {
    const user = userEvent.setup();
    mount();
    await toColumns(user);

    for (const name of ['Source', 'Substance']) {
      await user.click(screen.getByRole('checkbox', { name: kp('map.useColumn', { name }) }));
    }
    await user.click(next());

    expect(within(stepError()).getByText(k('problem.noRowKey'))).toBeDefined();
    expect(bindingsApi.savePermitBinding).not.toHaveBeenCalled();
  });

  it('використаний стовпець без вибраного стовпця аркуша — вада з кодом поля', async () => {
    const user = userEvent.setup();
    mount();
    await toColumns(user);

    await user.click(screen.getByRole('checkbox', { name: kp('map.useColumn', { name: 'Note' }) }));
    await user.click(next());

    expect(within(stepError()).getByText(kp('problem.noColumn', { fieldCode: 'Note' }))).toBeDefined();
  });

  it('Formulas без стовпців: крок «Columns» лише пояснює, у запиті columns порожній', async () => {
    const user = userEvent.setup();
    mount();
    await toSource(user);
    await user.click(screen.getByRole('radio', { name: k('mode.formulas') }));
    await user.click(next());
    await user.click(next());

    expect(await screen.findByTestId('binding-formulas-note')).toBeDefined();
    await user.click(next());
    await user.click(screen.getByTestId('wizard-apply'));

    await waitFor(() => expect(bindingsApi.savePermitBinding).toHaveBeenCalledTimes(1));
    expect(vi.mocked(bindingsApi.savePermitBinding).mock.calls[0]?.[2]).toMatchObject({ mode: 'Formulas', columns: [] });
  });

  it('розкладку Rows, збережену в майстрі, не відправляє, якщо потім перемкнули на Formulas', async () => {
    const user = userEvent.setup();
    mount();
    await toColumns(user);
    await user.click(screen.getByTestId('wizard-back'));
    await user.click(screen.getByTestId('wizard-back'));
    await user.click(screen.getByRole('radio', { name: k('mode.formulas') }));
    await user.click(next());
    await user.click(next());
    await user.click(next());
    await user.click(screen.getByTestId('wizard-apply'));

    await waitFor(() => expect(bindingsApi.savePermitBinding).toHaveBeenCalledTimes(1));
    expect(vi.mocked(bindingsApi.savePermitBinding).mock.calls[0]?.[2]).toMatchObject({ mode: 'Formulas', columns: [] });
  });

  it('відмову сервера (422 з локалізованим текстом) показує банером на кроці підсумку, майстер лишається', async () => {
    const user = userEvent.setup();
    vi.mocked(bindingsApi.savePermitBinding).mockRejectedValue(
      new EcrApiError({
        title: 'Unprocessable',
        status: 422,
        errorCode: 'ECR-PRM-0422',
        correlationId: 'c',
        detail: 'Table "Emissions" has fixed rows, so permit rows cannot be added to it.',
        extensions2: { messageKey: 'err.ECR-PRM-0422.bindingRowsNeedDynamic' },
      }),
    );
    const { onClose } = mount();
    await toColumns(user);
    await user.click(next());
    await user.click(screen.getByTestId('wizard-apply'));

    expect((await screen.findByTestId('wizard-step-error')).textContent).toContain('has fixed rows');
    expect(onClose).not.toHaveBeenCalled();
  });

  it('підсумок несе попередження про руйнівну зміну у версії з документами', async () => {
    const user = userEvent.setup();
    mount();
    await toColumns(user);
    await user.click(next());

    expect(screen.getByTestId('binding-breaking-hint').textContent).toContain(k('wizard.breakingHint'));
  });
});

describe('BindingWizard — правка', () => {
  it('тип і таблицю наявної прив’язки змінити не можна (bindingIdentityChanged); зберігається під її кодом', async () => {
    const user = userEvent.setup();
    mount({ existing: binding() });
    await user.click(next());

    expect((await screen.findByRole('textbox', { name: k('wizard.type') }) as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('textbox', { name: k('wizard.table') }) as HTMLInputElement).disabled).toBe(true);

    await user.click(next());
    await user.click(next());
    await screen.findByRole('checkbox', { name: kp('map.useColumn', { name: 'Note' }) });
    // Наявний ліміт лишився, невикористаний стовпець допитано невикористаним.
    expect((screen.getByRole('checkbox', { name: kp('map.useColumn', { name: 'Note' }) }) as HTMLInputElement).checked).toBe(false);
    expect((screen.getByRole('checkbox', { name: kp('map.useColumn', { name: 'Limit' }) }) as HTMLInputElement).checked).toBe(true);
    await user.click(next());
    await user.click(screen.getByTestId('wizard-apply'));

    await waitFor(() => expect(bindingsApi.savePermitBinding).toHaveBeenCalledWith(7, 'PB41_Emissions', expect.anything()));
  });
});
