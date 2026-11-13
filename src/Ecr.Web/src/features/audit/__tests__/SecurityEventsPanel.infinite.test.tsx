import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SecurityEventsPanel } from '@/features/audit/SecurityEventsPanel';
import { servePages, showPanel, SlowEnvTimeout } from './panels.infinite.helpers';

/**
 * W-3d (рішення PO 11.11): журнал безпеки дописує сторінку.
 * ⛔ Червоний без фіксу: після «Load more» `corr-1` зникав (курсор жив у ключі запиту).
 */
function event(correlationId: string): Record<string, unknown> {
  return {
    changedAt: '2026-01-05T10:00:00Z',
    changedByUserId: 1,
    changedByDisplayName: 'Admin',
    eventType: 'AccessDenied',
    detailsJson: null,
    targetUserId: null,
    targetRoleId: null,
    correlationId,
    siteTimeZoneId: null,
  };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('SecurityEventsPanel: нескінченна прокрутка (W-3d)', () => {
  it(
    'W-3d: Load more дописує сторінку, перша лишається',
    async () => {
      servePages('/api/v1/audit/security', event('corr-1'), event('corr-2'));
      showPanel(<SecurityEventsPanel from="2026-01-01" to="2026-01-31" />, '/admin/audit?view=security');
      const user = userEvent.setup();

      await screen.findByText('corr-1', {}, { timeout: SlowEnvTimeout });
      await user.click(screen.getByRole('button', { name: '⟦documents.more⟧' }));

      await screen.findByText('corr-2', {}, { timeout: SlowEnvTimeout });
      expect(screen.getByText('corr-1')).toBeTruthy();
    },
    SlowEnvTimeout,
  );
});
