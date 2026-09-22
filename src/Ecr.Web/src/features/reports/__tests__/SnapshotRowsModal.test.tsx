import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import SnapshotRowsModal from '@/features/reports/SnapshotRowsModal';
import { testTheme } from '@/test/render';

/**
 * D-52a: рядки зрізу. Колонки й порядок задає СЕРВЕР (опис версії), а «показати
 * ще» просить наступну сторінку курсором попередньої й дописує, а не заміняє.
 */
const Strings: Record<string, string> = {
  'snapshots.rowsTitle': 'Snapshot rows',
  'snapshots.rowsMore': 'Show more',
  'snapshots.rowsEmpty': 'No rows',
};

/*
 * ⚠ `name` — обов'язкове поле контракту (`R9`, `SnapshotColumn`): сервер
 * розгортає фолбек «мова запиту → en → код» сам і порожнього підпису не
 * віддає. Фікстура без нього малювала б порожню шапку — і це вже не перевірка
 * екрана, а перевірка мока.
 *
 * ⚠ Тут підпис НАВМИСНО дорівнює коду: цей файл про сторінкування й порожній
 * стан, і різниця «код проти назви» перевіряється там, де вона предмет
 * (`SnapshotRowsModal.columnName.test.tsx`).
 */
const columns = [
  { code: 'OutputCode', kind: 'text', name: 'OutputCode' },
  { code: 'Value', kind: 'number', name: 'Value' },
];

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('SnapshotRowsModal (D-52a)', () => {
  it(
    'перша сторінка, потім «Show more» з курсором — рядки дописуються',
    async () => {
      const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);

        if (url.includes('/ui-strings/')) {
          return json({ languageCode: 'en', revision: 1, strings: Strings });
        }

        if (url.includes('/api/v1/reports/snapshots/7/rows')) {
          return url.includes('cursor=1')
            ? json({ columns, rows: [{ rowNo: 2, cells: { OutputCode: 'E_NOX', Value: null } }], nextCursor: null })
            : json({ columns, rows: [{ rowNo: 1, cells: { OutputCode: 'E_CO2', Value: 12.5 } }], nextCursor: 1 });
        }

        return json(null);
      });

      vi.stubGlobal('fetch', fetchMock);
      await loadCatalog('en', 'private');

      render(
        <MantineProvider theme={testTheme}>
          <QueryClientProvider
            client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
          >
            <SnapshotRowsModal snapshotId={7} onClose={() => undefined} />
          </QueryClientProvider>
        </MantineProvider>,
      );

      expect(await screen.findByText('E_CO2', undefined, { timeout: SlowEnvTimeout })).toBeDefined();
      expect(screen.getByText('12.5')).toBeDefined();
      expect(screen.getAllByRole('columnheader').map((th) => th.textContent)).toEqual([
        '#',
        'OutputCode',
        'Value',
      ]);

      fireEvent.click(screen.getByRole('button', { name: 'Show more' }));

      expect(await screen.findByText('E_NOX')).toBeDefined();
      expect(screen.getByText('E_CO2')).toBeDefined();
      expect(screen.queryByRole('button', { name: 'Show more' })).toBeNull();

      const asked = fetchMock.mock.calls.map(([url]) => String(url)).filter((u) => u.includes('/rows'));
      expect(asked).toHaveLength(2);
      expect(asked[0]).toContain('limit=100');
      expect(asked[1]).toContain('cursor=1');
    },
    SlowEnvTimeout,
  );
});
