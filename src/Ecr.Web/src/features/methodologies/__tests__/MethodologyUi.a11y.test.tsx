import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { loadCatalog } from '@/shared/i18n';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { RouteShell, Themes } from '@/test/__tests__/a11yFixtures';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';

/**
 * Сторінка методології після виведення формульного рушія з інтерфейсу (✎ 24.11, «має бути тільки наш новий
 * редактор»): картка поточної версії-скрипта, таблиця скриптів, режими, прив'язки, трасування, журнал публікацій і
 * формульних версій немає (лише в базі) — axe без блокуючих порушень в обох темах, з журналом згорнутим і розгорнутим.
 *
 * ⚠ Контраст axe у jsdom не рахує (`test/a11y.ts`); його доводить e2e `methodologyUi.spec.ts` справжнім браузером.
 */

const Base = { numericMode: 'Strict', calendarMode: 'Actual', traceLevel: 'Off', createdByUserId: 1, level: 'Configuration' };
const Versions = [
  { ...Base, id: 10, versionNumber: 'V1', engine: 'Formulas', status: 'Published', isEditable: false, effectiveFrom: '2024-01-01' },
  { ...Base, id: 12, versionNumber: 'V2', engine: 'Script', status: 'Published', isEditable: false, effectiveFrom: '2024-01-02' },
  { ...Base, id: 13, versionNumber: '3.0', engine: 'Script', status: 'Draft', isEditable: true, effectiveFrom: null },
];

function mockApi(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (payload: unknown): Response =>
        new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: {} });
      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'tester',
          language: 'en',
          permissions: ['Calculation.View', 'Calculation.EditFormula', 'Calculation.EditRule', 'Calculation.Publish'],
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }
      if (url.endsWith('/api/v1/methodologies/1/versions')) return json(Versions);
      if (url.endsWith('/api/v1/methodologies/1/bindings')) return json([]);
      if (url.endsWith('/api/v1/methodologies/1/trace-steps')) return json({ traceSteps: false });
      if (url.includes('/api/v1/methodologies/1/publications')) return json([]);

      throw new Error(`Немає мока для ${url}`);
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  // Вибір «згорнуто/розгорнуто» секції пам'ятається в сховищі браузера — кожна тема починає зі згорнутого журналу.
  localStorage.clear();
});

describe('Сторінка методології (лише скрипти) — axe', () => {
  it.each(Themes)('тема %s', async (scheme) => {
    mockApi();
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const { container } = render(
      <RouteShell colorScheme={scheme} path="/admin/methodologies/:id/versions" entry="/admin/methodologies/1/versions">
        <MethodologyVersionsPage />
      </RouteShell>,
    );

    await screen.findByTestId('current-script-version');
    await screen.findByTestId('methodology-section-toggle-bindings', {}, { timeout: 20_000 });
    expect(screen.queryByTestId('formula-history')).toBeNull();
    expect(screen.getByTestId('methodology-section-toggle-publications').getAttribute('aria-expanded')).toBe('false');

    const collapsed = await findViolations(container);
    expect(collapsed, describeViolations(collapsed)).toHaveLength(0);

    // Журнал публікацій розгорнуто — теж без порушень.
    fireEvent.click(screen.getByTestId('methodology-section-toggle-publications'));
    expect(screen.getByTestId('methodology-section-toggle-publications').getAttribute('aria-expanded')).toBe('true');
    const expanded = await findViolations(container);
    expect(expanded, describeViolations(expanded)).toHaveLength(0);
  }, 60_000);
});