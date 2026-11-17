import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { PeriodPolicyManager } from '../PeriodPolicyManager';

/**
 * B149: «Manage period policies» — менеджер, що лишається відкритим після запису.
 *
 * Зонд (база 826f654e4):
 * - подвійний клік «Save» рядка / «Create» — ЗАКРИТО для людського подвійного кліку: `loading={…isPending…}` вимикає
 *   кнопку з наступним рендером; після успіху форма створення очищається вже ПІСЛЯ перечитування;
 * - закрите вікно — ВІДКРИТО: `Modal` змонтований завжди (стан `opened` усередині компонента), форма нової політики
 *   після «Close» на місці, «Create» активна — клік по ній у закритому `Modal` слав `POST /api/v1/projects/period-policies`
 *   уже після закриття. Рядки таблиці після закриття знімаються (`data={opened ? … : undefined}`), тож «Save» рядка в
 *   закритому вікні недосяжна — захист `!opened` там другий шар.
 *
 * Мутаційний доказ: прибрати `!opened ||` з обробника «Create» у `PeriodPolicyManager.tsx` → червоний другий тест
 * (`expected [ Array(1) ] to deeply equal []`).
 */

const Policy = { id: 7, code: 'MONTHLY_STD', openOffsetDays: -3, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 60 };
const Create = 'POST /api/v1/projects/period-policies';

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

async function openAndType(): Promise<HTMLElement> {
  fireEvent.click(screen.getByRole('button', { name: /periods\.managePolicies/ }));
  await screen.findByText('MONTHLY_STD');
  const codes = screen.getAllByRole('textbox', { name: /periods\.policyCode/ });
  const code = codes[codes.length - 1];
  if (code === undefined) throw new Error('поля коду немає');
  fireEvent.change(code, { target: { value: 'QUARTERLY' } });

  return screen.getByRole('button', { name: /periods\.policyCreate/ });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: PeriodPolicyManager — один запис на дію, жодного з закритого вікна', () => {
  it('подвійний клік «Create» шле один POST', async () => {
    const server = holdWrites({ read: () => [Policy], write: () => ({ body: { ...Policy, id: 8, code: 'QUARTERLY' } }) });
    renderKeepMounted(<PeriodPolicyManager />);
    const create = await openAndType();

    fireEvent.click(create);
    await waitFor(() => {
      expect(server.sent).toEqual([Create]);
    });
    await nextTick();
    fireEvent.click(create);
    await settle();
    expect(server.sent).toEqual([Create]);
  });

  /*
   * ⚠ «Save» рядка: рядки після закриття знімаються (`data={opened ? … : undefined}`) — кнопка від'єднана, і клік до
   * React не доходить. Тому `!opened` в її обробнику — ДРУГИЙ шар і сам по собі не червоніє (див. звіт мутацій: без
   * нього зелено; без нього І з рядками, що лишаються після закриття (`data={policies.data}`), — червоно; з захистом і
   * такими рядками — зелено).
   */
  it('«Save» рядка закритого вікна нічого не шле (рядки зняті разом із вікном)', async () => {
    const server = holdWrites({ read: () => [Policy] });
    renderKeepMounted(<PeriodPolicyManager />);
    await openAndType();
    const save = screen.getByRole('button', { name: /common\.save/ });
    expect(save).toHaveProperty('disabled', false);

    fireEvent.click(screen.getByRole('button', { name: /common\.close/ }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([]);
  });

  it('«Create» закритого вікна (ще в DOM) нічого не шле', async () => {
    const server = holdWrites({ read: () => [Policy] });
    renderKeepMounted(<PeriodPolicyManager />);
    const create = await openAndType();

    fireEvent.click(screen.getByRole('button', { name: /common\.close/ }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    expect(create.isConnected).toBe(true);
    expect(create).toHaveProperty('disabled', false);
    fireEvent.click(create);
    await settle();
    expect(server.sent).toEqual([]);
  });
});
