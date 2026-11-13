import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import { FlertSyncPanel } from '../FlertSyncPanel';

/**
 * P3 F13 #841 (c): прогін при вимкненому синку журнал пише як `Succeeded` + `reason = disabled`, і смуга казала
 * «Last sync: … — succeeded.» поруч із «Sync is off» — одне заперечувало інше. Тепер такий прогін — «skipped — sync is
 * off», без нульових лічильників і без повтору причини.
 *
 * ⛔ Доказ: прибрати гілку `run.reason === 'disabled'` у `FlertSyncPanel.tsx` — перший тест червоний («succeeded» і
 * «Rows from FLERT: 0» повертаються). Контроль: справжній успішний прогін і далі «succeeded» з лічильниками.
 */

const Run = {
  startedAt: '2026-11-12T02:00:00Z',
  finishedAt: '2026-11-12T02:00:03Z',
  status: 'Succeeded',
  reason: 'disabled' as string | null,
  sourceRows: 0,
  added: 0,
  updated: 0,
  deactivated: 0,
  reactivated: 0,
  issueCount: 0,
  message: null,
};

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

function serve(lastRun: typeof Run, enabled: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Catalog });
      if (url.includes('/api/v1/me')) {
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
      if (url.includes('/api/v1/integration/flert-sync/status')) {
        return json({ enabled, registryCodes: ['STREAM', 'STREAM_CASE', 'GAS_COMPOSITION'], lastRun });
      }
      return json(null);
    }),
  );
}

async function panelText(lastRun: typeof Run, enabled: boolean): Promise<string> {
  serve(lastRun, enabled);
  await loadCatalog('en', 'private');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <FlertSyncPanel registryCode="STREAM" />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return (await screen.findByTestId('flert-sync')).textContent ?? '';
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('FlertSyncPanel: прогін при вимкненому синку', () => {
  it('сід має «skipped — sync is off» англійською', () => {
    expect(Catalog['flert.sync.status.skipped']).toBe('skipped — sync is off');
  });

  it('«skipped», а не «succeeded»; без нульових лічильників і без повтору причини', async () => {
    const text = await panelText(Run, false);

    expect(text).toContain('skipped — sync is off');
    expect(text).not.toContain(Catalog['flert.sync.status.succeeded']!);
    expect(text).not.toContain('Rows from FLERT');
    expect(text).not.toContain(Catalog['flert.sync.reason.disabled']!);
    expect(text).toContain(Catalog['flert.sync.disabled']!);
    expect(text).not.toContain('⟦');
  });

  it('контроль: справжній успішний прогін — «succeeded» з лічильниками', async () => {
    const text = await panelText({ ...Run, reason: null, sourceRows: 12, added: 12 }, true);

    expect(text).toContain(Catalog['flert.sync.status.succeeded']!);
    expect(text).toContain('Rows from FLERT: 12');
    expect(text).not.toContain('skipped');
  });
});
