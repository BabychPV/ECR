import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import type { GasComponent, GasVersion } from '../api';
import { VersionEditorModal } from '../VersionEditorModal';

/**
 * Доступність редактора версії складу газу в обох темах (людина 25.11: числові поля — `DecimalInput`, читабельне
 * подання з точним у підказці). Значення — з живої копії (ПК-3 (370-220) зима): 16 знаків, `4.013E-13`.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

afterEach(cleanup);

const Components: GasComponent[] = [
  { code: 'Methane', names: {}, formula: 'CH4', mw: '16.043', group: 'hydrocarbon' },
  { code: 'Propane', names: {}, formula: 'C3H8', mw: '44.097', group: 'hydrocarbon' },
  { code: 'CO2', names: {}, formula: null, mw: null, group: 'inert' },
];

const Live: GasVersion = {
  id: 9,
  validFrom: '2022-01-01',
  validTo: null,
  temperatureC: '98.0835451945116000',
  densityOp: '72.2161523478101000',
  mwHysys: '9.7447970000000000',
  z: '0.9123700000000000',
  ruleParse: null,
  legacyTypeId: null,
  legacyCompositionId: null,
  flertAliases: null,
  sum: '100',
  noComposition: false,
  components: [
    { code: 'Methane', molPct: '0.0000000000004013000' },
    { code: 'Propane', molPct: '5.2319888858166700' },
    { code: 'CO2', molPct: '94.7680111141833295987' },
  ],
  locks: [],
};

describe('VersionEditorModal — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: числові поля з читабельним поданням — без блокуючих порушень`, async () => {
      render(
        <Shell colorScheme={colorScheme}>
          <QueryClientProvider client={new QueryClient()}>
            <VersionEditorModal
              streamId={1}
              target={{ kind: 'edit', caseName: 'ПК-3 (370-220) зима', version: Live, previous: undefined, readOnly: false }}
              components={Components}
              onClose={vi.fn()}
              onSaved={vi.fn()}
            />
          </QueryClientProvider>
        </Shell>,
      );

      const dialog = await screen.findByRole('dialog');
      expect(dialog.textContent).not.toContain('⟦');
      const temperature = screen.getByRole<HTMLInputElement>('textbox', { name: Catalog['gasComposition.editor.temperature']! });
      expect(temperature.value).toBe('98.08355');
      expect(temperature.title).toBe('98.0835451945116');

      const violations = await findViolations(dialog);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
