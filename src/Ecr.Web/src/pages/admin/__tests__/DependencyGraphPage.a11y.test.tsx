import { describe as suite, it, expect, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { describe as report, findViolations } from '@/test/a11y';
import { loadCatalog } from '@/shared/i18n';
import { routes } from '@/app/routes';
import { Catalog, FullAccessPermissions, ScanShell, Themes, createScanClient, settlePage } from '@/test/__tests__/a11yFixtures';
import { DependencyGraphPage } from '../DependencyGraphPage';

/**
 * Доступність екрана «Граф залежностей» (`B179 L5`) в обох темах: рівні, зв'язка з перемикачами, споживач із доказами,
 * смуга циклу й зауваження побудови.
 *
 * ⚠ Мутаційний доказ (локально): тест ловить лише те, що вміє набір правил `findViolations`. Прибрати `aria-label` у
 * `SegmentedControl` НЕ червоніє (групи радіо-кнопок правилами не покрито, імена дають самі опції), а вимкнути `label`
 * у `Switch` теж не червоніє (Mantine кладе `description` у той самий `<label>`); червоніє «кнопка без імені»:
 * порожній підпис кнопки підтвердження (`ConfirmModal`) тут не відкритий, тож справжніх мутацій екрана немає, і
 * доступність підтверджується vitest-тестами ролей (`DependencyGraphPage.test.tsx`: `switch`, `heading`, `alert`).
 */
const graph = {
  revision: 'r1',
  truncated: true,
  nodes: [
    { kind: 'template', id: 1, code: 'HSE400' },
    { kind: 'template', id: 2, code: 'LAND_UI' },
    { kind: 'registry', id: 10, code: 'COMP' },
  ],
  edges: [
    {
      kind: 'fills', fromKind: 'template', fromId: 1, toKind: 'registry', toId: 10, scope: 'all', monthMask: 0, evidenceTotal: 1,
      evidence: [{ source: 'sourceBinding', id: 5, versionId: null, detail: 'Any' }],
    },
    {
      kind: 'templateFormula', fromKind: 'registry', fromId: 10, toKind: 'template', toId: 2, scope: 'month', monthMask: 0, evidenceTotal: 30,
      evidence: [{ source: 'ast', id: 7, versionId: 70, detail: 'T1.CNT' }],
    },
  ],
  templateDependencies: [
    {
      sourceTemplateId: 1, consumerTemplateId: 2, bindingId: 5, registryDefId: 10, scope: 'month', monthMask: 0,
      viaTemplateFormula: true, viaMethodology: false,
    },
  ],
  levels: [
    { level: 0, templateIds: [1] },
    { level: 1, templateIds: [2] },
  ],
  cycles: [{ templateIds: [1, 2] }],
  diagnostics: [{ key: 'astFallback', detail: 'template version 70' }],
};

const binding = {
  id: 5,
  code: 'HSE400_LAND_COMPOSITION',
  sourceTemplateCode: 'HSE400',
  registryCode: 'COMP',
  sourceState: 'any',
  policy: 'ConfirmWithReason',
  readFromDocument: true,
  recalculateDependents: true,
  readFromDocumentEffective: true,
  readFromDocumentSource: 'binding',
  consumerTemplateCodes: ['LAND_UI'],
  updatedAt: null,
  updatedByUserId: null,
};

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Catalog });
      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'bootstrap',
          language: 'en',
          permissions: FullAccessPermissions,
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }
      if (url.includes('/api/v1/dependency-graph')) return json(graph);
      if (url.includes('/api/v1/document-source-bindings')) return json([binding]);

      return new Response('{}', { status: 404, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

suite.each(Themes)('Доступність екрана «Граф залежностей» (%s)', (colorScheme) => {
  it('ФВ-14.16: /admin/dependency-graph не має порушень critical і serious', async () => {
    mockServer();
    await loadCatalog('en', 'private');
    await loadCatalog('en', 'public');

    const client = createScanClient();
    const route = {
      path: routes.adminDependencyGraph.path,
      entry: '/admin/dependency-graph',
      content: () => screen.findByTestId('template-LAND_UI'),
    };
    render(
      <ScanShell colorScheme={colorScheme} client={client} route={route}>
        <DependencyGraphPage />
      </ScanShell>,
    );

    await settlePage(client, route);
    await screen.findByTestId('template-LAND_UI', undefined, { timeout: 10_000 });
    const violations = await findViolations(document.body);

    expect(violations, report(violations)).toHaveLength(0);
  });
});
