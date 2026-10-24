import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { EcrApiError } from '@/api/client';
import { renderWithQuery } from '@/test/render';
import { showApiError, showDone } from '@/shared/ui/notify';
import * as bindingsApi from '../../api/bindingsApi';
import * as typesApi from '../../api/typesApi';
import { PermitBindingPanel } from '../PermitBindingPanel';
import { airType, binding, k, kp, sheetTable } from './fixtures';

/**
 * Вкладка «Permit binding» (PM-11): порожній стан, зіставлення, права, заморожена версія, видалення.
 *
 * Мутаційний доказ (виконано, відновлено): у панелі `mayEdit = canEdit && !frozen` → `mayEdit = true` валить
 * «без права» і «заморожена»; прибрати фільтр `item.sheetTableDefId === table.id` валить «чужа прив'язка»;
 * у `remove.onError` прибрати `showApiError` валить «відмова видалення»; у `mutationFn` викликати
 * `deletePermitBinding` з іншим кодом валить «видалення».
 */
vi.mock('@/features/permits/api/bindingsApi', () => ({ listPermitBindings: vi.fn(), deletePermitBinding: vi.fn() }));
vi.mock('@/features/permits/api/typesApi', () => ({ getPermitType: vi.fn() }));
vi.mock('@/shared/ui/notify', () => ({ showDone: vi.fn(), showApiError: vi.fn() }));
// Важкі лінивi чанки замінено заглушками: тут перевіряється лише хост.
vi.mock('@/features/permits/bindings/BindingPreview', () => ({
  BindingPreview: ({ binding: shown }: { binding: { code: string } }) => <div data-testid="preview-stub">{shown.code}</div>,
}));
vi.mock('@/features/permits/bindings/BindingWizard', () => ({
  BindingWizard: ({ existing }: { existing: { code: string } | null }) => (
    <div data-testid="wizard-stub">{existing === null ? 'create' : existing.code}</div>
  ),
}));

beforeEach(() => {
  vi.mocked(typesApi.getPermitType).mockResolvedValue(airType());
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const list = (items: ReturnType<typeof binding>[], isEditable = true) =>
  vi.mocked(bindingsApi.listPermitBindings).mockResolvedValue({ items, isEditable });

function mount(canEdit = true, table = sheetTable()) {
  renderWithQuery(<PermitBindingPanel templateVersionId={7} table={table} canEdit={canEdit} />);
}

describe('PermitBindingPanel — немає прив’язки', () => {
  it('з правом: пояснення й «Bind to a permit…», що відкриває майстер створення', async () => {
    const user = userEvent.setup();
    list([]);
    mount();

    expect(await screen.findByTestId('binding-empty')).toBeDefined();
    await user.click(screen.getByRole('button', { name: k('bind') }));
    expect((await screen.findByTestId('wizard-stub')).textContent).toBe('create');
  });

  it('без права: пояснення є, кнопки запису немає', async () => {
    list([]);
    mount(false);

    expect(await screen.findByTestId('binding-empty')).toBeDefined();
    expect(screen.queryByRole('button', { name: k('bind') })).toBeNull();
  });

  it('прив’язка ІНШОЇ таблиці версії цю таблицю не стосується', async () => {
    list([binding({ sheetTableDefId: 99, sheetTableCode: 'Other' })]);
    mount();

    expect(await screen.findByTestId('binding-empty')).toBeDefined();
    expect(screen.queryByTestId('binding-summary')).toBeNull();
  });
});

describe('PermitBindingPanel — є прив’язка', () => {
  it('показує стрілку «аркуш ← дозвіл», режим, правило, зіставлення з назвами й перегляд', async () => {
    list([binding()]);
    mount();

    const summary = await screen.findByTestId('binding-summary');
    expect(summary.textContent).toContain('Emission sources');
    await waitFor(() => expect(summary.textContent).toContain('Air permit › Air emissions'));
    expect(summary.textContent).toContain(k('mode.rows'));
    expect(summary.textContent).toContain(k('rule.byFacility'));

    const table = screen.getByRole('table');
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.map((row) => within(row).getAllByRole('cell').map((cell) => cell.textContent))).toEqual([
      ['Source', 'Source', k('role.rowKey'), k('map.noteRowKey')],
      ['Permitted_t_year', 'Limit', k('role.limit'), k('map.noteLimit')],
    ]);
    expect((await screen.findByTestId('preview-stub')).textContent).toBe('PB41_Emissions');
  });

  it('Formulas: замість зіставлення — пояснення про REGSUM, перегляд лишається', async () => {
    list([binding({ mode: 'Formulas', columns: [] })]);
    mount();

    expect(await screen.findByTestId('binding-formulas-only')).toBeDefined();
    expect(screen.queryByRole('table')).toBeNull();
    expect(await screen.findByTestId('preview-stub')).toBeDefined();
  });

  it('вимкнена прив’язка позначена', async () => {
    list([binding({ isActive: false })]);
    mount();

    expect(await screen.findByText(k('inactive'))).toBeDefined();
  });

  it('з правом: «Edit binding» відкриває майстер правки з кодом прив’язки', async () => {
    const user = userEvent.setup();
    list([binding()]);
    mount();

    await user.click(await screen.findByRole('button', { name: k('edit') }));
    expect((await screen.findByTestId('wizard-stub')).textContent).toBe('PB41_Emissions');
  });

  it('без права: ні «Edit», ні «Remove»', async () => {
    list([binding()]);
    mount(false);

    await screen.findByTestId('binding-summary');
    expect(screen.queryByRole('button', { name: k('edit') })).toBeNull();
    expect(screen.queryByRole('button', { name: k('remove') })).toBeNull();
  });

  it('заморожена (опублікована) версія: банер, і навіть з правом кнопок запису немає', async () => {
    list([binding()], false);
    mount();

    expect(await screen.findByTestId('binding-frozen')).toBeDefined();
    expect(screen.queryByRole('button', { name: k('edit') })).toBeNull();
    expect(screen.queryByRole('button', { name: k('remove') })).toBeNull();
  });

  it('заморожена версія без права на правку: банер не показується (нічого пояснювати)', async () => {
    list([], false);
    mount(false);

    await screen.findByTestId('binding-empty');
    expect(screen.queryByTestId('binding-frozen')).toBeNull();
  });
});

describe('PermitBindingPanel — видалення', () => {
  async function openRemove(user: ReturnType<typeof userEvent.setup>): Promise<HTMLElement> {
    await user.click(await screen.findByRole('button', { name: k('remove') }));

    return screen.findByRole('dialog');
  }

  it('діалог називає таблицю, попереджає про руйнівну зміну; підтвердження викликає DELETE за кодом', async () => {
    const user = userEvent.setup();
    list([binding()]);
    vi.mocked(bindingsApi.deletePermitBinding).mockResolvedValue(undefined);
    mount();

    const dialog = await openRemove(user);
    expect(within(dialog).getByText(kp('removeTitle', { name: 'Emission sources' }))).toBeDefined();
    expect(within(dialog).getByText(k('removeBreaking'))).toBeDefined();

    await user.click(within(dialog).getByRole('button', { name: k('removeVerb') }));

    await waitFor(() => expect(bindingsApi.deletePermitBinding).toHaveBeenCalledWith(7, 'PB41_Emissions'));
    await waitFor(() => expect(showDone).toHaveBeenCalledWith(k('removed')));
  });

  it('відмову видалення (409 breaking) показує showApiError і не вдає успіху', async () => {
    const user = userEvent.setup();
    list([binding()]);
    const refusal = new EcrApiError({ title: 'Conflict', status: 409, errorCode: 'ECR-SCHM-0409', correlationId: 'c' });
    vi.mocked(bindingsApi.deletePermitBinding).mockRejectedValue(refusal);
    mount();

    const dialog = await openRemove(user);
    await user.click(within(dialog).getByRole('button', { name: k('removeVerb') }));

    await waitFor(() => expect(showApiError).toHaveBeenCalledWith(refusal));
    expect(showDone).not.toHaveBeenCalled();
  });
});

describe('PermitBindingPanel — помилка читання', () => {
  it('403 на перелік — ErrorAlert, а не порожній стан із кнопкою створення', async () => {
    vi.mocked(bindingsApi.listPermitBindings).mockRejectedValue(
      new EcrApiError({ title: 'Forbidden', status: 403, errorCode: 'ECR-AUTH-0403', correlationId: 'c' }),
    );
    mount();

    expect(await screen.findByRole('alert')).toBeDefined();
    expect(screen.queryByRole('button', { name: k('bind') })).toBeNull();
  });
});
