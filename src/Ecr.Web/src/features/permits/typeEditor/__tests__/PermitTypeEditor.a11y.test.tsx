import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { apiFetch } from '@/api/client';
import { PermitTypeEditorPage } from '@/pages/admin/PermitTypeEditorPage';
import { Catalog, RouteShell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import * as typesApi from '../../api/typesApi';
import { permitType } from './fixtures';

/**
 * Доступність конструктора типу в обох темах (`ФВ-14.16`, `D-127`): Structure з вибраною таблицею,
 * відкритий інспектор (спільний реєстр, потім список лише типу), поле шапки лише для читання,
 * опублікований тип зі смугою «New version…», вкладки-слоти. Підписи — зі СПРАВЖНЬОГО каталогу.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));
vi.mock('../../api/typesApi', () => ({ getPermitType: vi.fn(), replacePermitType: vi.fn() }));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.EditDefinition'], isSimulation: false } }),
}));

afterEach(cleanup);

function lookupType() {
  const type = permitType();
  type.tables[0]!.columns[2] = {
    ...type.tables[0]!.columns[2]!,
    code: 'Use_type',
    nameL10n: { en: 'Type of use' },
    dataType: 'Lookup',
    precision: null,
    scale: null,
    unitId: null,
    listScope: 'TypeOnly',
    choices: ['Cooling', 'Process'],
  };
  return type;
}

async function scan(colorScheme: 'light' | 'dark', entry: string, type = permitType(), ready = 'tablist'): Promise<void> {
  vi.mocked(typesApi.getPermitType).mockResolvedValue(type);
  vi.mocked(apiFetch).mockImplementation((path: string) =>
    Promise.resolve(path === '/api/v1/units' ? [{ id: 7, code: 't/yr' }] : []) as never,
  );
  const { container } = render(
    <RouteShell colorScheme={colorScheme} path="/admin/permits/types/:code" entry={entry}>
      <PermitTypeEditorPage />
    </RouteShell>,
  );
  await screen.findByRole(ready);
  if (entry.includes('panel=')) await screen.findByTestId('column-inspector');
  await waitFor(() => expect(container.textContent).not.toContain('⟦'));
  const violations = await findViolations(container);
  expect(violations, describeViolations(violations)).toHaveLength(0);
}

describe('PermitTypeEditor — доступність', { timeout: 180_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: Structure з таблицею стовпців і підказками`, async () => {
      await scan(colorScheme, '/admin/permits/types/WaterDischarge');
    });

    it(`${colorScheme}: інспектор числового стовпця`, async () => {
      await scan(colorScheme, '/admin/permits/types/WaterDischarge?panel=Annual');
    });

    it(`${colorScheme}: інспектор списку лише для типу`, async () => {
      await scan(colorScheme, '/admin/permits/types/WaterDischarge?panel=Use_type', lookupType());
    });

    it(`${colorScheme}: Permit details, вкладки Checks і Versions`, async () => {
      await scan(colorScheme, '/admin/permits/types/WaterDischarge?t=__head');
      cleanup();
      await scan(colorScheme, '/admin/permits/types/WaterDischarge?tab=rules');
      cleanup();
      await scan(colorScheme, '/admin/permits/types/WaterDischarge?tab=versions');
    });

    it(`${colorScheme}: опублікований тип зі смугою «New version…»`, async () => {
      await scan(
        colorScheme,
        '/admin/permits/types/WaterDischarge?panel=Annual',
        permitType({ status: 'Published', hasUnpublishedChanges: false, draftVersion: null, version: 2 }),
      );
    });
  }
});
