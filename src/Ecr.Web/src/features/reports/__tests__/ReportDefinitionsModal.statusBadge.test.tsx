import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReportDefinition } from '@/api/types';
import { ReportDefinitionsModal } from '@/features/reports/ReportDefinitionsModal';
import { statusTable } from '@/shared/ui/StatusBadge';
import { testTheme } from '@/test/render';

/**
 * Статус версії звіту малює набір (`StatusBadge kind="version"`).
 *
 * ⛔ Тут стояло `<Badge color={… 'green' : 'gray'}>{reportVersion.status}</Badge>`:
 * код сервера сирим рядком і зелений на «все гаразд», якого набір не вживає.
 * `ReportVersion.Status` — той самий `TemplateVersionStatus`, що й у шаблонів
 * (`ReportDefinitions.cs:74`), і їде рядком (`v.Status.ToString()`), тож
 * словник `version` перевикористано, а не заведено другий.
 */
function versionWith(id: number, status: string): ReportDefinition['versions'][number] {
  return {
    id,
    version: `${String(id)}.0`,
    status,
    columnsJson: '[]',
    rulesJson: '{}',
    createdAt: '2026-09-19T10:00:00Z',
  };
}

const definitions: ReportDefinition[] = [
  {
    id: 1,
    code: 'FORM_6',
    isActive: true,
    isRegulatory: true,
    nameL10n: { values: { en: 'Form 6' } },
    versions: [versionWith(3, 'Draft'), versionWith(2, 'Published'), versionWith(1, 'Retired')],
  },
];

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ReportDefinitionsModal opened onClose={() => undefined} definitions={definitions} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function badgeOf(state: string): HTMLElement | null {
  return document.querySelector<HTMLElement>(`[data-status-state="${state}"]`);
}

const SlowEnvTimeout = 400_000;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ReportDefinitionsModal: статус версії — бейдж набору, а не код сервера', () => {
  it(
    'підпис іде з каталогу, сирого коду як тексту немає, невідомий стан не ламає рендер',
    async () => {
      expect(statusTable.version['Retired'], 'фікстура має бути невідомим станом').toBeUndefined();

      show();

      const row = (await screen.findByText('FORM_6', {}, { timeout: SlowEnvTimeout })).closest('tr');
      expect(row).not.toBeNull();

      /*
       * ⛔ Мутаційний доказ (RED, якщо повернути `{reportVersion.status}`):
       * бейджа з `data-status-state` немає, а в рядку з'являється текст
       * `Published`. Каталогу файл не завантажує, тож `t()` віддає позначений
       * ключ (`D-138`) — він і доводить, що підпис пройшов через `t()`.
       */
      expect(badgeOf('Published')?.getAttribute('data-status-kind')).toBe('version');
      expect(badgeOf('Published')?.textContent).toBe('⟦status.version.Published⟧');

      for (const state of ['Draft', 'Published', 'Retired']) {
        expect(
          within(row as HTMLElement).queryByText(state),
          `сирий код «${state}» на екрані`,
        ).toBeNull();
      }

      expect(badgeOf('Draft')?.getAttribute('data-status-known')).toBe('true');
      expect(badgeOf('Retired')?.getAttribute('data-status-known')).toBe('false');
      expect(badgeOf('Retired')?.getAttribute('data-status-tone')).toBe('warning');

      // Дія публікації лишилась прив'язаною до коду стану, а не до підпису.
      expect(within(row as HTMLElement).getAllByRole('button', { name: /reportDefs\.publish/ })).toHaveLength(1);
    },
    SlowEnvTimeout,
  );
});
