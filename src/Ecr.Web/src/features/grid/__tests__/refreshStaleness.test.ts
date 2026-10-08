import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
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
});
