import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import * as headerFieldApi from '@/features/templates/headerFieldApi';
import * as bindingsApi from '../../api/bindingsApi';
import * as typesApi from '../../api/typesApi';
import { BindingWizard } from '../BindingWizard';
import { PermitBindingPanel } from '../PermitBindingPanel';
import { airType, airTypeItem, binding, preview, sheetTable } from './fixtures';

/**
 * Доступність вкладки «Permit binding» і майстра прив'язки в обох темах (Permits, PM-11; `ФВ-14.16`).
 * Скануються: вкладка з прив'язкою (зіставлення + перегляд із заштрихованими комірками), порожній стан,
 * кроки майстра «Permit table», «Columns» і «Review».
 */
vi.mock('@/features/permits/api/bindingsApi', () => ({
  listPermitBindings: vi.fn(),
  deletePermitBinding: vi.fn(),
  previewPermitBinding: vi.fn(),
  savePermitBinding: vi.fn(),
}));
vi.mock('@/features/permits/api/typesApi', () => ({ listPermitTypes: vi.fn(), getPermitType: vi.fn() }));
vi.mock('@/features/permits/api/permitsApi', () => ({ listPermits: vi.fn() }));
vi.mock('@/features/templates/headerFieldApi', () => ({ getHeaderFields: vi.fn() }));

beforeEach(() => {
  vi.mocked(typesApi.listPermitTypes).mockResolvedValue({ items: [airTypeItem()] } as never);
  vi.mocked(typesApi.getPermitType).mockResolvedValue(airType());
  vi.mocked(headerFieldApi.getHeaderFields).mockResolvedValue([]);
  vi.mocked(bindingsApi.previewPermitBinding).mockResolvedValue(preview());
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

async function expectClean(root: Element): Promise<void> {
  const violations = await findViolations(root);
  expect(violations, describeViolations(violations)).toHaveLength(0);
}

describe('Прив’язка аркуша до дозволу — доступність', { timeout: 120_000 }, () => {
  for (const scheme of Themes) {
    it(`${scheme}: вкладка з прив’язкою (зіставлення й перегляд) і порожній стан`, async () => {
      vi.mocked(bindingsApi.listPermitBindings).mockResolvedValue({ items: [binding()], isEditable: true });
      const { container, unmount } = render(
        <Shell colorScheme={scheme}>
          <PermitBindingPanel templateVersionId={7} table={sheetTable()} canEdit />
        </Shell>,
      );
      await screen.findByTestId('binding-preview-permit');
      await expectClean(container);
      unmount();

      vi.mocked(bindingsApi.listPermitBindings).mockResolvedValue({ items: [], isEditable: true });
      const empty = render(
        <Shell colorScheme={scheme}>
          <PermitBindingPanel templateVersionId={7} table={sheetTable()} canEdit />
        </Shell>,
      );
      await screen.findByTestId('binding-empty');
      await expectClean(empty.container);
    });

    it(`${scheme}: майстер — крок таблиці дозволу, помилка кроку, «Columns» і «Review»`, async () => {
      const user = userEvent.setup();
      render(
        <Shell colorScheme={scheme}>
          <BindingWizard opened templateVersionId={7} table={sheetTable()} existing={null} onClose={vi.fn()} />
        </Shell>,
      );

      await user.click(await screen.findByTestId('wizard-next'));
      await user.click(screen.getByTestId('wizard-next'));
      expect(screen.getByTestId('wizard-step-error')).toBeDefined();
      await expectClean(screen.getByRole('dialog'));

      await user.click(screen.getByRole('textbox', { name: /wizard\.type/ }));
      await user.click(await screen.findByRole('option', { name: 'Air permit' }));
      await user.click(screen.getByTestId('wizard-next'));
      await user.click(screen.getByTestId('wizard-next'));
      await screen.findByRole('checkbox', { name: /map\.useColumn \(name=Source\)/ });
      await expectClean(screen.getByRole('dialog'));

      await user.click(screen.getByTestId('wizard-next'));
      expect(screen.getByTestId('wizard-summary')).toBeDefined();
      await expectClean(screen.getByRole('dialog'));
    });
  }
});
