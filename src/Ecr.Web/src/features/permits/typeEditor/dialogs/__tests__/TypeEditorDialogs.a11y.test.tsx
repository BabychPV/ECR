import type { JSX } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { AddColumnDialog } from '../AddColumnDialog';
import { AddRuleDialog } from '../AddRuleDialog';
import { AddSeriesConfirm } from '../AddSeriesConfirm';
import { AddTableWizard } from '../AddTableWizard';
import { DeleteTypeConfirm } from '../DeleteTypeConfirm';
import { NewVersionConfirm } from '../NewVersionConfirm';
import { PublishTypeDialog } from '../PublishTypeDialog';
import { permitType, units } from './fixtures';

/**
 * Доступність діалогів і майстра конструктора типу в обох темах (Permits, PM-08c; `ФВ-14.16`).
 *
 * ⚠ Скануються стани, де з'являється нове: помилки форм (`role="alert"`, фокус на полі), крок
 * «Columns» із кількома рядками, «Review» із підсумком, діалог-відмова «без таблиць».
 */

const noop = (): void => {};
const type = permitType();

async function expectClean(): Promise<void> {
  const violations = await findViolations(screen.getByRole('dialog'));
  expect(violations, describeViolations(violations)).toHaveLength(0);
}

function mount(scheme: 'light' | 'dark', node: JSX.Element): void {
  render(<Shell colorScheme={scheme}>{node}</Shell>);
}

describe('Діалоги конструктора типу — доступність', { timeout: 120_000 }, () => {
  for (const scheme of Themes) {
    it(`${scheme}: майстер Add a table — помилка кроку, «Columns», «Review»`, async () => {
      const user = userEvent.setup();
      mount(scheme, <AddTableWizard opened type={type} units={units} onApply={noop} onClose={noop} />);
      await waitFor(() => {
        expect(document.activeElement).not.toBe(document.body);
      });

      await user.click(screen.getByTestId('wizard-next'));
      expect(screen.getByTestId('wizard-step-error')).toBeDefined();
      await expectClean();

      await user.type(screen.getByLabelText(/addTable\.nameEn/), 'Storage');
      await user.click(screen.getByTestId('wizard-next'));
      await user.click(screen.getByRole('button', { name: /addTable\.addColumn/ }));
      await expectClean();

      await user.type(screen.getAllByLabelText(/addTable\.columnN/)[1] as HTMLElement, 'Place');
      await user.click(screen.getByTestId('wizard-next'));
      expect(screen.getByTestId('wizard-summary')).toBeDefined();
      await expectClean();

      cleanup();
    });

    it(`${scheme}: Add a column — порожній і з помилкою`, async () => {
      const user = userEvent.setup();
      mount(
        scheme,
        <AddColumnDialog opened tableCode="Limits" existingCodes={[]} units={units} onApply={noop} onClose={noop} />,
      );
      await waitFor(() => {
        expect(document.activeElement).not.toBe(document.body);
      });
      await expectClean();

      await user.click(screen.getByRole('button', { name: /addColumn\.apply/ }));
      await screen.findByText(/col\.nameRequired/);
      await expectClean();

      cleanup();
    });

    it(`${scheme}: Add a check — порожній і з помилками`, async () => {
      const user = userEvent.setup();
      mount(scheme, <AddRuleDialog opened type={type} onApply={noop} onClose={noop} />);
      await waitFor(() => {
        expect(document.activeElement).not.toBe(document.body);
      });
      await expectClean();

      await user.click(screen.getByRole('button', { name: /addRule\.apply/ }));
      await screen.findByText(/addRule\.nameRequired/);
      await expectClean();

      cleanup();
    });

    it(`${scheme}: підтвердження серії, нової версії, видалення`, async () => {
      mount(scheme, <AddSeriesConfirm opened tableCode="Limits" existingCodes={[]} unit={units[0]} onApply={noop} onClose={noop} />);
      await expectClean();
      cleanup();

      mount(scheme, <NewVersionConfirm opened type={permitType({ status: 'Published' })} onConfirm={noop} onClose={noop} />);
      await expectClean();
      cleanup();

      mount(scheme, <DeleteTypeConfirm opened type={type} onConfirm={noop} onClose={noop} />);
      await expectClean();
      cleanup();
    });

    it(`${scheme}: публікація — причина і відмова «без таблиць»`, async () => {
      mount(scheme, <PublishTypeDialog opened type={type} permitCount={3} onSubmit={vi.fn()} onClose={noop} />);
      await expectClean();
      cleanup();

      mount(scheme, <PublishTypeDialog opened type={permitType({ tables: [] })} onSubmit={vi.fn()} onAddTable={noop} onClose={noop} />);
      await expectClean();
      cleanup();
    });
  }
});
