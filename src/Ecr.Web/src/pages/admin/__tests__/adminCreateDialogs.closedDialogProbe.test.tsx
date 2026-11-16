import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { MethodologiesPage } from '@/pages/admin/MethodologiesPage';
import { TemplatesPage } from '@/pages/admin/TemplatesPage';
import { UnitsPage } from '@/pages/admin/UnitsPage';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149, група «скидання після успіху → кнопка вимкнена»: діалоги створення на сторінках адміністрування
 * («New unit», «New template», «New methodology»), що закрилися (ще в DOM) або ще чекають перечитування
 * переліку, не створюють удруге.
 *
 * Зонд на рівні СПРАВЖНЬОЇ сторінки (власник), запис тримає тест, перечитування переліку — ворота читань:
 * - UnitsPage «New unit» — вікно 2: `await invalidateQueries(units)`; `if (create.isPending) return` у
 *   обробнику (спінер `usePendingLoading` ще не ввімкнувся). Вікно 3: код/символ/назву/розмірність скинуто →
 *   `disabled`. ЗАКРИТО, один `POST /units`.
 * - TemplatesPage «New template» — вікно 2: `loading={create.isPending}` (вимкнена); вікно 3: код і назву
 *   скинуто → `disabled`. ЗАКРИТО, один `POST /templates`.
 * - MethodologiesPage «New methodology» — як «New unit»: `isPending` в обробнику, потім `disabled`. ЗАКРИТО,
 *   один `POST /methodologies`.
 */

function me(permissions: string[]): unknown {
  return {
    userId: 1,
    userName: 'tester',
    language: 'en',
    permissions,
    isSimulation: false,
    denies: [],
    grants: {},
    mustChangePassword: false,
    simulatedForUserId: null,
  };
}

const isOpen = (): boolean => screen.queryByRole('dialog') !== null;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: діалоги створення на сторінках адміністрування — один запит на відкриття', () => {
  it('UnitsPage «New unit»: один POST /units у всіх трьох вікнах', async () => {
    const units = [
      { id: 1, code: 'kg', dimensionId: 1, factorToBase: '1.0000000000', offsetToBase: '0.0000000000', dimensionCode: 'Mass' },
    ];
    const server = holdWrites({
      read: (url) => (url.endsWith('/api/v1/me') ? me(['Uom.EditCatalog']) : url.endsWith('/api/v1/units') ? units : {}),
      write: () => ({ body: { ...units[0], id: 3, code: 'g' } }),
    });
    const gate = gateReads();
    renderKeepMounted(<UnitsPage />);

    fireEvent.click(await screen.findByRole('button', { name: '⟦units.new⟧' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('⟦units.newCode⟧'), { target: { value: 'g' } });
    fireEvent.change(within(dialog).getByLabelText('⟦units.symbol⟧'), { target: { value: 'g' } });
    fireEvent.change(within(dialog).getByLabelText('⟦units.name⟧'), { target: { value: 'Gram' } });
    fireEvent.click(within(dialog).getByRole('textbox', { name: '⟦units.dimension⟧' }));
    fireEvent.click(await screen.findByRole('option', { name: /Mass/ }));
    const save = within(dialog).getByRole('button', { name: '⟦units.new⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });

    await probeThreeWindows({
      server,
      gate,
      button: () => save,
      isOpen,
      expected: ['POST /api/v1/units'],
      awaitedRead: '/api/v1/units',
    });
  });

  it('TemplatesPage «New template»: один POST /templates у всіх трьох вікнах', async () => {
    const server = holdWrites({
      read: (url) =>
        url.endsWith('/api/v1/me')
          ? me(['Template.Edit', 'Template.View'])
          : url.endsWith('/api/v1/templates')
            ? { items: [], nextCursor: null, totalCount: 0 }
            : url.endsWith('/api/v1/languages')
              ? [{ code: 'en', nameNative: 'English', isDefault: true, isActive: true }]
              : [],
      write: () => ({ body: { templateId: 5 } }),
    });
    const gate = gateReads();
    renderKeepMounted(<TemplatesPage />);

    fireEvent.click((await screen.findAllByRole('button', { name: '⟦templates.create⟧' }))[0] as HTMLElement);
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('⟦templates.code⟧'), { target: { value: 'AIR' } });
    fireEvent.change(await within(dialog).findByLabelText(/templates\.name/), { target: { value: 'Air' } });
    const save = within(dialog).getByRole('button', { name: '⟦common.save⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });

    await probeThreeWindows({
      server,
      gate,
      button: () => save,
      isOpen,
      expected: ['POST /api/v1/templates'],
      awaitedRead: '/api/v1/templates',
    });
  });

  it('MethodologiesPage «New methodology»: один POST /methodologies у всіх трьох вікнах', async () => {
    const server = holdWrites({
      read: (url) => (url.endsWith('/api/v1/me') ? me(['Calculation.EditFormula', 'Calculation.View']) : url.endsWith('/api/v1/methodologies') ? [] : {}),
      write: () => ({ body: { methodologyId: 4 } }),
    });
    const gate = gateReads();
    renderKeepMounted(<MethodologiesPage />);

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.newMethodology⟧' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('⟦methodologies.code⟧'), { target: { value: 'M-AIR' } });
    fireEvent.change(within(dialog).getByLabelText('⟦methodologies.name⟧'), { target: { value: 'Air' } });
    const save = within(dialog).getByRole('button', { name: '⟦methodologies.create⟧' });

    await probeThreeWindows({
      server,
      gate,
      button: () => save,
      isOpen,
      expected: ['POST /api/v1/methodologies'],
      awaitedRead: '/api/v1/methodologies',
    });
  });
});
