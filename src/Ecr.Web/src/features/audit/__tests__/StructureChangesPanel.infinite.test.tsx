import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { StructureChangesPanel } from '@/features/audit/StructureChangesPanel';
import { servePages, showPanel, SlowEnvTimeout } from './panels.infinite.helpers';

/**
 * W-3e (рішення PO 11.11): журнал структурних змін дописує сторінку.
 * ⛔ Червоний без фіксу: після «Load more» `BG_Total` зникав (курсор жив у ключі запиту).
 */
function change(entityLabel: string): Record<string, unknown> {
  return {
    changedAt: '2026-01-05T10:00:00Z',
    entityType: 'ColumnDef',
    entityId: 36396,
    operation: 'Update',
    oldJson: null,
    newJson: null,
    changeReason: null,
    changedByUserId: 41,
    changedByDisplayName: 'Ann',
    entityLabel,
  };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('StructureChangesPanel: нескінченна прокрутка (W-3e)', () => {
  it(
    'W-3e: Load more дописує сторінку, перша лишається',
    async () => {
      servePages('/api/v1/audit/structure', change('BG_Total'), change('BG_Other'));
      showPanel(<StructureChangesPanel from="2026-01-01" to="2026-01-31" />, '/admin/audit?view=structure');
      const user = userEvent.setup();

      await screen.findByText(/BG_Total/, {}, { timeout: SlowEnvTimeout });
      await user.click(screen.getByRole('button', { name: '⟦documents.more⟧' }));

      await screen.findByText(/BG_Other/, {}, { timeout: SlowEnvTimeout });
      expect(screen.getByText(/BG_Total/)).toBeTruthy();
    },
    SlowEnvTimeout,
  );
});
