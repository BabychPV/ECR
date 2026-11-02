import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { loadCatalog } from '@/shared/i18n';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { MethodologyConstantsPanel } from '../MethodologyContentPanels';
import { DerivedArguments } from '../DerivedArguments';

/**
 * План CONST-CATEGORY: таблиця констант із колонкою категорії й фільтром, діалог переліку категорій,
 * похідні аргументи — axe без блокуючих порушень в обох темах.
 *
 * ⚠ Контраст axe у jsdom не рахує (`test/a11y.ts`); його доводить e2e `methodologyCategories.spec.ts`
 * справжнім браузером (світла й темна тема, 1440 і 500 px).
 */

const Constants = [
  { id: 1, code: 'EF_gsec_1325_', kind: 'Numeric', value: 1.5, textValue: null, unitId: null, category: 'Loc_A', source: null, isResolved: true, validFrom: null, validTo: null },
  { id: 2, code: 'EF_gsec_1325_', kind: 'Numeric', value: 2.5, textValue: null, unitId: null, category: null, source: null, isResolved: true, validFrom: null, validTo: null },
];

const Categories = [
  { id: 1, code: 'Loc_A', description: 'Before MR, zone A', constantCount: 1, isRetired: false, ordinal: 1 },
  { id: 2, code: 'Loc_Old', description: null, constantCount: 0, isRetired: true, ordinal: 2 },
];

function mockApi(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (payload: unknown): Response =>
        new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: {} });
      if (url.endsWith('/versions/42/constants')) return json(Constants);
      if (url.endsWith('/versions/42/categories')) return json(Categories);
      if (url.includes('/api/v1/units')) return json([]);

      throw new Error(`Немає мока для ${url}`);
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('Категорії констант і похідні аргументи — axe', () => {
  it.each(Themes)('тема %s', async (scheme) => {
    mockApi();
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const { container } = render(
      <Shell colorScheme={scheme}>
        <h1>Methodology versions</h1>
        <MethodologyConstantsPanel methodologyId={1} versionId={42} editable />
        <DerivedArguments
          items={[
            { kind: 'Argument', name: 'Flow', isOptional: true, text: '@Flow?' },
            { kind: 'Constant', name: 'k1', isOptional: false, text: 'CST.k1' },
          ]}
        />
      </Shell>,
    );

    await screen.findByTestId('methodology-constants', {}, { timeout: 60_000 });

    const page = await findViolations(container);
    expect(page, describeViolations(page)).toHaveLength(0);

    fireEvent.click(screen.getByTestId('methodology-constants-categories'));
    const dialog = await screen.findByTestId('methodology-categories', {}, { timeout: 60_000 });
    await screen.findByText('Before MR, zone A', {}, { timeout: 60_000 });

    const modal = dialog.closest<HTMLElement>('[role="dialog"]') ?? dialog;
    const inDialog = await findViolations(modal);
    expect(inDialog, describeViolations(inDialog)).toHaveLength(0);
  }, 300_000);
});
