import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { EffectiveAccessView } from '@/api/types';
import { EffectiveAccessPanel } from '@/features/security/EffectiveAccessPanel';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes, createScanClient, settleQueries } from '@/test/__tests__/a11yFixtures';

/**
 * Розріз effective-access (`ФВ-6.16`, `D-220`) — axe без блокуючих порушень в обох темах (`ФВ-14.16`).
 *
 * ⚠ Маршрут `/admin/security` сканується із закритим діалогом доступу, а розріз малюється лише після
 * «Пояснити» — тобто жоден наявний прогін його не бачив. Тут — найбагатший стан: аркуш із проєктом,
 * застереження про стан документа, заборона, групи невідомі і внески з «успадковано від».
 *
 * ⛔ Мутаційний доказ (локально, 2026-10-02): прибрати `label` у `NumberInput` «effectiveAccess.projectId»
 * → червоний `critical · label` в обох темах.
 */
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const View: EffectiveAccessView = {
  userId: 7,
  userName: 'ivanov',
  resource: 'Sheet:5',
  level: 'None',
  isDenied: true,
  denyReason: 'Deny',
  groupsFromTicket: false,
  caveat: 'DocumentStateNotConsidered',
  projectId: 3,
  contributions: [
    { source: 'Grant', roleCode: 'Prj', principalSid: null, permissionCode: null, level: 'Read', isDeny: false, scope: 'InScope', counted: true, inheritedFrom: 'Project:3' },
    { source: 'Grant', roleCode: 'Blk', principalSid: 'S-1-5-21-1', permissionCode: null, level: 'Read', isDeny: true, scope: 'Unscoped', counted: true, inheritedFrom: null },
    { source: 'Permission', roleCode: 'Old', principalSid: null, permissionCode: 'Document.View', level: 'Read', isDeny: false, scope: 'Expired', counted: false, inheritedFrom: null },
  ],
} as EffectiveAccessView;

describe('Розріз effective-access — axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: аркуш із застереженням, забороною і внесками', async (scheme) => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify(View), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      ),
    );

    const client = createScanClient();
    const { container } = render(
      <Shell colorScheme={scheme} client={client}>
        <EffectiveAccessPanel userId={7} />
      </Shell>,
    );

    // ⚠ Поля — за порядком, а не за підписом: інакше мутація «без підпису» падала б на пошуку поля, а не
    // на axe, і доказ був би не про те.
    fireEvent.change(container.querySelector('select') as HTMLSelectElement, { target: { value: 'Sheet' } });
    const [resourceId, projectId] = Array.from(container.querySelectorAll('input'));
    fireEvent.change(resourceId as HTMLInputElement, { target: { value: '5' } });
    fireEvent.change(projectId as HTMLInputElement, { target: { value: '3' } });
    fireEvent.click(screen.getByRole('button', { name: /effectiveAccess\.explain/ }));

    await waitFor(() => expect(screen.getByTestId('effective-access-caveat')).toBeDefined());
    await settleQueries(client);
    expect(screen.getAllByRole('row')).toHaveLength(4);

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
