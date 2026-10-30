import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { loadCatalog } from '@/shared/i18n';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { FormulaSyntaxHelp } from '../FormulaSyntaxHelp';
import { MethodologySection } from '../MethodologySection';
import { MethodologyRuleCoveragePanel } from '../RuleCoveragePanel';

/**
 * Сторінка версії методології (lane/integ-methodology-ui): згортувані секції,
 * довідка синтаксису (відкрита), матриця покриття з двома `PeriodPicker` і
 * відмовою «оберіть обидва періоди» — axe без блокуючих порушень в обох темах.
 *
 * ⚠ Контраст axe у jsdom не рахує (`test/a11y.ts`); його доводить e2e
 * `methodologyUi.spec.ts` справжнім браузером (світла й темна тема).
 */

function mockApi(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (payload: unknown): Response =>
        new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: {} });
      if (url.includes('/api/v1/expressions/metadata')) {
        return json({
          arguments: [],
          constants: [],
          formulas: [],
          headers: [],
          functions: [{ name: 'Pow', minArgs: 2, maxArgs: 2, acceptsRange: false, resultType: 'Number', tier: 'Core' }],
        });
      }
      if (url.includes('/rule-coverage')) {
        return json({
          methodologyVersionId: 42,
          periodFrom: 202501,
          periodTo: 202612,
          tableDefIds: [7],
          columnDefIds: [101],
          rules: [{ code: 'NOX', priority: 10 }],
          combinations: [
            { values: ['NOX'], state: 'Covered', winnerRuleCode: 'NOX', matchedRuleCodes: ['NOX'], rows: 1, documents: 1 },
            { values: ['SO2'], state: 'Gap', winnerRuleCode: null, matchedRuleCodes: [], rows: 1, documents: 1 },
          ],
          truncated: false,
          excludedRows: 0,
        });
      }
      if (url.endsWith('/bindings')) return json([]);

      throw new Error(`Немає мока для ${url}`);
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('Сторінка методології: секції, довідка, межі покриття — axe', () => {
  it.each(Themes)('тема %s', async (scheme) => {
    mockApi();
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const { container } = render(
      <Shell colorScheme={scheme}>
        <h1>Methodology versions</h1>
        <MethodologySection sectionId="a11y-collapsed" title="Publications" defaultExpanded={false}>
          <p>History</p>
        </MethodologySection>
        <MethodologySection sectionId="a11y-formulas" title="Formulas">
          <FormulaSyntaxHelp versionId={42} />
        </MethodologySection>
        <MethodologyRuleCoveragePanel methodologyId={1} versionId={42} />
      </Shell>,
    );

    fireEvent.click(screen.getByTestId('syntax-help-toggle'));
    await screen.findByTestId('syntax-help-functions');
    await screen.findByText('NOX', { selector: 'code' });

    // Стан, який сканується, справді на екрані: довідка відкрита, одна секція згорнута.
    expect(screen.getByTestId('syntax-help').hidden).toBe(false);
    expect(container.querySelectorAll('[aria-expanded="false"]').length).toBeGreaterThan(0);

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);

    // Відмова «оберіть обидва періоди» — теж без порушень.
    const fromField = container.querySelector<HTMLInputElement>('.ecr-period input');
    expect(fromField).not.toBeNull();
    fireEvent.change(fromField as HTMLInputElement, { target: { value: '' } });
    await screen.findByTestId('rule-coverage-window');

    const afterClear = await findViolations(container);
    expect(afterClear, describeViolations(afterClear)).toHaveLength(0);
  });
});
