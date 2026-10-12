import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider } from '@tanstack/react-query';
import { notifications } from '@mantine/notifications';
import { MemoryRouter } from 'react-router-dom';
import { createQueryClient } from '@/app/queryClient';
import { EditAssignmentsDrawer } from '@/features/editAssignments/EditAssignmentsDrawer';
import { testTheme } from '@/test/render';
import { December, DocumentId, February, January, mockServer, type Sent, type ServerOptions } from './fixture';

/**
 * Шторка «Доступ до редагування» (EA-5a): матриця, переходи Validate/Return/Remove/Repeat і майстер.
 * Тексти — позначки `⟦ключ⟧` (каталог у тесті не завантажено), як в сусідніх тестах документа.
 */

const sent: Sent[] = [];
/** Upsert передачі - `POST …/edit-assignments` (сервер не має `PUT`). */
const slice = (monthNumber: number): Record<string, unknown> => ({ monthNumber, periodKey: null, sheetCode: null, tableCode: null });
const isUpsert = (s: Sent): boolean => s.method === 'POST' && s.url.endsWith('/edit-assignments');
const Sheets = [{ code: 'AIR', name: 'Air emissions' }];

function show(options: ServerOptions = {}, entry = '/?panel=edit-assignments'): void {
  sent.length = 0;
  mockServer(sent, options);
  // Справжня сітка застосунку: без `meta.handled` відмова майстра дала б ще й другий (глобальний) тост.
  const client = createQueryClient();
  client.setDefaultOptions({ queries: { retry: false } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <EditAssignmentsDrawer documentId={DocumentId} sheets={Sheets} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

beforeEach(() => {
  cleanup();
});
afterEach(() => {
  vi.unstubAllGlobals();
});

async function rowOf(label: string): Promise<HTMLElement> {
  const header = await screen.findByRole('rowheader', { name: label });
  return header.closest('tr') as HTMLElement;
}

describe('EditAssignmentsDrawer — матриця', () => {
  it('малює передачі рядками з виконавцями й статусами; чекаючі валідації — лічильником', async () => {
    show();

    const jan = await rowOf('January');
    expect(within(jan).getByText('Ivanov')).toBeDefined();
    expect(within(jan).getByText('Petrenko')).toBeDefined();
    // Статус названий словом (ключ каталогу), а не лише кольором.
    expect(jan.querySelector('[data-assignment-status="Filled"]')?.textContent).toContain('⟦editAssign.status.Filled⟧');

    const feb = await rowOf('February');
    expect(feb.querySelector('[data-assignment-status="Returned"]')).not.toBeNull();
    expect(within(feb).getByText('⟦editAssign.returnedWith (reason=Check row 4)⟧')).toBeDefined();

    expect(screen.getByTestId('edit-assignments-awaiting').textContent).toBe('⟦editAssign.awaiting (count=1)⟧');
    expect(screen.getByText('⟦editAssign.owner (name=Owner Olena)⟧')).toBeDefined();
  });

  it('подання People — таблиця «людина × місяці» із заголовками рядків і колонок', async () => {
    show();
    await rowOf('January');

    fireEvent.click(screen.getByRole('radio', { name: '⟦editAssign.view.people⟧' }));

    const table = await screen.findByTestId('edit-assignments-people');
    expect(within(table).getAllByRole('columnheader')).toHaveLength(13);
    const ivanov = within(table).getByRole('rowheader', { name: 'Ivanov' }).closest('tr') as HTMLElement;
    // Січень — «заповнено», лютий — «повернено», 12 клітинок місяців; читалка бачить слово, не лише знак.
    expect(within(ivanov).getByText('⟦editAssign.status.Filled⟧')).toBeDefined();
    expect(within(ivanov).getByText('⟦editAssign.status.Returned⟧')).toBeDefined();
  });

  it('403 від сервера — стан «заборонено», кнопки «Assign» немає', async () => {
    show({ listStatus: 403 });

    await waitFor(() => expect(screen.queryByTestId('edit-assignments-months')).toBeNull());
    await screen.findByRole('dialog');
    expect(screen.queryByText('⟦editAssign.assign⟧')).toBeNull();
  });

  it('порожня шторка пояснює, що робити', async () => {
    show({ list: { ownerDisplayName: 'Owner Olena', canManage: true, assignments: [] } });

    expect(await screen.findByText('⟦editAssign.empty⟧')).toBeDefined();
    expect(screen.getByText('⟦editAssign.emptyHint⟧')).toBeDefined();
  });
});

describe('EditAssignmentsDrawer — переходи', () => {
  it('Validate шле baseVersion передачі', async () => {
    show();
    const jan = await rowOf('January');

    fireEvent.click(within(jan).getByRole('button', { name: '⟦editAssign.rowActions (slice=January)⟧' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: '⟦editAssign.validate⟧' }));

    // Підтвердження ДО запиту: без кліку в діалозі нічого не пішло.
    const dialog = await screen.findByRole('dialog', { name: '⟦editAssign.validateTitle (slice=January)⟧' });
    expect(sent).toHaveLength(0);
    fireEvent.click(within(dialog).getByRole('button', { name: '⟦editAssign.validate⟧' }));

    await waitFor(() => expect(sent.some((s) => s.url.endsWith('/1/validate'))).toBe(true));
    expect(sent.find((s) => s.url.endsWith('/1/validate'))?.body).toEqual({ baseVersion: 'v1' });
  });

  it('Return не ввімкнено без причини; із причиною шле reason і baseVersion', async () => {
    show();
    const jan = await rowOf('January');

    fireEvent.click(within(jan).getByRole('button', { name: '⟦editAssign.rowActions (slice=January)⟧' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: '⟦editAssign.return⟧' }));

    const dialog = await screen.findByRole('dialog', { name: '⟦editAssign.returnTitle (slice=January)⟧' });
    const confirm = within(dialog).getByRole('button', { name: '⟦editAssign.returnConfirm⟧' }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);

    // Підказка називає виконавців, яких зачепить повернення, і межу довжини; поле не дасть набрати більше.
    expect(within(dialog).getByText('⟦editAssign.returnHint (names=Ivanov, Petrenko, max=500)⟧')).toBeDefined();
    expect(within(dialog).getByRole('textbox').getAttribute('maxlength')).toBe('500');

    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'Fix row 4' } });
    expect(confirm.disabled).toBe(false);
    fireEvent.click(confirm);

    await waitFor(() => expect(sent.some((s) => s.url.endsWith('/1/return'))).toBe(true));
    expect(sent.find((s) => s.url.endsWith('/1/return'))?.body).toEqual({ reason: 'Fix row 4', baseVersion: 'v1', userIds: null });
  });

  it('Remove питає підтвердження, потім шле DELETE', async () => {
    show();
    const feb = await rowOf('February');

    fireEvent.click(within(feb).getByRole('button', { name: '⟦editAssign.rowActions (slice=February)⟧' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: '⟦editAssign.remove⟧' }));
    expect(sent).toHaveLength(0);

    const dialog = await screen.findByRole('dialog', { name: '⟦editAssign.removeTitle (slice=February)⟧' });
    fireEvent.click(within(dialog).getByRole('button', { name: '⟦editAssign.remove⟧' }));

    await waitFor(() => expect(sent.some((s) => s.method === 'DELETE')).toBe(true));
    expect(sent.find((s) => s.method === 'DELETE')?.url).toContain('/edit-assignments/2');
  });

  it('Repeat for next month переносить склад на наступний місяць БЕЗ примітки власника; для грудня пункту немає', async () => {
    show({ list: { ownerDisplayName: 'O', canManage: true, assignments: [January, December] } });

    const jan = await rowOf('January');
    fireEvent.click(within(jan).getByRole('button', { name: '⟦editAssign.rowActions (slice=January)⟧' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: '⟦editAssign.repeatNext⟧' }));

    await waitFor(() => expect(sent.some(isUpsert)).toBe(true));
    const body = sent.find(isUpsert)?.body as Record<string, unknown>;
    expect(body['slice']).toEqual(slice(2));
    expect(body['recipientUserIds']).toEqual(January.recipients.map((r) => r.userId));
    // D-4: примітка попереднього місяця не їде на наступний (раніше `/copy` її переносив разом з назвою місяця).
    expect(body).not.toHaveProperty('note');
    expect(sent.some((s) => s.url.endsWith('/copy'))).toBe(false);
    cleanup();

    show({ list: { ownerDisplayName: 'O', canManage: true, assignments: [December] } });
    const dec = await rowOf('December');
    fireEvent.click(within(dec).getByRole('button', { name: '⟦editAssign.rowActions (slice=December)⟧' }));
    await screen.findByRole('menuitem', { name: '⟦editAssign.edit⟧' });
    expect(screen.queryByRole('menuitem', { name: '⟦editAssign.repeatNext⟧' })).toBeNull();
  });
});

describe('EditAssignmentsDrawer — відмова і режими', () => {
  it('у симуляції - лише перегляд: ні «Assign», ні меню дій у рядках', async () => {
    show({ simulation: true });
    await rowOf('January');

    expect(screen.queryByRole('button', { name: '⟦editAssign.assign⟧' })).toBeNull();
    expect(screen.queryByRole('button', { name: '⟦editAssign.rowActions (slice=January)⟧' })).toBeNull();
  });
});

describe('EditAssignmentsDrawer — майстер', () => {
  async function openWizard(): Promise<void> {
    show({ list: { ownerDisplayName: 'O', canManage: true, assignments: [February] } });
    await rowOf('February');
    fireEvent.click(screen.getByRole('button', { name: '⟦editAssign.assign⟧' }));
    await screen.findByTestId('wizard-step-body');
  }

  const next = (): HTMLButtonElement => screen.getByTestId('wizard-next') as HTMLButtonElement;

  it('місяць → набір/виконавець → підсумок → POST (upsert) з ексклюзивністю за замовчуванням', async () => {
    await openWizard();

    // Крок 1: «Next» вимкнено, доки не вибрано місяць; зайнятий місяць має попередження про заміну.
    expect(next().disabled).toBe(true);
    fireEvent.click(screen.getByRole('checkbox', { name: 'January' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'February' }));
    expect(screen.getByRole('note').textContent).toContain('February');
    expect(next().disabled).toBe(false);
    fireEvent.click(next());

    // Крок 2: без виконавців далі не пускає, помилка — банером у кроці.
    await screen.findByLabelText('⟦editAssign.wizard.search⟧');
    fireEvent.click(next());
    expect((await screen.findByTestId('wizard-step-error')).textContent).toContain('⟦editAssign.wizard.noRecipients⟧');

    fireEvent.change(screen.getByLabelText('⟦editAssign.wizard.search⟧'), { target: { value: 'Ko' } });
    fireEvent.click(await screen.findByRole('button', { name: 'Kovalenko' }));
    fireEvent.click(next());

    // Підсумок: місяці, виконавці, ексклюзивність.
    const summary = await screen.findByTestId('wizard-summary');
    expect(within(summary).getByText('January, February')).toBeDefined();
    expect(within(summary).getByText('Kovalenko')).toBeDefined();
    expect(within(summary).getByText('⟦editAssign.wizard.summary.exclusive⟧')).toBeDefined();

    fireEvent.click(screen.getByTestId('wizard-apply'));

    await waitFor(() => expect(sent.filter((s) => isUpsert(s))).toHaveLength(2));
    const [jan, feb] = sent.filter((s) => isUpsert(s));
    expect(jan?.body).toEqual({ slice: slice(1), recipientUserIds: [12], isExclusive: true });
    // Наявна передача оновлюється з baseVersion.
    expect(feb?.body).toEqual({ slice: slice(2), recipientUserIds: [12], isExclusive: true, baseVersion: 'v2' });
  });

  it('галочка «дозволити й іншим» знімає ексклюзивність; набір розгортається знімком; «Зберегти як набір» створює набір', async () => {
    await openWizard();

    fireEvent.click(screen.getByRole('checkbox', { name: 'March' }));
    fireEvent.click(next());

    await screen.findByLabelText('⟦editAssign.wizard.search⟧');
    fireEvent.click(screen.getByRole('textbox', { name: '⟦editAssign.wizard.set⟧' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Dept 5' }));
    expect(await screen.findByText('Sydorenko')).toBeDefined();

    fireEvent.click(screen.getByRole('switch', { name: '⟦editAssign.wizard.saveAsSet⟧' }));
    fireEvent.change(screen.getByLabelText('⟦editAssign.wizard.setName⟧'), { target: { value: 'Spring team' } });
    fireEvent.click(next());

    await screen.findByTestId('wizard-summary');
    fireEvent.click(screen.getByRole('checkbox', { name: '⟦editAssign.wizard.alsoOthers⟧' }));
    expect(screen.getByText('⟦editAssign.wizard.summary.shared⟧')).toBeDefined();
    fireEvent.click(screen.getByTestId('wizard-apply'));

    await waitFor(() => expect(sent.some((s) => isUpsert(s))).toBe(true));
    expect(sent.find((s) => s.url.endsWith('/me/editor-sets'))?.body).toEqual({ name: 'Spring team', memberUserIds: [9] });
    expect(sent.find((s) => isUpsert(s))?.body).toEqual({
      slice: slice(3),
      recipientUserIds: [9],
      isExclusive: false,
      appliedSetId: 55,
    });
  });

  it('422 «понад власні права» лишає майстер відкритим і показує причину банером у підсумку', async () => {
    const shown = vi.spyOn(notifications, 'show');
    show({ list: { ownerDisplayName: 'O', canManage: true, assignments: [] }, putStatus: 422 });
    await screen.findByText('⟦editAssign.empty⟧');
    fireEvent.click(screen.getByRole('button', { name: '⟦editAssign.assign⟧' }));
    await screen.findByTestId('wizard-step-body');

    fireEvent.click(screen.getByRole('checkbox', { name: 'May' }));
    fireEvent.click(next());
    await screen.findByLabelText('⟦editAssign.wizard.search⟧');
    fireEvent.change(screen.getByLabelText('⟦editAssign.wizard.search⟧'), { target: { value: 'Ko' } });
    fireEvent.click(await screen.findByRole('button', { name: 'Koval' }));
    fireEvent.click(next());
    await screen.findByTestId('wizard-summary');
    fireEvent.click(screen.getByTestId('wizard-apply'));

    expect((await screen.findByTestId('wizard-step-error')).textContent).toContain('You cannot hand over sheets');
    expect(screen.getByTestId('wizard-summary')).toBeDefined();
    // Причина показана ЛИШЕ в майстрі: ні власного, ні глобального тоста.
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(shown).not.toHaveBeenCalled();
    shown.mockRestore();
  });
});
