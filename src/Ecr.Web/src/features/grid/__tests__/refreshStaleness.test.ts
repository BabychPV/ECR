import { QueryClient, QueryObserver } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { calculationResultsKey } from '@/features/methodologies/calculationResultsKey';
import { refreshStaleness } from '../useCellPatch';

/**
 * RC15-C: банер «результати застаріли» з'являється без перезавантаження сторінки - збереження правки
 * інвалідує картку документа, але лише коли вона ще не застаріла (не `GET` на кожне автозбереження).
 */
function clientWith(summary: unknown): QueryClient {
  const client = new QueryClient();
  if (summary !== undefined) client.setQueryData(['document', 7, 202609], summary);
  client.setQueryData(calculationResultsKey(7, 202609), []);
  return client;
}

const invalidated = (client: QueryClient, key: readonly unknown[]): boolean =>
  client.getQueryState(key)?.isInvalidated === true;

describe('refreshStaleness', () => {
  it('картка свіжа (resultsStale=false) - картка й числа методологій інвалідуються', () => {
    const client = clientWith({ resultsStale: false });
    refreshStaleness(client, 7, 202609);
    expect(invalidated(client, ['document', 7, 202609])).toBe(true);
    expect(invalidated(client, calculationResultsKey(7, 202609))).toBe(true);
  });

  it.each([[{ resultsStale: true }], [{ resultsStale: null }], [{}], [undefined]])(
    'картка %j - запиту немає (уже показано або не знаємо)',
    (summary) => {
      const client = clientWith(summary);
      refreshStaleness(client, 7, 202609);
      expect(invalidated(client, ['document', 7, 202609])).toBe(false);
      expect(invalidated(client, calculationResultsKey(7, 202609))).toBe(false);
    },
  );

  it('чужий документ чи період не чіпається', () => {
    const client = clientWith({ resultsStale: false });
    refreshStaleness(client, 8, 202609);
    expect(invalidated(client, ['document', 7, 202609])).toBe(false);
  });

  // ⛔ Мутаційний доказ: прибери перший `invalidateQueries` у `refreshStaleness` - тест почервоніє.
  it.each([[{ resultsStale: true }], [undefined]])(
    'зведення переліку документів інвалідується завжди (картка %j) - чіп «Needs recalculation (N)»',
    (summary) => {
      const client = clientWith(summary);
      client.setQueryData(['documents', 'summary', 202609], { staleResultsCount: 0 });
      refreshStaleness(client, 7, 202609);
      expect(invalidated(client, ['documents', 'summary', 202609])).toBe(true);
    },
  );

  /**
   * AN-108 / P2-01: активний спостерігач зведення (бейдж меню на сторінці документа) НЕ перезапитує зведення на
   * кожне автозбереження — лише на перехід картки false → true.
   * ⛔ Мутаційний доказ: поверни безумовний `invalidateQueries` з `refetchType` за замовчуванням — перший кейс
   * почервоніє.
   */
  it.each([
    [{ resultsStale: true }, 0],
    [undefined, 0],
    [{ resultsStale: null }, 0],
    [{ resultsStale: false }, 1],
  ])('активне зведення, картка %j — перезапитів зведення: %i', async (summary, expected) => {
    const client = clientWith(summary);
    const fetchSummary = vi.fn(() => Promise.resolve({ staleResultsCount: 0 }));
    const observer = new QueryObserver(client, {
      queryKey: ['documents', 'summary', 202609],
      queryFn: fetchSummary,
      staleTime: 60_000,
    });
    const unsubscribe = observer.subscribe(() => undefined);
    await vi.waitFor(() => expect(fetchSummary).toHaveBeenCalledTimes(1));
    fetchSummary.mockClear();

    refreshStaleness(client, 7, 202609);
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(fetchSummary).toHaveBeenCalledTimes(expected);
    // Без перезапиту зведення все одно позначене застарілим — перелік перечитає його при монтуванні.
    if (expected === 0) expect(invalidated(client, ['documents', 'summary', 202609])).toBe(true);
    unsubscribe();
  });
});
