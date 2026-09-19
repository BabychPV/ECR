import { afterEach, describe, expect, it, vi } from 'vitest';
import { QueryClient, QueryObserver, focusManager } from '@tanstack/react-query';
import type { DocumentTableDto } from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { createQueryClient } from '@/app/queryClient';
import { applySliceCachePolicy, invalidateSlices, markSlicesStale } from '../sliceCache';

/**
 * `CL-02` (`DIRECTIVE-14-ARCH.md` §3.5): інвалідація зрізів адресна, а
 * повернення до вкладки не перезапитує нічого.
 *
 * ⛔ Доказ — ЛІЧИЛЬНИК ЗАПИТІВ на кожен зріз окремо. «Швидше» тут нічого не
 * означає: вада в тому, що один імпорт піднімав до 91 читання зрізу, і
 * перевіряти треба саме це число.
 *
 * ⚠ Спостерігачі створюються напряму (`QueryObserver`), без React: активність
 * запиту — це наявність підписника, і рендер компонента тут нічого не додав
 * би, крім часу.
 */

const PeriodKey = 202609;
const OtherPeriodKey = 202512;
const DocumentId = 5;

/** Аркуш 1 — таблиці 700 і 701; аркуш 2 — таблиця 800. */
function documentTables(): DocumentTableDto[] {
  return [
    { sheetDefId: 1, tableInstanceId: 700 },
    { sheetDefId: 1, tableInstanceId: 701 },
    { sheetDefId: 2, tableInstanceId: 800 },
  ] as DocumentTableDto[];
}

interface Stand {
  client: QueryClient;
  /** Скільки разів запитували зріз таблиці. */
  reads: (tableInstanceId: number, periodKey?: number) => number;
  stop: () => void;
}

/**
 * ⚠ `null`, а не `undefined`, для «переліку таблиць у кеші немає»: явний
 * `undefined` в аргументі підставляє ЗНАЧЕННЯ ЗА ЗАМОВЧУВАННЯМ, тобто тест
 * «без переліку» мовчки отримував би перелік і перевіряв не те (перевірено:
 * саме так він і поводився, доки тут стояв `undefined`).
 *
 * ⛔ `policy` застосовується ДО створення спостерігачів, і це не порядок
 * рядків заради охайності: `setQueryDefaults` діє в мить, коли спостерігач
 * розв'язує свої опції. Політика, ввімкнена після `watch()`, не діє на вже
 * створені запити взагалі — і перевірка «не перезапитує на фокус» проходила б
 * навіть із увімкненим `refetchOnWindowFocus` (перевірено мутацією: так воно
 * й було).
 */
function stand(
  options: { tables?: DocumentTableDto[] | null; policy?: boolean } = {},
): Stand {
  const tables = 'tables' in options ? (options.tables ?? null) : documentTables();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const counts = new Map<string, number>();
  const unsubscribes: (() => void)[] = [];

  if (options.policy === true) applySliceCachePolicy(client);

  // ⛔ Без `mount()` клієнт не підписаний на `focusManager` взагалі, і подія
  // фокуса не доходить до жодного запиту: перевірка «не перезапитує на
  // фокус» проходила б завжди, хоч із політикою, хоч без неї. У застосунку це
  // робить `QueryClientProvider`.
  client.mount();

  if (tables !== null) {
    client.setQueryData(['document-tables', DocumentId, PeriodKey], tables);
  }

  const watch = (tableInstanceId: number, periodKey: number): void => {
    const id = `${String(tableInstanceId)}:${String(periodKey)}`;

    const observer = new QueryObserver(client, {
      queryKey: queryKeys.slices.one(tableInstanceId, periodKey),
      queryFn: () => {
        counts.set(id, (counts.get(id) ?? 0) + 1);

        return Promise.resolve({ tableInstanceId, periodKey });
      },
    });

    unsubscribes.push(observer.subscribe(() => undefined));
  };

  watch(700, PeriodKey);
  watch(701, PeriodKey);
  watch(800, PeriodKey);
  watch(900, OtherPeriodKey);

  return {
    client,
    reads: (tableInstanceId, periodKey = PeriodKey) =>
      counts.get(`${String(tableInstanceId)}:${String(periodKey)}`) ?? 0,
    stop: () => {
      for (const unsubscribe of unsubscribes) unsubscribe();
      client.unmount();
    },
  };
}

afterEach(() => {
  focusManager.setFocused(undefined);
  vi.unstubAllGlobals();
});

describe('CL-02 · адресна інвалідація', () => {
  it('інвалідація аркуша не чіпає зрізів інших аркушів і періодів', async () => {
    const { client, reads, stop } = stand();

    // Перше читання — монтування спостерігачів; далі рахуємо лише приріст.
    await vi.waitFor(() => expect(reads(700)).toBe(1));
    await vi.waitFor(() => expect(reads(800)).toBe(1));

    await invalidateSlices(client, { documentId: DocumentId, periodKey: PeriodKey, sheetDefId: 1 });

    expect(reads(700)).toBe(2);
    expect(reads(701)).toBe(2);

    // ⛔ Ядро перевірки: сусідній аркуш і сусідній період не запитувалися.
    expect(reads(800)).toBe(1);
    expect(reads(900, OtherPeriodKey)).toBe(1);

    stop();
  });

  it('зріз поза аркушем позначено застарілим — він перечитається при наступному монтуванні', async () => {
    const { client, reads, stop } = stand();
    await vi.waitFor(() => expect(reads(800)).toBe(1));

    await invalidateSlices(client, { documentId: DocumentId, periodKey: PeriodKey, sheetDefId: 1 });

    const other = client
      .getQueryCache()
      .find({ queryKey: queryKeys.slices.one(800, PeriodKey) });

    expect(other?.isStale()).toBe(true);
    expect(reads(800)).toBe(1);

    stop();
  });

  it('імпорт без аркуша бере всі таблиці документа того самого періоду', async () => {
    const { client, reads, stop } = stand();
    await vi.waitFor(() => expect(reads(800)).toBe(1));

    await invalidateSlices(client, { documentId: DocumentId, periodKey: PeriodKey });

    expect(reads(700)).toBe(2);
    expect(reads(800)).toBe(2);
    expect(reads(900, OtherPeriodKey)).toBe(1);

    stop();
  });

  it('без переліку таблиць у кеші намір звужується періодом, а не зникає', async () => {
    const { client, reads, stop } = stand({ tables: null });
    await vi.waitFor(() => expect(reads(700)).toBe(1));

    await invalidateSlices(client, { documentId: DocumentId, periodKey: PeriodKey, sheetDefId: 1 });

    expect(reads(700)).toBe(2);
    expect(reads(800)).toBe(2);

    // Інший період лишається незайманим навіть без переліку.
    expect(reads(900, OtherPeriodKey)).toBe(1);

    stop();
  });

  it('перерахунок проєкту позначає зрізи застарілими, не запитуючи жодного', async () => {
    const { client, reads, stop } = stand();
    await vi.waitFor(() => expect(reads(700)).toBe(1));

    await markSlicesStale(client);

    expect(reads(700)).toBe(1);
    expect(reads(800)).toBe(1);
    expect(reads(900, OtherPeriodKey)).toBe(1);

    expect(
      client.getQueryCache().find({ queryKey: queryKeys.slices.one(700, PeriodKey) })?.isStale(),
    ).toBe(true);

    stop();
  });
});

describe('CL-02 · політика кешу зрізів', () => {
  it('зріз не перезапитується на поверненні до вкладки, хоча й застарілий', async () => {
    const { client, reads, stop } = stand({ policy: true });

    await vi.waitFor(() => expect(reads(700)).toBe(1));

    // Зріз явно застарілий — тобто єдине, що стримує перезапит, це сама
    // політика, а не свіжість даних.
    await markSlicesStale(client);
    expect(reads(700)).toBe(1);

    focusManager.setFocused(false);
    focusManager.setFocused(true);

    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(reads(700)).toBe(1);
    expect(reads(800)).toBe(1);

    stop();
  });

  it('політику застосовано до КЛІЄНТА застосунку, а не лише оголошено', () => {
    /*
     * ⛔ Сторож був ПО ТЕКСТУ `App.tsx` (`toContain('applySliceCachePolicy(
     * queryClient)')`) — і причина була поважна: створення клієнта жило
     * модульною змінною в `App.tsx`, дістати його з тесту було нічим, а
     * монтувати `App` разом із роутером і всіма сторінками заради однієї
     * перевірки — задорого.
     *
     * ⚠ Після `UI-00` клієнт створює `createQueryClient()`, і підпірка більше
     * не потрібна: тест бере рівно той екземпляр, який отримує
     * `QueryClientProvider`, і питає в нього САМУ політику. Текстовий сторож
     * лишився б зеленим після перейменування виклику; цей — ні.
     */
    const client = createQueryClient();

    const forSlice = client.getQueryDefaults(queryKeys.slices.one(700, PeriodKey));
    expect(forSlice.staleTime).toBeGreaterThanOrEqual(5 * 60_000);
    expect(forSlice.refetchOnWindowFocus).toBe(false);
  });

  it('свіжість зрізу — не менше п’яти хвилин, і лише для зрізів', () => {
    const client = new QueryClient();
    applySliceCachePolicy(client);

    const forSlice = client.getQueryDefaults(queryKeys.slices.one(700, PeriodKey));
    expect(forSlice.staleTime).toBeGreaterThanOrEqual(5 * 60_000);
    expect(forSlice.refetchOnWindowFocus).toBe(false);

    // ⚠ Політика адресна: перелік документів або довідник від неї не
    // застигають на п'ять хвилин.
    expect(client.getQueryDefaults(['documents']).refetchOnWindowFocus).toBeUndefined();
    expect(client.getQueryDefaults(queryKeys.registries.list()).staleTime).toBeUndefined();
  });
});
