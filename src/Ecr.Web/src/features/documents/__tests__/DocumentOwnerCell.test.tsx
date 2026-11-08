import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { DocumentOwnerCell } from '@/features/documents/DocumentOwnerCell';
import { testTheme } from '@/test/render';

/**
 * ✎ Live-прохід 08.11 (P3, Documents): під службовим обліковим записом «—» у кожному рядку «Owner». Корінь —
 * навмисне приховування R-7 для читача зі звуженням (сервер: `ownerDisplayName = null`, `modifiedAt = null`).
 * Доказ «падає без фіксу»: колишня клітинка — голе «—» з `title`, без названої причини й без фокуса;
 * обидві перевірки нижче на ній червоні (немає `aria-label` з причиною, немає `tabindex`).
 */
function show(owner: string | null, modifiedAt: string | null): HTMLElement {
  render(
    <MantineProvider theme={testTheme}>
      <DocumentOwnerCell ownerDisplayName={owner} modifiedAt={modifiedAt} />
    </MantineProvider>,
  );

  return screen.getByText(owner ?? '—');
}

describe('DocumentOwnerCell', () => {
  it('власника видно — ім’я', () => {
    expect(show('land-owner', '2026-11-08T10:00:00Z').textContent).toBe('land-owner');
  });

  it('приховано звуженням (R-7) — «—» з названою причиною, доступною з клавіатури', () => {
    const cell = show(null, null);
    expect(cell.getAttribute('aria-label')).toBe('⟦documents.ownerHidden⟧');
    expect(cell.getAttribute('data-owner')).toBe('hidden');
    expect(cell.getAttribute('tabindex')).toBe('0');
  });

  it('облікового запису власника немає — інша причина', () => {
    const cell = show(null, '2026-11-08T10:00:00Z');
    expect(cell.getAttribute('aria-label')).toBe('⟦documents.ownerUnknown⟧');
  });
});
