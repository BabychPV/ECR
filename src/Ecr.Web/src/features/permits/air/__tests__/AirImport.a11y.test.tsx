import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import * as airApi from '@/features/permits/air/airApi';
import { AirLimitsTab } from '../AirLimitsTab';
import { AirImportWizard } from '../import/AirImportWizard';
import { analysis, card, preview, profile, profiles, result, scopeCodes, tree, xlsx } from './airFixtures';

/**
 * Доступність картки лімітів і майстра імпорту в обох темах (`ФВ-14.16`): картка (згорнута, розкрита, підсумки) і
 * чотири кроки майстра (файл, відповідність з редактором, перегляд із відсутніми НАЕ й заміною чужих дозволів,
 * результат). Підписи - справжні, з `09-seed.sql`; `⟦` у розмітці означає ключ без тексту в каталозі.
 *
 * ⚠ Контраст axe у jsdom не рахує (`test/a11y.ts`); тут перевіряються імена, ролі, `aria-*`, заголовки, таблиці.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/features/permits/air/airApi', () => ({
  getAirLimits: vi.fn(),
  listImportProfiles: vi.fn(),
  getImportProfile: vi.fn(),
  saveImportProfile: vi.fn(),
  analyseAirImport: vi.fn(),
  previewAirImport: vi.fn(),
  applyAirImport: vi.fn(),
  getNaeTree: vi.fn(),
  getAirScopeCodes: vi.fn(),
}));

const api = vi.mocked(airApi);
const text = (key: string): string => Catalog[key] ?? key;

beforeEach(() => {
  api.getAirLimits.mockResolvedValue(card());
  api.listImportProfiles.mockResolvedValue(profiles);
  api.getImportProfile.mockResolvedValue(profile());
  api.analyseAirImport.mockResolvedValue(analysis());
  api.previewAirImport.mockResolvedValue(preview());
  api.applyAirImport.mockResolvedValue(result());
  api.getNaeTree.mockResolvedValue(tree());
  api.getAirScopeCodes.mockResolvedValue(scopeCodes());
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

async function clean(container: HTMLElement): Promise<void> {
  expect(container.textContent).not.toContain('⟦');
  const found = await findViolations(container);
  expect(found, describeViolations(found)).toHaveLength(0);
}

describe('Air limits - доступність', { timeout: 300_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: картка - згорнута, розкрита, підсумки`, async () => {
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <AirLimitsTab permitId={5} mayImport />
        </Shell>,
      );
      await screen.findByText('Source 1085');
      await clean(container);

      fireEvent.click(screen.getByRole('button', { name: 'Substances of source 1085' }));
      await clean(container);

      fireEvent.click(screen.getByRole('radio', { name: text('permits.air.view.totals') }));
      await clean(container);
    });

    it(`${colorScheme}: майстер - файл, відповідність, перегляд, результат`, async () => {
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <AirImportWizard permitId={5} mayImport mayEditProfile />
        </Shell>,
      );
      await screen.findByRole('textbox', { name: new RegExp('^' + text('permits.air.import.region.label')) });
      await clean(container);

      const input = document.querySelector<HTMLInputElement>('input[type="file"]');
      if (input === null) throw new Error('file input');
      await userEvent.upload(input, xlsx());
      await userEvent.click(screen.getByRole('textbox', { name: new RegExp('^' + text('permits.air.import.region.label')) }));
      await userEvent.click(await screen.findByRole('option', { name: /^ATYRAU/ }));
      await userEvent.click(screen.getByRole('textbox', { name: new RegExp('^' + text('permits.air.import.area.label')) }));
      await userEvent.click(await screen.findByRole('option', { name: /^OFFSHORE/ }));
      fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.analyse') }));
      await screen.findByTestId('air-header-report');
      await clean(container);

      fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.map.edit') }));
      await screen.findAllByLabelText(text('permits.air.import.map.fieldName'));
      await clean(container);
      fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.map.discard') }));

      fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.toPreview') }));
      await screen.findByText(text('permits.air.import.replace.title'));
      await clean(container);

      fireEvent.click(screen.getByRole('button', { name: 'Skip all undecided (2)' }));
      fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.rerun') }));
      await waitFor(() => expect(screen.queryByText(text('permits.air.import.stale.title'))).toBeNull());
      fireEvent.click(screen.getByRole('checkbox', { name: text('permits.air.import.replace.confirm') }));
      await clean(container);

      fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.apply') }));
      await screen.findByTestId('air-import-result');
      await clean(container);
    });
  }
});
