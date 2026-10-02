import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { withTestDefaults } from '@/test/render';
import { CollectionScheduleTab } from '@/features/integration/CollectionScheduleTab';
import type { CollectionSchedule } from '@/features/integration/scheduleApi';

/**
 * Підтвердження видалення розкладу з клавіатури (WCAG 2.4.3, 4.1.2).
 *
 * ⛔ Кнопка «Remove» зникає з дерева в мить натискання — без переносу фокус падав на `body`, і
 * клавіатурний користувач починав сторінку спочатку. Тепер: підтвердження — група з ім'ям-питанням,
 * фокус на безпечному «Cancel»; скасування повертає фокус на «Remove».
 *
 * ⛔ Мутаційний доказ (локально, 2026-10-02): без ефекту переносу фокуса в `ScheduleForm` обидва
 * тести червоні — `document.activeElement` = `body`.
 */
const Strings: Record<string, string> = {
  'common.save': 'Save',
  'common.cancel': 'Cancel',
  'common.delete': 'Remove',
  'sources.never': 'never',
  'schedule.cron': 'Cron expression',
  'schedule.enabled': 'Enabled',
  'schedule.lastRun': 'Last run',
  'schedule.removeConfirm': 'Remove the schedule?',
  'schedule.lookbackDays': 'Window (days)',
  'schedule.dependsOn': 'Depends on schedule',
};

const schedule: CollectionSchedule = {
  id: 7,
  sourceEntityId: 42,
  sourceEntityCode: 'STACK-1',
  sourceEntityName: 'Stack analyzer',
  dataSourceId: 3,
  dataSourceCode: 'PI-WEST',
  cron: '0 15 2 * * ?',
  isEnabled: true,
  lastRunAt: null,
  lastError: null,
  lastErrorAt: null,
  rowVersion: 'AAAAAAAAB9E=',
  lookbackDays: 7,
};

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

async function show(): Promise<void> {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (url.includes('/api/v1/collection-schedules')) return json([schedule]);

      return json([]);
    }),
  );
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <CollectionScheduleTab sourceEntityId={42} dataSource="PI-WEST" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CollectionScheduleTab: підтвердження видалення з клавіатури', () => {
  it('Enter на «Remove» — фокус на «Cancel» у групі з ім\'ям-питанням', async () => {
    await show();
    const user = userEvent.setup();

    const remove = await screen.findByRole('button', { name: 'Remove' });
    remove.focus();
    await user.keyboard('{Enter}');

    const group = await screen.findByRole('group', { name: 'Remove the schedule?' });
    const cancel = within(group).getByRole('button', { name: 'Cancel' });

    await waitFor(() => expect(document.activeElement).toBe(cancel));
    // Підтвердження — наступна табуляція, а не повернення на початок сторінки.
    await user.tab({ shift: true });
    expect(document.activeElement).toBe(within(group).getByRole('button', { name: 'Remove' }));
  }, 60_000);

  it('«Cancel» з клавіатури повертає фокус на «Remove»', async () => {
    await show();
    const user = userEvent.setup();

    (await screen.findByRole('button', { name: 'Remove' })).focus();
    await user.keyboard('{Enter}');
    await screen.findByRole('group', { name: 'Remove the schedule?' });

    await user.keyboard('{Enter}');

    await waitFor(() => expect(screen.queryByRole('group', { name: 'Remove the schedule?' })).toBeNull());
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Remove' }));
  }, 60_000);
});
