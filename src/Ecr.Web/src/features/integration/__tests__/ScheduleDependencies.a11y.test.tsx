import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, waitFor } from '@testing-library/react';
import { DataSourceScheduleTab } from '@/features/integration/DataSourceScheduleTab';
import type { DataSource } from '@/features/integration/dataSourceApi';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes, createScanClient, settleQueries } from '@/test/__tests__/a11yFixtures';

/**
 * Залежності розкладів у шухляді з'єднання (`ФВ-13.15`, `ФВ-14.3`): axe без блокуючих порушень в обох
 * темах (`ФВ-14.16`).
 *
 * ⚠ Маршрут `/admin/sources` сканується без відкритої шухляди — вкладка розкладів, таблиця залежностей,
 * форма з вибором «від чого залежить», причина «не поставлено» і підтвердження видалення там не
 * малюються взагалі. Тут — усе це разом: перелік із залежністю, вибрана сутність із `lastError` і
 * відкрите підтвердження.
 *
 * ⛔ Мутаційний доказ (локально, 2026-10-02): прибрати `label` у полі cron (`TextInput` «schedule.cron») →
 * червоний `critical · label` в обох темах.
 */
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const Source = { id: 3, code: 'PI-WEST' } as unknown as DataSource;

function schedule(id: number, sourceEntityId: number, code: string, extra: Record<string, unknown> = {}): unknown {
  return {
    id,
    sourceEntityId,
    sourceEntityCode: code,
    sourceEntityName: `${code} analyzer`,
    dataSourceId: 3,
    dataSourceCode: 'PI-WEST',
    cron: '0 15 2 * * ?',
    isEnabled: true,
    lastRunAt: '2026-10-01T02:15:00Z',
    lastError: null,
    lastErrorAt: null,
    rowVersion: 'AAAAAAAAB9E=',
    lookbackDays: 7,
    dependsOnScheduleId: null,
    ...extra,
  };
}

const Schedules = [
  schedule(7, 42, 'STACK-1'),
  schedule(8, 43, 'STACK-2', {
    isEnabled: false,
    dependsOnScheduleId: 7,
    lastError: 'Quartz: trigger misfire',
    lastErrorAt: '2026-10-01T03:00:00Z',
  }),
];

const Entities = [42, 43].map((id) => ({
  id,
  code: `STACK-${String(id - 41)}`,
  displayName: `STACK-${String(id - 41)} analyzer`,
  dataSourceCode: 'PI-WEST',
  dataSourceId: 3,
  entityPath: null,
  isActive: true,
  lastRun: null,
  oldestGap: null,
  transport: 'PiWebApi',
}));

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

describe('Вкладка розкладів — axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: перелік із залежністю, форма з причиною і підтвердження видалення', async (scheme) => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/api/v1/collection-schedules')) return json(Schedules);
        if (/\/api\/v1\/sources(\?|$)/.test(url)) return json(Entities);
        if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: {} });

        return json([]);
      }),
    );

    const client = createScanClient();
    const { container } = render(
      <Shell colorScheme={scheme} client={client}>
        <DataSourceScheduleTab source={Source} />
      </Shell>,
    );
    await settleQueries(client);

    const row = container.querySelector('[data-schedule-row="STACK-2"] button');
    expect(row).not.toBeNull();
    fireEvent.click(row as Element);
    await waitFor(() => expect(container.querySelector('[data-last-error]')).not.toBeNull());

    // Підтвердження видалення — стан, у якому кнопки й підпис-питання міняються місцями.
    const remove = Array.from(container.querySelectorAll('[data-collection-schedule] button')).find(
      (button) => button.textContent === 'common.delete' || button.textContent?.includes('delete'),
    );
    expect(remove).toBeDefined();
    fireEvent.click(remove as Element);
    await waitFor(() => expect(container.querySelector('[role="group"]')).not.toBeNull());

    expect(container.querySelector('[data-schedule-depends-on="7"]')).not.toBeNull();

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
