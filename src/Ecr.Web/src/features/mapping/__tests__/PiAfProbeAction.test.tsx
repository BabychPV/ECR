import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { vi } from 'vitest';
import { CreateMappingModal } from '@/features/mapping/CreateMappingModal';
import { testTheme } from '@/test/render';

/**
 * «Перевірити» біля поля шляху мапінгу (`ФВ-13.17`).
 *
 * ⛔ Що саме перевіряється, і чому кожне з цього не видно з екрана:
 *  1. **Право.** Кнопки немає без `Integration.Manage` — те саме право, що
 *     вимагає сервер на `POST /data-sources/{id}/probe`.
 *  2. **`hasValue: true`** доводить пробу до кінця: значення, одиниця,
 *     мітка часу й якість — усе, що прийшло з реального джерела.
 *  3. **`hasValue: false`** — це НЕ порожнеча без пояснення: шлях
 *     підтверджений каталогом, просто в пробному вікні немає точок.
 *  4. **`404` із підказками** — клікабельний перелік, який підставляє
 *     значення назад у поле шляху; порожній перелік (`L10`) не малює блок.
 *  5. **`503`/`502`** — стан ДЖЕРЕЛА, а не помилка форми: під полем шляху
 *     таке читалося б як «ти ввела не те», хоча виправити це в полі
 *     неможливо.
 *  6. **`422`** показується під полем — звичайною відмовою запиту.
 */

const DataSourceId = 7;

interface ProbeCall {
  readonly path: string;
}

const probeCalls: ProbeCall[] = [];
let meCalls = 0;

/** Що віддає сервер на пробу; задається кожним тестом окремо. */
let respond: (path: string) => Response = () => probeOk({ hasValue: false });

function json(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** `200` у формі контракту `SourcePathProbeResult`. */
function probeOk(partial: {
  hasValue: boolean;
  valueNumeric?: string | null;
  valueString?: string | null;
  unitSymbol?: string | null;
  timestamp?: string | null;
  quality?: string | null;
}): Response {
  return json(
    {
      path: 'Plant/Unit1|CO2',
      hasValue: partial.hasValue,
      valueNumeric: partial.valueNumeric ?? null,
      valueString: partial.valueString ?? null,
      unitSymbol: partial.unitSymbol ?? null,
      timestamp: partial.timestamp ?? null,
      quality: partial.quality ?? null,
    },
    200,
  );
}

/** Локалізоване сервером речення відмови — показуване лише за `messageKey`. */
const OutageDetail = 'Data source "PI-MAIN" is unavailable, so the probe could not run. Try again later.';

/** `503`/`502` у формі, якою їх віддає `ExceptionHandlingMiddleware`. */
function outage(status: number, errorCode: string): Response {
  return json(
    {
      title: 'Source unavailable',
      status,
      detail: OutageDetail,
      errorCode,
      correlationId: 'cid-test',
      messageKey: 'err.ECR-INT-0503.probeUnavailable',
      code: 'PI-MAIN',
      timeoutSeconds: 10,
    },
    status,
  );
}

const NotFoundDetail = 'The path "Plant/Unit1|CO3" was not found in data source "PI-MAIN". Check the suggested names.';

/** `404` із підказками найближчих імен — той самий склад, що `ProbeSourcePathHandler`. */
function notFound(suggestions: readonly string[]): Response {
  return json(
    {
      title: 'Path not found',
      status: 404,
      detail: NotFoundDetail,
      errorCode: 'ECR-INT-0404',
      correlationId: 'cid-test',
      messageKey: 'err.ECR-INT-0404.sourcePathNotFound',
      path: 'Plant/Unit1|CO3',
      code: 'PI-MAIN',
      suggestions,
    },
    404,
  );
}

const InvalidDetail = 'A probe path is required, from 1 to 500 characters.';

/** `422` — шлях проби не пройшов валідацію (`ProbeSourcePathHandler.MaxPathLength`). */
function invalid(): Response {
  return json(
    {
      title: 'Invalid request',
      status: 422,
      detail: InvalidDetail,
      errorCode: 'ECR-REQ-0422',
      correlationId: 'cid-test',
      messageKey: 'err.ECR-REQ-0422.probePathInvalid',
      code: null,
    },
    422,
  );
}

function stubFetch(permissions: readonly string[]): void {
  probeCalls.length = 0;
  meCalls = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');

      if (url.pathname === '/api/v1/me') {
        meCalls += 1;

        return json(
          { userId: 1, userName: 'tester', language: 'en', permissions, isSimulation: false },
          200,
        );
      }

      if (url.pathname === `/api/v1/data-sources/${DataSourceId}/probe` && init?.method === 'POST') {
        const body = JSON.parse(String(init.body)) as { path: string };
        probeCalls.push({ path: body.path });

        return respond(body.path);
      }

      // Каталог PI AF рендериться в тій самій формі — порожня сторінка,
      // щоб кнопка каталогу не заважала пробі.
      if (url.pathname === `/api/v1/data-sources/${DataSourceId}/catalog`) {
        return json({ items: [], nextCursor: null }, 200);
      }

      return json({ id: 1 }, 200);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <CreateMappingModal
          sourceEntityId={5}
          dataSourceId={DataSourceId}
          opened
          onClose={() => {}}
          onCreated={() => {}}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const ProbeLabel = '⟦mapping.probeAction⟧';
const RetryLabel = '⟦common.retry⟧';
const FieldLabel = '⟦mapping.createField⟧';

async function settle(): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => {
      setTimeout(resolve, 0);
    });
  });
}

/** Вписує шлях у поле й запускає пробу. */
async function runProbe(path: string): Promise<void> {
  const field = await screen.findByLabelText(FieldLabel);
  fireEvent.change(field, { target: { value: path } });
  fireEvent.click(screen.getByRole('button', { name: ProbeLabel }));
}

beforeEach(() => {
  respond = () => probeOk({ hasValue: false });
});

afterEach(() => {
  vi.unstubAllGlobals();
  cleanup();
});

describe('Проба шляху мапінгу (ФВ-13.17)', () => {
  it('кнопка є з правом Integration.Manage і зникає без нього', async () => {
    stubFetch(['Integration.Manage']);
    show();

    expect(await screen.findByRole('button', { name: ProbeLabel })).toBeDefined();

    cleanup();
    stubFetch([]);
    show();

    // ⛔ Мутаційний доказ: якщо право не перевіряється, кнопка з'являється ще
    // ДО приходу профілю (і лишається без нього) — цей рядок червоніє.
    await waitFor(() => {
      expect(meCalls).toBe(1);
    });
    await settle();

    expect(screen.queryByRole('button', { name: ProbeLabel })).toBeNull();
  });

  it('порожній шлях — кнопка неактивна, проби немає', async () => {
    stubFetch(['Integration.Manage']);
    show();

    const button = await screen.findByRole('button', { name: ProbeLabel });
    expect((button as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(button);
    await settle();

    expect(probeCalls).toHaveLength(0);
  });

  it('hasValue: true — значення, одиниця, мітка часу й якість', async () => {
    stubFetch(['Integration.Manage']);
    respond = () =>
      probeOk({
        hasValue: true,
        valueNumeric: '12.4',
        unitSymbol: 't/h',
        timestamp: '2026-09-19T18:51:58Z',
        quality: 'Good',
      });

    show();
    await runProbe('Plant/Unit1|CO2');

    await waitFor(() => {
      expect(document.querySelector('[data-probe-state="value"]')).not.toBeNull();
    });

    expect(screen.getByText(/12\.4/)).toBeDefined();
    expect(screen.getByText(/t\/h/)).toBeDefined();
    expect(screen.getByText(/Good/)).toBeDefined();
    expect(document.querySelector('time[datetime="2026-09-19T18:51:58Z"]')).not.toBeNull();

    expect(probeCalls[0]?.path).toBe('Plant/Unit1|CO2');
  });

  it('hasValue: false — «даних немає», а не порожній чи хибний результат', async () => {
    stubFetch(['Integration.Manage']);
    respond = () => probeOk({ hasValue: false });

    show();
    await runProbe('Plant/Unit1|Idle');

    await waitFor(() => {
      expect(document.querySelector('[data-probe-state="no-data"]')).not.toBeNull();
    });

    // ⛔ Дзеркальний доказ: текст саме пояснює, ЩО сталося (шлях є, точок
    // немає), а не мовчить і не показує генеричну помилку.
    expect(screen.getByText('⟦mapping.probeNoData⟧')).toBeDefined();
    expect(document.querySelector('[data-probe-state="value"]')).toBeNull();
    expect(document.querySelector('[data-probe-state="unavailable"]')).toBeNull();
    expect(document.querySelector('[data-probe-state="not-found"]')).toBeNull();
  });

  it('404 із підказками — перелік клікабельний і підставляє значення в поле', async () => {
    stubFetch(['Integration.Manage']);
    respond = () => notFound(['CO2', 'CO', 'CO2Flow']);

    show();
    await runProbe('Plant/Unit1|CO3');

    await waitFor(() => {
      expect(document.querySelector('[data-probe-state="not-found"]')).not.toBeNull();
    });

    expect(screen.getByText(NotFoundDetail)).toBeDefined();

    const suggestion = screen.getByRole('button', { name: 'CO2' });
    fireEvent.click(suggestion);

    // ⛔ Мутаційний доказ: клік, що нічого не підставляє, лишає поле зі
    // старим (неправильним) шляхом — цей рядок червоніє.
    await waitFor(() => {
      expect((screen.getByLabelText(FieldLabel) as HTMLInputElement).value).toBe('CO2');
    });
  });

  it('404 без підказок (L10) — блок підказок не малюється', async () => {
    stubFetch(['Integration.Manage']);
    respond = () => notFound([]);

    show();
    await runProbe('Plant/Unit1|Ghost');

    await waitFor(() => {
      expect(document.querySelector('[data-probe-state="not-found"]')).not.toBeNull();
    });

    expect(document.querySelector('[data-probe-suggestions]')).toBeNull();
  });

  it('503 — стан «джерело не відповідає», а не помилка під полем', async () => {
    stubFetch(['Integration.Manage']);
    respond = () => outage(503, 'ECR-INT-0503');

    show();
    await runProbe('Plant/Unit1|CO2');

    await waitFor(() => {
      expect(document.querySelector('[data-probe-state="unavailable"]')).not.toBeNull();
    });

    expect(screen.getByText(OutageDetail)).toBeDefined();

    // ⛔ Мутаційний доказ: показ як помилки поля поставив би на нього
    // `aria-invalid="true"` й опис помилки — обидва рядки нижче червоніють.
    const field = screen.getByLabelText(FieldLabel);
    expect(field.getAttribute('aria-invalid')).toBe('false');
    expect(field.getAttribute('aria-describedby')).toBeNull();
    expect(document.querySelector('[data-probe-state="not-found"]')).toBeNull();
    expect(document.querySelector('[data-probe-state="value"]')).toBeNull();

    const before = probeCalls.length;
    fireEvent.click(screen.getByRole('button', { name: RetryLabel }));

    await waitFor(() => {
      expect(probeCalls.length).toBeGreaterThan(before);
    });
  });

  it('502 — та сама подача: джерело відмовило в автентифікації', async () => {
    stubFetch(['Integration.Manage']);
    respond = () => outage(502, 'ECR-INT-0502');

    show();
    await runProbe('Plant/Unit1|CO2');

    await waitFor(() => {
      expect(document.querySelector('[data-probe-state="unavailable"]')).not.toBeNull();
    });

    expect(screen.getByRole('button', { name: RetryLabel })).toBeDefined();
    expect(screen.getByLabelText(FieldLabel).getAttribute('aria-invalid')).toBe('false');
  });

  it('422 — показано під полем шляху', async () => {
    stubFetch(['Integration.Manage']);
    respond = () => invalid();

    show();
    await runProbe('x'.repeat(600));

    await waitFor(() => {
      expect(screen.getByText(InvalidDetail)).toBeDefined();
    });

    expect(screen.getByText('ECR-REQ-0422')).toBeDefined();
    expect(document.querySelector('[data-probe-state="unavailable"]')).toBeNull();
    expect(document.querySelector('[data-probe-state="not-found"]')).toBeNull();
  });
});
