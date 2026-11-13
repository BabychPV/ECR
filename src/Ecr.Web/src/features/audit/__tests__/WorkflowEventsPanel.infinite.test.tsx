import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WorkflowEventsPanel } from '@/features/audit/WorkflowEventsPanel';
import { servePages, showPanel, SlowEnvTimeout } from './panels.infinite.helpers';

/**
 * W-3f (рішення PO 11.11): журнал погодження дописує сторінку.
 * ⛔ Червоний без фіксу: після «Load more» `DOC-7` зникав (курсор жив у ключі запиту).
 */
function event(documentBusinessKey: string): Record<string, unknown> {
  return {
    changedAt: '2026-01-05T10:00:00Z',
    action: 'Submit',
    fromStatus: 'Draft',
    toStatus: 'Submitted',
    comment: null,
    changedByUserId: 41,
    changedByDisplayName: 'Boss',
    documentId: 7,
    documentBusinessKey,
    sheetDefId: 2,
    sheetCode: 'S2',
    periodKey: 202601,
    stepOrdinal: null,
  };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('WorkflowEventsPanel: нескінченна прокрутка (W-3f)', () => {
  it(
    'W-3f: Load more дописує сторінку, перша лишається',
    async () => {
      servePages('/api/v1/audit/workflow', event('DOC-7'), event('DOC-8'));
      showPanel(<WorkflowEventsPanel from="2026-01-01" to="2026-01-31" />, '/admin/audit?view=workflow');
      const user = userEvent.setup();

      await screen.findByText(/DOC-7/, {}, { timeout: SlowEnvTimeout });
      await user.click(screen.getByRole('button', { name: '⟦documents.more⟧' }));

      await screen.findByText(/DOC-8/, {}, { timeout: SlowEnvTimeout });
      expect(screen.getByText(/DOC-7/)).toBeTruthy();
    },
    SlowEnvTimeout,
  );
});
