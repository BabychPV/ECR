import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HealthPage } from '@/pages/admin/HealthPage';
import { statusTable } from '@/shared/ui/StatusBadge';

/**
 * Статус здоров'я фарбує набір, а не сама сторінка.
 *
 * ⛔ Тут стояла власна `badgeColor(status)`: `Healthy → statusSuccess`,
 * `Degraded → statusWarning`, **усе інше → `statusError`**. Остання гілка і є
 * дефектом, який набір закриває: `HealthReportDto.status` доходить до клієнта
 * простим `string` (`schema.d.ts:9401`), тож будь-який новий стан платформи
 * (`Starting`, `Unknown`) приїде без жодної скарги компілятора — і сторінка
 * мовчки оголосила б його ВІДМОВОЮ. Червоний бейдж посилає оператора шукати
 * несправність, якої немає; правдиве твердження тут — «увага, розберіться»
 * (`UnknownStateTone`), і розбиратися треба з тим, що клієнт відстав.
 *
 * ⚠ Тон читається з розмітки (`data-status-tone`), а не з кольору CSS: колір —
 * наслідок тону через `toneFills`, і перевіряти його означало б перевіряти
 * Mantine. Контраст самих тонів стереже
 * `shared/ui/__tests__/statusBadgeContrast.test.ts`.
 */

/**
 * ⚠ Усі три відомі стани ПЛЮС невідомий — одночасно, в одній відповіді.
 * Тест на самому `Healthy` лишався б зеленим і тоді, коли всі стани стали
 * нейтральними; доказ — саме РОЗРІЗНЕННЯ.
 *
 * ⚠ Зведений статус звіту (`Degraded`) навмисно не збігається з жодним
 * статусом перевірок: інакше `querySelector` за станом не сказав би, ЯКИЙ із
 * двох бейджів він знайшов — шапки чи картки.
 */
const report = {
  status: 'Degraded',
  totalDurationMs: 3,
  checks: [
    { name: 'db', status: 'Healthy', description: 'База доступна.', durationMs: 1, data: {} },
    { name: 'quartz', status: 'Unhealthy', description: 'Планувальник стоїть.', durationMs: 1, data: {} },
    { name: 'piaf', status: 'Starting', description: 'Адаптер піднімається.', durationMs: 1, data: {} },
  ],
};

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      new Response(JSON.stringify(report), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
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

function badgeOf(state: string): Element | null {
  return document.querySelector(`[data-status-state="${state}"]`);
}

function toneOf(state: string): string | null {
  return badgeOf(state)?.getAttribute('data-status-tone') ?? null;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('HealthPage: тон статусу приходить із набору', () => {
  it('Unhealthy — відмова, Degraded — попередження, Healthy — нейтрально', async () => {
    respond();
    show();

    await screen.findByText('quartz');

    // ⛔ Мутаційний доказ (RED, якщо повернути `badgeColor`): локальний помічник
    // не лишає в розмітці ані `data-status-tone`, ані `data-status-state`, тож
    // кожен рядок нижче стає `expected null to be …`.
    expect(toneOf('Unhealthy')).toBe('danger');
    expect(toneOf('Degraded')).toBe('warning');
    expect(toneOf('Healthy')).toBe('neutral');
  });

  it('стан, якого набір не знає, — попередження, а не оголошена відмова', async () => {
    /*
     * ⛔ Не гіпотеза: `HealthReportDto.status` — `string`. Саме на цій гілці
     * стара `badgeColor` і брехала: `return 'statusError'` за замовчуванням
     * означав «система впала» для стану, про який сторінка просто нічого не
     * знає.
     */
    expect(statusTable.health['Starting'], 'фікстура має бути СПРАВДІ невідомим станом').toBeUndefined();

    respond();
    show();

    await screen.findByText('piaf');

    expect(toneOf('Starting')).toBe('warning');
    expect(toneOf('Starting')).not.toBe('danger');
    expect(badgeOf('Starting')?.getAttribute('data-status-known')).toBe('false');

    // Відомий стан позначений відомим — атрибут не константа.
    expect(badgeOf('Healthy')?.getAttribute('data-status-known')).toBe('true');
  });

  it('підпис бере рядок каталогу, а не друкує код сервера', async () => {
    respond();
    show();

    await screen.findByText('quartz');

    /*
     * ⚠ Цей файл каталогу не завантажує, тож `t()` віддає позначений ключ
     * (`D-138`) — і саме ключ є доказом, що підпис пройшов через `t()`, а не
     * через `{check.status}`. Раніше на екрані стояв сирий код сервера; тепер
     * рядок береться з `09-seed.sql` (`status.health.*`), а те, що він там є,
     * доводить `shared/ui/__tests__/StatusBadge.test.tsx` на справжньому сіді.
     */
    expect(badgeOf('Unhealthy')?.textContent).toBe('⟦status.health.Unhealthy⟧');
    expect(badgeOf('Unhealthy')?.textContent).not.toBe('Unhealthy');
  });
});
