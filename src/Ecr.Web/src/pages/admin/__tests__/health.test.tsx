import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HealthPage } from '@/pages/admin/HealthPage';

/**
 * Дашборд здоров'я читає ту саму відповідь, яку віддає сервер.
 *
 * ⛔ Зразок — **спільний**: `tests/Ecr.TestKit/Fixtures/health-response.json`
 * читає і серверний тест `Форма_звіту_збігається_зі_спільним_зразком`, і цей.
 * Власний зразок на клієнті нічого не доводив би: саме розбіжність між тим,
 * що клієнт вважає відповіддю, і тим, що сервер надсилає, і є `A7-04`.
 *
 * Дефект: клієнт читав `entries` словником, сервер писав `checks` масивом.
 * `Object.entries(undefined ?? {})` не падає — дашборд відкривався порожнім і
 * виглядав рівно як здорова система без зареєстрованих перевірок.
 */
const sample = JSON.parse(
  readFileSync(
    path.resolve(process.cwd(), '../../tests/Ecr.TestKit/Fixtures/health-response.json'),
    'utf8',
  ),
) as { status: string; checks: { name: string; status: string; description: string }[] };

function respond(body: unknown, status = 200): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <HealthPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Дашборд здоров’я', () => {
  it('показує кожну перевірку зі справжньої відповіді сервера', async () => {
    respond(sample);
    show();

    for (const check of sample.checks) {
      expect(await screen.findByText(check.name)).toBeDefined();
      expect(await screen.findByText(check.description)).toBeDefined();
    }
  });

  it('показує подробиці бази з перевірки «db»', async () => {
    respond(sample);
    show();

    // Саме те, заради чого `/health/db` існує (АРХ-7 п. 5).
    expect(await screen.findAllByText('Enterprise')).not.toHaveLength(0);

    // ⛔ Аудит-пас 8, lane6, п.7: людський підпис через каталог, а не сире
    // ім'я поля. Цей тест НЕ завантажує каталог (як і решта файлу — див.
    // `⟦health.noChecks⟧` нижче), тому `t()` повертає позначений ключ, а не
    // готовий переклад: саме ключ і є доказом, що рядок пройшов через `t()`,
    // а не через голе `{key}`.
    expect(await screen.findAllByText('⟦health.database.effectiveMode⟧')).not.toHaveLength(0);
  });

  it(
    'аудит-пас 8, lane6, п.7: панель бази не показує сирі camelCase-імена полів',
    async () => {
      respond(sample);
      show();

      await screen.findAllByText('⟦health.database.effectiveMode⟧');

      // ⛔ Мутаційний доказ: повернення `{key}` замість `{fieldLabel(key)}` у
      // `HealthPage.tsx` зробить цей тест червоним — `effectiveMode` знову
      // з'явиться в DOM буквально, замість позначеного ключа каталогу.
      expect(screen.queryByText('effectiveMode')).toBeNull();
      expect(screen.queryByText('rcsi')).toBeNull();
      expect(screen.queryByText('archiveBatchSize')).toBeNull();
      expect(screen.queryByText('majorVersion')).toBeNull();
    },
  );

  it('ФВ-14.22: невдалий запит НЕ виглядає як порожній дашборд (A7-04)', async () => {
    respond(
      {
        title: 'Недоступно',
        status: 503,
        errorCode: 'ECR-SYS-0503',
        correlationId: 'cid-health-1',
      },
      503,
    );
    show();

    // ⛔ Головне твердження. До виправлення тут була порожня сітка карток —
    // тобто екран, який неможливо відрізнити від здорової системи.
    const alerts = await screen.findAllByRole('alert');
    expect(alerts.length).toBeGreaterThan(0);
    expect(alerts.map((a) => a.textContent).join(' ')).toContain('ECR-SYS-0503');
  });

  it('порожній перелік перевірок пояснюється, а не мовчить', async () => {
    respond({ status: 'Healthy', totalDurationMs: 1, checks: [] });
    show();

    expect(await screen.findByText('⟦health.noChecks⟧')).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  /**
   * Стан `partial` (директива B3, `PR nav-arch #6`) — сторінка з кількома
   * НЕЗАЛЕЖНИМИ запитами показує ЧАСТИНУ даних, поки решта ще вантажиться чи
   * відмовила, а не гасить усю сторінку через одну секцію.
   *
   * ⛔ `HealthPage` монтує ДВІ окремі `<AsyncBoundary>` (`ready`/`db`) — саме
   * ця композиція, а не нове поле однієї межі, і є реалізацією `partial`
   * (див. коментар `AsyncBoundary.tsx`). Тест доводить це не оглядом коду, а
   * РІЗНИМИ відповідями на ДВА різні шляхи одночасно.
   */
  it('ФВ-B3 partial: /health/ready відмовляє, /health/db тим часом показує дані — обидві секції видно НЕЗАЛЕЖНО', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);

        if (url.includes('/health/ready')) {
          return new Response(
            JSON.stringify({
              title: 'Недоступно',
              status: 503,
              errorCode: 'ECR-SYS-0503',
              correlationId: 'cid-partial-1',
            }),
            { status: 503, headers: { 'Content-Type': 'application/json' } },
          );
        }

        if (url.includes('/health/db')) {
          return new Response(JSON.stringify(sample), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          });
        }

        return new Response(JSON.stringify(null), { status: 200 });
      }),
    );

    show();

    // ⛔ Головне твердження: ОБИДВІ секції видно ОДНОЧАСНО, кожна у СВОЄМУ
    // стані. Якби одна межа помилково гасила сусідню (спільний стан замість
    // двох незалежних `<AsyncBoundary>`), «effectiveMode» нижче не
    // з'явився б поруч із помилкою зверху.
    const alerts = await screen.findAllByRole('alert');
    expect(alerts.map((a) => a.textContent).join(' ')).toContain('ECR-SYS-0503');

    expect(await screen.findAllByText('⟦health.database.effectiveMode⟧')).not.toHaveLength(0);
    expect(await screen.findAllByText('Enterprise')).not.toHaveLength(0);
  });
});
