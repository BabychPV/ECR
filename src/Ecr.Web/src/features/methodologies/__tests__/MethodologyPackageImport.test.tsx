import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologyPackageImport } from '@/features/methodologies/MethodologyPackageImport';
import { testTheme } from '@/test/render';

/**
 * «Імпорт пакета» методологій (крок V, FEATURE-HSE301-VIEW §11.6).
 *
 * ⛔ Сервер — `fetch`-стаб: адреса з `dryRun` і відмова 422 проходять той самий
 * розбір (`apiFetch` → `EcrApiError` → `report`), що й у продукті.
 *
 * Мутаційні докази: звести умову кнопки «Імпортувати» до `pkg === null`
 * → червоний «запис недоступний до перевірки»; прибрати
 * `reportFromError` в `onError` → червоний «блокери з відмови 422 видно».
 */
const Report = {
  dryRun: true,
  applied: false,
  outcome: 'created',
  methodologies: [],
  blockers: [],
  conflicts: [],
  warnings: [],
  totals: { methodologiesToCreate: 2, versionsToCreate: 2, versionsUnchanged: 0, formulas: 2, constants: 1, imports: 1 },
};

const sent: { url: string; method: string }[] = [];

function mockServer(respond: (url: string) => { status: number; body: unknown; problem?: boolean }): void {
  sent.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      sent.push({ url: String(url), method: String(init?.method ?? 'GET') });
      const answer = respond(String(url));

      return new Response(JSON.stringify(answer.body), {
        status: answer.status,
        headers: { 'Content-Type': answer.problem === true ? 'application/problem+json' : 'application/json' },
      });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyPackageImport />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function choosePackage(): Promise<void> {
  fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.importPackage⟧' }));
  const input = await waitFor(() => {
    const found = document.querySelector('input[type="file"]');
    if (found === null) throw new Error('немає поля файлу');
    return found;
  });
  const file = new File([JSON.stringify({ format: 'ecr-methodology-package', version: 1 })], 'package.json', {
    type: 'application/json',
  });
  fireEvent.change(input, { target: { files: [file] } });
  await waitFor(() =>
    expect(screen.getByRole('button', { name: '⟦methodologies.importCheck⟧' }).hasAttribute('disabled')).toBe(false),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyPackageImport', () => {
  it('запис недоступний до перевірки; перевірка шле dryRun=true і показує підсумки', async () => {
    mockServer(() => ({ status: 200, body: Report }));
    show();
    await choosePackage();

    const apply = screen.getByRole('button', { name: '⟦methodologies.importApply⟧' });
    expect(apply.getAttribute('aria-disabled')).toBe('true');
    // ⚠ Недоступна, але пояснює чому, а клік до перевірки нічого не шле.
    expect(document.getElementById(apply.getAttribute('aria-describedby') ?? '')?.textContent).toBe(
      '⟦methodologies.importApplyBlocked⟧',
    );
    fireEvent.click(apply);
    expect(sent).toEqual([]);

    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.importCheck⟧' }));

    await screen.findByText('⟦methodologies.importOutcomeCreated⟧');
    expect(sent).toEqual([{ url: '/api/v1/methodologies/import?dryRun=true', method: 'POST' }]);
    expect(screen.getByRole('button', { name: '⟦methodologies.importApply⟧' }).getAttribute('aria-disabled')).toBeNull();
  });

  it('блокери з відмови 422 видно в діалозі', async () => {
    mockServer((url) =>
      url.endsWith('dryRun=true')
        ? { status: 200, body: Report }
        : {
            status: 422,
            problem: true,
            body: {
              title: 'Unprocessable',
              status: 422,
              errorCode: 'ECR-CALC-0422',
              messageKey: 'err.ECR-CALC-0422.methodologyImportBlocked',
              report: {
                ...Report,
                dryRun: false,
                outcome: 'blocked',
                blockers: [{ kind: 'unresolvedFormula', methodology: 'M1', version: 'V1', subject: 'F1', detail: '!Nowhere' }],
              },
            },
          },
    );
    show();
    await choosePackage();

    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.importCheck⟧' }));
    await screen.findByText('⟦methodologies.importOutcomeCreated⟧');
    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.importApply⟧' }));

    await screen.findByText('!Nowhere');
    expect(screen.getByText('⟦methodologies.importOutcomeBlocked⟧')).toBeDefined();
    expect(sent.at(-1)).toEqual({ url: '/api/v1/methodologies/import?dryRun=false', method: 'POST' });
  });
});

/**
 * Фокус після перевірки (WCAG 2.4.3): «Перевірити» на час запиту `loading` (= `disabled`) і втрачає фокус —
 * звіт забирає його собі, щоб читач почув підсумок, а `Tab` вів до блокерів.
 *
 * Мутаційний доказ (перевірено руками 2026-09-30): прибрати `reportRef.current?.focus()` → червоний.
 */
describe('MethodologyPackageImport: фокус', () => {
  it('звіт перевірки отримує фокус і має доступне ім’я — підсумок', async () => {
    mockServer(() => ({ status: 200, body: Report }));
    show();
    await choosePackage();

    const check = screen.getByRole('button', { name: '⟦methodologies.importCheck⟧' });
    check.focus();
    fireEvent.click(check);

    const report = await waitFor(() => {
      const found = document.querySelector<HTMLElement>('[data-import-report]');
      if (found === null) throw new Error('звіту ще немає');
      return found;
    });
    await waitFor(() => expect(document.activeElement).toBe(report));
    expect(report.getAttribute('aria-label')).toBe('⟦methodologies.importOutcomeCreated⟧');
  });
});

/**
 * L9-38: гонка «перевірка пакета A ↔ вибір пакета B».
 *
 * ⛔ Відповідь на перевірку A, що приїхала після вибору B, лягала звітом
 * `created` під B — і «Імпортувати» відкривалось для файлу, якого ніхто не
 * перевіряв. Поле файлу на час запиту вимкнене; `fireEvent.change` обходить
 * це навмисно, щоб довести й другий рубіж — звірення пакета в `onSuccess`.
 *
 * Мутаційні докази: `onSuccess: setReport` (без звірення з `pkgRef`) →
 * червоний «відповідь про попередній пакет…»; прибрати `disabled` з
 * `FileInput` → червоний «поле файлу вимкнене…».
 */
describe('MethodologyPackageImport: гонка перевірки й вибору файлу (L9-38)', () => {
  let release: (() => void) | null = null;

  function mockHeldServer(): void {
    sent.length = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        sent.push({ url: String(url), method: String(init?.method ?? 'GET') });
        await new Promise<void>((resolve) => {
          release = resolve;
        });

        return new Response(JSON.stringify(Report), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );
  }

  function fileInput(): HTMLInputElement {
    const found = document.querySelector<HTMLInputElement>('input[type="file"]');
    if (found === null) throw new Error('немає поля файлу');
    return found;
  }

  /**
   * Лічильник ЗАВЕРШЕНИХ читань файлу: `choose` читає пакет асинхронно через `FileReader`.
   *
   * ⛔ Не «Перевірити» стало доступним: поки перевірка A в польоті, після 100 мс кнопка отримує
   * `loading` (`usePendingLoading`, `ФВ-14.26`) і з ним `disabled` — до відповіді, яку тест тримає.
   * Швидкий прогін встигав за 100 мс, під навантаженням чекання падало `expected true to be false`.
   */
  function trackFileReads(): () => number {
    let loaded = 0;
    const readAsText = FileReader.prototype.readAsText;
    vi.spyOn(FileReader.prototype, 'readAsText').mockImplementation(function (this: FileReader, blob, encoding) {
      this.addEventListener('loadend', () => {
        loaded += 1;
      });
      readAsText.call(this, blob, encoding);
    });

    return () => loaded;
  }

  afterEach(() => {
    release = null;
    vi.restoreAllMocks();
  });

  it('поле файлу вимкнене, поки перевірка в польоті', async () => {
    mockHeldServer();
    show();
    await choosePackage();

    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.importCheck⟧' }));
    await waitFor(() => expect(sent).toHaveLength(1));

    // ⚠ Видима кнопка поля (Mantine), а не прихований `input[type=file]`: саме нею людина міняє файл.
    await waitFor(() =>
      expect((screen.getByLabelText('⟦methodologies.importFile⟧') as HTMLButtonElement).disabled).toBe(true),
    );
  });

  it('відповідь про попередній пакет не стає звітом нового і не відкриває «Імпортувати»', async () => {
    mockHeldServer();
    const fileReads = trackFileReads();
    show();
    await choosePackage();

    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.importCheck⟧' }));
    await waitFor(() => expect(release).not.toBeNull());

    const other = new File([JSON.stringify({ format: 'ecr-methodology-package', version: 2 })], 'other.json', {
      type: 'application/json',
    });
    fireEvent.change(fileInput(), { target: { files: [other] } });
    // Пакет B прочитано й розібрано (продовження `choose` після `loadend` — мікрозадачі, вони
    // встигають до наступної перевірки `waitFor`), і лише тоді відпускаємо відповідь про A.
    await waitFor(() => expect(fileReads()).toBe(2));

    release?.();
    // Даємо відповіді доїхати й осісти.
    await waitFor(() =>
      expect((screen.getByLabelText('⟦methodologies.importFile⟧') as HTMLButtonElement).disabled).toBe(false),
    );
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(screen.queryByText('⟦methodologies.importOutcomeCreated⟧')).toBeNull();
    expect(screen.getByRole('button', { name: '⟦methodologies.importApply⟧' }).getAttribute('aria-disabled')).toBe(
      'true',
    );
  });
});

