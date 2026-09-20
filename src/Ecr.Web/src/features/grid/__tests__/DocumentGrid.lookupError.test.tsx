import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Відмова довідника в комірці `Lookup` виглядала як «довідник не наповнили».
 *
 * ⛔ Це єдине місце класу «невдалий запит = даних немає», яке щодня бачить
 * ОПЕРАТОР, а не адміністратор. Відмова `GET /api/v1/registries` (чи
 * `…/entries`) робила випадний список у комірці порожнім і мовчала. Оператор
 * читав це як «конфігуратор не завів записи» — і далі або зупиняв заповнення
 * з неправдивою скаргою, або, оскільки комірка деградує до текстового поля,
 * вписував значення руками: у документ їхав рядок, якого в довіднику немає.
 *
 * ⚠ Деградація сама по собі ПРАВИЛЬНА і залишена: коментар у `gridColumns`
 * пояснює, що редактор не має падати, поки довідник їде або поки колонку
 * налаштовано без нього. Дефект був у тому, що третій випадок — сервер
 * ВІДМОВИВ — потрапляв у ту саму гілку без жодного сліду на екрані.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => <div data-testid="revogrid-stub" />,
}));

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [
      {
        code: 'SRC',
        dataType: 'Lookup',
        defaultValue: null,
        displayFormat: null,
        header: 'Джерело',
        id: 1,
        isReadOnly: false,
        isRequired: false,
        isRequiredByMethodology: false,
        // ⚠ Саме непорожній `lookupRegistryDefId`: без нього сітка не запитує
        // довідників узагалі, і тест лишався б зеленим на будь-якому коді.
        lookupRegistryDefId: 42,
        ordinal: 0,
        unitId: null,
        unitSymbol: null,
      },
    ],
    rows: [
      {
        cells: { SRC: null },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowKind: 'Item',
        rowVersion: 'v1',
      },
    ],
  };
}

const RegistriesRefusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'перелік довідників прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-lookup-1',
  // ⚠ Без цієї ознаки подробиця до екрана не доходить (рішення людини про
  // мову, `problemText.ts`) — а тест саме про те, що людина бачить причину.
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

function mockServer(registriesFail: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/registries')) {
        return registriesFail
          ? new Response(JSON.stringify(RegistriesRefusal), {
              status: 500,
              headers: { 'Content-Type': 'application/problem+json' },
            })
          : new Response(JSON.stringify([{ id: 42, code: 'SOURCES', nameL10n: { values: {} } }]), {
              status: 200,
              headers: { 'Content-Type': 'application/json' },
            });
      }

      if (path.includes('/api/v1/registries/') && path.endsWith('/entries')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(sliceFixture()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: відмова довідника не виглядає як «довідник порожній»', () => {
  it('сервер відмовив — на екрані причина з кодом, а не мовчазний порожній список', async () => {
    mockServer(true);
    show();

    await screen.findByTestId('revogrid-stub');

    /*
     * ⛔ Головне твердження. До правки тут не було НІЧОГО: відмова
     * `registriesList` лишала `lookupEntriesByRegistryId` порожньою мапою, і
     * єдина `AsyncBoundary` цієї сітки стереже лише `slice.error`, тобто зовсім
     * інший запит.
     */
    const alert = await waitFor(() => screen.getByRole('alert'));

    expect(alert.textContent ?? '').toContain('перелік довідників прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
  });

  it('сітка при цьому ЛИШАЄТЬСЯ робочою — відмова довідника не забирає таблицю', async () => {
    /*
     * ⚠ Дзеркало, і воно тут не формальне. Спокуса «показати помилку» —
     * підняти її в `AsyncBoundary` і сховати сітку цілком. Це було б гірше за
     * мовчання: одна колонка з довідником зробила б увесь аркуш
     * незаповнюваним, хоч решта колонок працює.
     */
    mockServer(true);
    show();

    /*
     * ⛔ Порядок тут — половина доказу, і перша редакція цього випадку його не
     * мала. Я просто питав, чи є сітка, — і вона була, бо на той момент
     * відмова довідника ще не приїхала. Мутація «підняти `lookupError` у
     * `AsyncBoundary`» (тобто сховати сітку цілком) лишала тест ЗЕЛЕНИМ.
     *
     * ⚠ Тому спершу чекаємо на банер — це і є момент, коли відмова вже в
     * стані, — і лише ПІСЛЯ цього питаємо про сітку.
     */
    await waitFor(() => screen.getByRole('alert'));

    expect(screen.getByTestId('revogrid-stub')).toBeTruthy();
    expect(screen.getByRole('button', { name: /grid\.save/ })).toBeTruthy();
  });

  it('довідник приїхав — банера немає', async () => {
    mockServer(false);
    show();

    await screen.findByTestId('revogrid-stub');

    // ⚠ Дочекатися саме тиші: `getByRole` одразу після монтування був би
    // зеленим і на зламаному коді, бо запит ще в дорозі.
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /grid\.save/ })).toBeTruthy();
    });

    expect(screen.queryByRole('alert')).toBeNull();
  });
});
