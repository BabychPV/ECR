import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { PermitListsPage } from '@/pages/admin/PermitListsPage';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import type { PermitListDto } from '../listsApi';

/**
 * Доступність екрана «Lists» (PM-08d, PM-08e) в обох темах, зокрема шторка з полями правки значень. Тексти - справжні, із
 * `09-seed.sql`: ключ без тексту дав би `⟦ключ⟧`, і тест на це падає (так ловиться забутий рядок сіду).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View', 'Permit.EditType'], isSimulation: false } }),
}));

const use = (field: string) => ({
  typeCode: 'Air',
  typeNameL10n: { en: 'Air permit' },
  tableCode: 'Emission',
  tableNameL10n: { en: 'Emission' },
  fieldCode: field,
  fieldNameL10n: { en: field },
});

const lists: PermitListDto[] = [
  { key: 'shared:SUBSTANCES', scope: 'SharedRegistry', registryCode: 'SUBSTANCES', typeCode: null, nameL10n: { en: 'Substances' }, itemCount: 42, editable: true, usedBy: [use('Substance')] },
  { key: 'type:Air:Emission:Method', scope: 'TypeOnly', registryCode: 'PRM_Air_Emission_Method', typeCode: 'Air', nameL10n: { en: 'Method' }, itemCount: 2, editable: true, usedBy: [use('Method')] },
  { key: 'permit:Air:Emission:Kind', scope: 'SamePermitTable', registryCode: null, typeCode: 'Air', nameL10n: { en: 'Kind' }, itemCount: null, editable: false, usedBy: [use('Kind')] },
];

function stub(body: readonly PermitListDto[]): void {
  const json = (payload: unknown): Response =>
    new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } });
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const detail = /\/api\/v1\/permit-lists\/([^/?]+)/.exec(url);
      if (detail?.[1] === undefined) return json(body);
      const key = decodeURIComponent(detail[1]);

      return json({
        list: body.find((list) => list.key === key),
        items: [
          { id: 1, code: 'V001', displayL10n: { en: 'Direct' } },
          { id: 2, code: 'V002', displayL10n: { en: 'Calc' } },
        ],
      });
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('Списки вибору пермітів - доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: перелік з даними без блокуючих порушень і без ⟦ключів⟧`, async () => {
      stub(lists);
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitListsPage />
        </Shell>,
      );
      await screen.findByText('Substances');

      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });

    it(`${colorScheme}: шторка зі значеннями, полями додавання й перейменування без порушень`, async () => {
      stub(lists);
      render(
        <Shell colorScheme={colorScheme}>
          <PermitListsPage />
        </Shell>,
      );
      fireEvent.click(await screen.findByText('Method'));
      const drawer = await screen.findByRole('dialog');
      await within(drawer).findByText('Direct');
      fireEvent.click(within(drawer).getByRole('button', { name: 'Rename Calc' }));
      await within(drawer).findByRole('textbox', { name: 'Value' });

      expect(drawer.textContent).not.toContain('⟦');
      const violations = await findViolations(drawer);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });

    it(`${colorScheme}: порожній стан без порушень`, async () => {
      stub([]);
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitListsPage />
        </Shell>,
      );
      await screen.findByText(Catalog['permits.lists.empty.title'] ?? '');

      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
