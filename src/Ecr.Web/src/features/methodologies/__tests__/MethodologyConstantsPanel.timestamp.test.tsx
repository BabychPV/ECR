import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { formatDate } from '@/shared/format';
import { testTheme } from '@/test/render';

/**
 * `UI-07`: вікно дії константи — читабельне на екрані, точне в розмітці, і
 * «без межі» не прикидається «немає значення».
 *
 * ⛔ Три твердження, і жодне не випливає з решти:
 *   1. видимий текст НЕ дорівнює сирому входу (`2024-01-01` → «Jan 1, 2024»);
 *   2. точне значення лишилося в розмітці (`<time datetime>`), тобто нічого
 *      не втрачено для копіювання, звірки й локатора e2e;
 *   3. порожня межа показується НЕ тире. Контракт каже прямо: `validFrom:
 *      null` — «від початку», `validTo: null` — «без межі»
 *      (`MethodologyConstantDto`). Константа з порожнім `validTo` чинна й
 *      далі, і тире тут збрехало б у найдорожчий бік: людина шукає, чому
 *      коефіцієнт не підставляється, а комірка каже «тут порожньо».
 *
 * ⚠ Очікуваний текст береться з `shared/format`, а НЕ пишеться літералом:
 * літерал був би перевіркою версії ICU у Node.
 */

const ValidFrom = '2024-01-01';

/** Перший НЕчинний день — межа ВИКЛЮЧНА (`saveMethodologyConstant`). */
const ValidTo = '2025-01-01';

const Bounded = {
  id: 1,
  code: 'GWP_CH4',
  kind: 'Numeric',
  value: 28,
  textValue: null,
  unitId: null,
  category: null,
  source: null,
  isResolved: true,
  validFrom: ValidFrom,
  validTo: ValidTo,
};

/** Та сама константа без обох меж: чинна від початку й безстроково. */
const Unbounded = {
  ...Bounded,
  id: 2,
  code: 'GWP_N2O',
  value: 265,
  validFrom: null,
  validTo: null,
};

/**
 * Заглушка мережі.
 *
 * ⚠ Форма звірена з `methodologyConstants()`: `apiFetch<MethodologyConstantDto[]>`
 * — МАСИВ, а не `{ items: [...] }`. Заглушка не тієї форми дала б падіння за
 * таймаутом зовсім не з тієї причини, яку перевіряє набір.
 */
function mockApi(constants: readonly unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.endsWith('/api/v1/methodologies/1/versions/10/constants')) return json(constants);
      if (url.includes('/api/v1/units')) return json([]);

      return json(null);
    }),
  );
}

async function show(): Promise<void> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { MethodologyConstantsPanel } = await import('../MethodologyContentPanels');

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyConstantsPanel methodologyId={1} versionId={10} editable={false} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Вузол `<time>` із точно цим `dateTime`. */
function timeNode(iso: string): HTMLElement | null {
  return document.querySelector(`time[datetime="${iso}"]`);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('MethodologyConstantsPanel: вікно дії константи', () => {
  it(
    'обидві межі читабельні на екрані й точні в розмітці — без вигаданої години',
    async () => {
      mockApi([Bounded]);
      await show();

      await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      const from = timeNode(ValidFrom);
      expect(from, 'початок вікна дії не намальовано елементом <time>').not.toBeNull();
      expect(from?.textContent).toBe(formatDate(ValidFrom));
      expect(from?.textContent).not.toBe(ValidFrom);

      const to = timeNode(ValidTo);
      expect(to, 'кінець вікна дії не намальовано елементом <time>').not.toBeNull();
      expect(to?.textContent).toBe(formatDate(ValidTo));
      expect(to?.textContent).not.toBe(ValidTo);

      /*
       * ⚠ `dateOnly` в обох: вікно дії порівнюється з ДНЕМ періоду, а `validTo`
       * — перший НЕчинний день (виключна межа). Година зробила б виключну межу
       * схожою на момент, тобто підказувала б, що опівдні 2025-01-01 константа
       * ще діє.
       */
      expect(from?.textContent).not.toMatch(/\d{1,2}:\d{2}/);
      expect(to?.textContent).not.toMatch(/\d{1,2}:\d{2}/);

      // ⛔ Друга половина: точне значення лишилось у розмітці.
      expect(from?.getAttribute('title')).toBe(ValidFrom);
      expect(to?.getAttribute('title')).toBe(ValidTo);
    },
    SlowEnvTimeout,
  );

  it(
    'межі немає — «…», а не тире: константа чинна далі, а не «без значення»',
    async () => {
      mockApi([Unbounded]);
      await show();

      await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      const empties = document.querySelectorAll('[data-timestamp="none"]');

      // Дві порожні межі — початок і кінець вікна дії.
      expect(empties).toHaveLength(2);
      for (const node of empties) {
        expect(node.textContent).toBe('…');
      }

      /*
       * ⛔ Саме це твердження і є предметом рішення: дефолт `Timestamp` — тире,
       * і воно тут НЕ використане. Поверніть `fallback` на дефолт — тест упаде.
       */
      expect(screen.queryByText('—')).toBeNull();
      expect(document.querySelector('time')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
