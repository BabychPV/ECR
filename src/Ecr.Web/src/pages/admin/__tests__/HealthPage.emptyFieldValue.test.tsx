import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HealthPage } from '@/pages/admin/HealthPage';

/**
 * UI-прохід, F7: рядок «Missing filegroups» не має бути порожньою клітинкою.
 *
 * ⛔ Порожнеча тут читається ДВОЯКО — «відсутніх груп немає» і «рядок не
 * завантажився», — а решта вісім рядків панелі мають значення, тобто один
 * порожній серед них виглядає саме як збій. Причина була в `String(value)`:
 * `missingFilegroups` і `limitations` — це СПИСКИ (`DatabaseHealthCheck.cs`),
 * а `String([])` — порожній рядок.
 *
 * ⚠ Власний зразок, а не спільний `health-response.json`: у спільному цих
 * полів немає взагалі (там лише `edition`/`rcsi`/`partitionsAhead`), тож
 * порожній список він не відтворює. Спільний зразок лишається доказом ФОРМИ
 * відповіді (`health.test.tsx`); цей тест — доказ показу порожнього значення.
 */
const report = {
  status: 'Healthy',
  totalDurationMs: 1.5,
  checks: [
    {
      name: 'db',
      status: 'Healthy',
      description: 'База доступна.',
      durationMs: 1.1,
      data: {
        edition: 'Enterprise Developer Edition (64-bit)',
        filegroups: ['PRIMARY', 'FG_FACTS_2026'],
        // ⛔ Саме те, що бачить здорова система: жодної відсутньої групи.
        missingFilegroups: [],
        limitations: [],
        partitionsAhead: 15,
      },
    },
  ],
};

function show(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(report), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <HealthPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Текст клітинки значення в рядку з цим підписом. */
async function valueOf(labelKey: string): Promise<string> {
  const label = await screen.findByText(`⟦${labelKey}⟧`);
  const row = label.closest('tr');

  expect(row).not.toBeNull();

  return row?.cells[1]?.textContent ?? '';
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Панель бази: порожнє значення видно', () => {
  it('F7: порожній перелік відсутніх файлових груп показано знаком, а не порожньою клітинкою', async () => {
    show();

    // ⛔ Головне твердження. Мутаційний доказ: повернути `String(value)`
    // замість `fieldValue(value)` у `HealthPage.tsx` — і тут буде `''`.
    expect(await valueOf('health.database.missingFilegroups')).toBe('—');

    // Той самий дефект був і в другому списку панелі.
    expect(await valueOf('health.database.limitations')).toBe('—');
  });

  it('F7: непорожній перелік читається як перелік, а не як зліплений рядок', async () => {
    show();

    // ⚠ `String(['PRIMARY','FG_FACTS_2026'])` дає `PRIMARY,FG_FACTS_2026` —
    // без пробілу, тобто схоже на одне довге ім'я групи.
    expect(await valueOf('health.database.filegroups')).toBe('PRIMARY, FG_FACTS_2026');
  });

  it('F7: значення, які й до фіксу були видимі, лишились незмінними', async () => {
    show();

    expect(await valueOf('health.database.edition')).toBe('Enterprise Developer Edition (64-bit)');
    expect(await valueOf('health.database.partitionsAhead')).toBe('15');
  });
});
