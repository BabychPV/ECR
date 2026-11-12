import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FlertSyncPanel } from '@/features/registries/flert/FlertSyncPanel';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * Стан синку FLERT (D-476 F3) в обох темах: смуга зі станом, лічильниками, причиною й кнопкою «Синхронізувати зараз» —
 * без блокуючих порушень axe.
 */
function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('FlertSyncPanel — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    for (const status of ['Succeeded', 'Degraded', 'Failed'] as const) {
      it(`${colorScheme}: ${status} без порушень`, async () => {
        vi.stubGlobal(
          'fetch',
          vi.fn(async (input: RequestInfo | URL) => {
            const path = String(input).split('?')[0] ?? '';
            if (path.endsWith('/api/v1/me')) {
              return json({
                denies: [],
                grants: {},
                isSimulation: false,
                language: 'en',
                mustChangePassword: false,
                permissions: ['Integration.Manage'],
                simulatedForUserId: null,
                userId: 9,
                userName: 'tester',
              });
            }

            return json({
              enabled: true,
              registryCodes: ['STREAM', 'STREAM_CASE', 'GAS_COMPOSITION'],
              lastRun: {
                startedAt: '2026-11-12T02:00:00Z',
                finishedAt: '2026-11-12T02:00:03Z',
                status,
                reason: status === 'Succeeded' ? null : 'deactivationLimit',
                sourceRows: 982,
                added: 3,
                updated: 1,
                deactivated: 0,
                reactivated: 0,
                issueCount: 2,
                message: null,
              },
            });
          }),
        );

        render(
          <Shell colorScheme={colorScheme}>
            <FlertSyncPanel registryCode="STREAM" />
          </Shell>,
        );
        await screen.findByTestId('flert-sync');

        const violations = await findViolations(document.body);
        expect(violations, describeViolations(violations)).toHaveLength(0);
      });
    }
  }
});
