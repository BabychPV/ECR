import { useState, type JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import * as headerFieldApi from '@/features/templates/headerFieldApi';
import { renderKeepMounted, settle } from '@/test/closedDialogProbe';
import * as bindingsApi from '../../api/bindingsApi';
import * as typesApi from '../../api/typesApi';
import { BindingWizard } from '../BindingWizard';
import { bindingsKey } from '../bindingsKeys';
import { airType, airTypeItem, binding, kp, sheetTable } from './fixtures';

/**
 * B145b: майстер прив'язки зберігає ОДИН раз — і поки перелік прив'язок перечитується, і після закриття.
 *
 * ⛔ Діра (зонд на базі d618ea1f6; справжній майстер, відповіді віддає тест). Після відповіді сервера
 * `onSuccess` виклику ще чекав перечитування переліку прив'язок (`await invalidateQueries`): мутація вже
 * не `isPending`, майстер ще ВІДКРИТИЙ, кнопка «Save binding» активна — вікно завдовжки з мережевий запит,
 * не 200 мс. Клік у ньому слав другий `PUT` прив'язки (зонд: 2 збереження замість 1). Друге вікно — клік
 * по майстру, що закривається (ще в DOM) — закриває кореневий захист `shared/ui/Wizard`.
 *
 * Захист вікна «ще відкритий»: перечитування переліку — в `onSuccess` САМОЇ мутації, тож `isPending`
 * (і зайнятість кнопки) триває від запиту до закриття майстра.
 *
 * Мутаційні докази:
 * - база (`await invalidateQueries` в `onSuccess` виклику `save.mutate`) → червоний
 *   (`перелік перечитується: expected "vi.fn()" to be called 1 times, but got 2 times`);
 * - `BindingWizard.tsx`: не чекати перечитування в `onSuccess` мутації (`void invalidateQueries`) → червоний
 *   (`expected "vi.fn()" to not be called at all, but actually been called 1 times`: майстер закрився, не
 *   дочекавшись переліку, — зайнятість уже не тримає його до кінця);
 * - `Wizard.tsx`: прибрати `if (!opened) return;` → червоний
 *   (`майстер закрито: expected "vi.fn()" to be called 1 times, but got 2 times`).
 */
vi.mock('@/features/permits/api/typesApi', () => ({ listPermitTypes: vi.fn(), getPermitType: vi.fn() }));
vi.mock('@/features/permits/api/bindingsApi', () => ({ savePermitBinding: vi.fn() }));
vi.mock('@/features/templates/headerFieldApi', () => ({ getHeaderFields: vi.fn() }));
vi.mock('@/shared/ui/notify', () => ({ showDone: vi.fn(), showApiError: vi.fn() }));

/** Відпускає перечитування переліку прив'язок, що чекає; порожньо — перечитувань у польоті немає. */
const refetches: (() => void)[] = [];
let listReads = 0;

/** Як панель прив'язок: тримає активним запит переліку, який майстер інвалідує після збереження. */
function Host({ onClose }: { readonly onClose: () => void }): JSX.Element {
  const [opened, setOpened] = useState(true);

  useQuery({
    queryKey: bindingsKey(7),
    queryFn: async () => {
      listReads += 1;
      // Перше читання відповідає одразу; кожне перечитування чекає, доки тест його відпустить.
      if (listReads > 1) {
        await new Promise<void>((resolve) => {
          refetches.push(resolve);
        });
      }

      return [];
    },
  });

  return (
    <BindingWizard
      opened={opened}
      templateVersionId={7}
      table={sheetTable()}
      existing={binding()}
      onClose={() => {
        onClose();
        setOpened(false);
      }}
    />
  );
}

beforeEach(() => {
  listReads = 0;
  refetches.length = 0;
  vi.mocked(typesApi.listPermitTypes).mockResolvedValue({ items: [airTypeItem()] } as never);
  vi.mocked(typesApi.getPermitType).mockResolvedValue(airType());
  vi.mocked(headerFieldApi.getHeaderFields).mockResolvedValue([] as never);
  vi.mocked(bindingsApi.savePermitBinding).mockResolvedValue(binding());
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('B145b: BindingWizard — одне збереження на відкриття', () => {
  it('клік під час перечитування переліку і клік по закритому майстру не зберігають удруге', async () => {
    const onClose = vi.fn();
    renderKeepMounted(<Host onClose={onClose} />);

    // Правка наявної прив'язки: кроки вже заповнені — Sheet → Source → Pick → Columns → Review.
    fireEvent.click(screen.getByTestId('wizard-next'));
    await screen.findByRole('textbox', { name: /wizard\.type/ });
    fireEvent.click(screen.getByTestId('wizard-next'));
    fireEvent.click(screen.getByTestId('wizard-next'));
    await screen.findByRole('checkbox', { name: kp('map.useColumn', { name: 'Note' }) });
    fireEvent.click(screen.getByTestId('wizard-next'));

    const apply = screen.getByTestId('wizard-apply');
    fireEvent.click(apply);
    await waitFor(() => {
      expect(bindingsApi.savePermitBinding).toHaveBeenCalledTimes(1);
    });

    // Сервер відповів; перелік прив'язок перечитується (тест його тримає), майстер ще ВІДКРИТИЙ.
    await waitFor(() => {
      expect(refetches).toHaveLength(1);
    });
    expect(onClose).not.toHaveBeenCalled();
    expect(apply.isConnected).toBe(true);
    fireEvent.click(apply);
    await settle();
    expect(bindingsApi.savePermitBinding, 'перелік перечитується').toHaveBeenCalledTimes(1);
    // Активна кнопка, що нічого не робить, — загадка: до закриття вона зайнята.
    expect(apply).toHaveProperty('disabled', true);

    // Перелік перечитано — майстер закривається; він ще в DOM.
    refetches.shift()?.();
    await waitFor(() => {
      expect(onClose).toHaveBeenCalledTimes(1);
    });
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(apply).toHaveProperty('disabled', false);
    });
    expect(apply.isConnected).toBe(true);
    fireEvent.click(apply);
    await settle();
    expect(bindingsApi.savePermitBinding, 'майстер закрито').toHaveBeenCalledTimes(1);
  });
});
