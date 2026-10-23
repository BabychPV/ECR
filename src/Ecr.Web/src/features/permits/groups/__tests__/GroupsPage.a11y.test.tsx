import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { PermitGroupsPage } from '@/pages/admin/PermitGroupsPage';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import type { PermitGroupDto } from '../groupsApi';

/**
 * Доступність екрана «Groups» в обох темах: перелік, шторка створення (з показаними помилками полів), шторка
 * використовуваної групи, діалог підтвердження видалення. Тексти - справжні, із `09-seed.sql`: ключ без тексту дав би
 * `⟦ключ⟧`, і тест на це падає (так ловиться забутий рядок сіду).
 *
 * Фокус: Esc закриває лише шторку, фокус повертається на кнопку/рядок, що її відкрили (клас дефектів `returnFocus`).
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

const types = [
  { code: 'WaterIntake', category: 'Water', headerRegistryCode: 'PRM_WaterIntake', nameL10n: { en: 'Water intake' } },
  { code: 'Discharge', category: 'Water', headerRegistryCode: 'PRM_Discharge', nameL10n: { en: 'Discharge' } },
];

const groups: PermitGroupDto[] = [
  {
    code: 'WATER',
    descriptionL10n: { en: 'All water permits' },
    isActive: true,
    nameL10n: { en: 'Water permits' },
    registryCode: 'PRMG_WATER',
    registryDefId: 90,
    rowVersion: 'v1',
    types,
    updatedAt: '2026-10-20T10:00:00Z',
    usedInColumns: 0,
    usedInTemplates: 0,
  },
  {
    code: 'AIRUSED',
    descriptionL10n: {},
    isActive: false,
    nameL10n: { en: 'Air used' },
    registryCode: 'PRMG_AIRUSED',
    registryDefId: 91,
    rowVersion: 'v2',
    types: [],
    updatedAt: '2026-10-21T10:00:00Z',
    usedInColumns: 2,
    usedInTemplates: 1,
  },
];

function stub(body: readonly PermitGroupDto[]): void {
  const json = (payload: unknown): Response =>
    new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } });
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/languages')) {
        return json([
          { code: 'en', nameNative: 'English', isDefault: true },
          { code: 'ru', nameNative: 'Русский', isDefault: false },
          { code: 'kz', nameNative: 'Қазақша', isDefault: false },
        ]);
      }
      if (url.includes('/api/v1/permit-types')) {
        return json({
          items: types.map((type) => ({ ...type, status: 'Published', hasUnpublishedChanges: false, permitCount: 1, tables: [], updatedAt: null, updatedByDisplayName: null, version: 1 })),
          nextCursor: null,
          totalCount: 2,
        });
      }

      return json(body);
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('Групи пермітів - доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: перелік з даними без блокуючих порушень і без ⟦ключів⟧`, async () => {
      stub(groups);
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitGroupsPage />
        </Shell>,
      );
      await screen.findByText('Water permits');

      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });

    it(`${colorScheme}: шторка створення з помилками полів без порушень, Esc повертає фокус на «New group»`, async () => {
      stub(groups);
      render(
        <Shell colorScheme={colorScheme}>
          <PermitGroupsPage />
        </Shell>,
      );
      await screen.findByText('Water permits');
      const opener = await screen.findByRole('button', { name: Catalog['permits.groups.new'] ?? '' });
      opener.focus();
      fireEvent.click(opener);

      const drawer = await screen.findByRole('dialog');
      await within(drawer).findByRole('textbox', { name: /Name · English/ });
      fireEvent.click(within(drawer).getByRole('button', { name: Catalog['permits.groups.drawer.create'] ?? '' }));
      await within(drawer).findByText(/is not valid/);

      expect(drawer.textContent).not.toContain('⟦');
      const violations = await findViolations(drawer);
      expect(violations, describeViolations(violations)).toHaveLength(0);

      fireEvent.keyDown(drawer, { key: 'Escape' });
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(opener));
    });

    it(`${colorScheme}: шторка використовуваної групи й діалог видалення без порушень`, async () => {
      stub(groups);
      render(
        <Shell colorScheme={colorScheme}>
          <PermitGroupsPage />
        </Shell>,
      );
      fireEvent.click(await screen.findByText('Air used'));
      const used = await screen.findByRole('dialog');
      await within(used).findByText(/Used in 1 templates/);

      expect(used.textContent).not.toContain('⟦');
      const usedViolations = await findViolations(used);
      expect(usedViolations, describeViolations(usedViolations)).toHaveLength(0);
    });

    it(`${colorScheme}: діалог видалення невикористаної групи: фокус на «Cancel», Esc закриває лише діалог`, async () => {
      stub(groups);
      render(
        <Shell colorScheme={colorScheme}>
          <PermitGroupsPage />
        </Shell>,
      );
      fireEvent.click(await screen.findByText('Water permits'));
      const drawer = await screen.findByRole('dialog');
      fireEvent.click(await within(drawer).findByRole('button', { name: Catalog['permits.groups.drawer.delete'] ?? '' }));

      const confirm = await screen.findByTestId('confirm-cancel');
      await waitFor(() => expect(document.activeElement).toBe(confirm));
      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);

      fireEvent.keyDown(confirm, { key: 'Escape' });
      await waitFor(() => expect(screen.queryByTestId('confirm-cancel')).toBeNull());
      expect(screen.getAllByRole('dialog').length).toBeGreaterThan(0);
    });

    it(`${colorScheme}: правка -> Esc -> діалог «Discard» без порушень, фокус на «Keep editing»; Keep вертає фокус у поле, Discard - на рядок`, async () => {
      stub(groups);
      render(
        <Shell colorScheme={colorScheme}>
          <PermitGroupsPage />
        </Shell>,
      );
      const row = await screen.findByText('Water permits');
      fireEvent.click(row);
      const drawer = await screen.findByRole('dialog');
      const name = await within(drawer).findByRole('textbox', { name: /Name · English/ });
      fireEvent.change(name, { target: { value: 'Water 2' } });
      name.focus();

      fireEvent.keyDown(name, { key: 'Escape' });
      const keep = await screen.findByTestId('confirm-cancel');
      expect(keep.textContent).toBe(Catalog['permits.groups.drawer.keepEditing']);
      await waitFor(() => expect(document.activeElement).toBe(keep));
      const dialogs = screen.getAllByRole('dialog');
      const confirm = dialogs[dialogs.length - 1] as HTMLElement;
      expect(confirm.textContent).not.toContain('⟦');
      const violations = await findViolations(confirm);
      expect(violations, describeViolations(violations)).toHaveLength(0);

      fireEvent.click(keep);
      await waitFor(() => expect(screen.queryByTestId('confirm-cancel')).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(name));
      expect((name as HTMLInputElement).value).toBe('Water 2');

      fireEvent.keyDown(name, { key: 'Escape' });
      fireEvent.click(await screen.findByTestId('confirm-verb'));
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    });
  }
});
