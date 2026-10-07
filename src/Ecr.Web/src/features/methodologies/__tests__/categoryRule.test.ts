import { afterEach, describe, expect, it, vi } from 'vitest';
import { EcrApiError } from '@/api/client';
import {
  deleteMethodologyCategoryRule,
  methodologyCategoryRule,
  saveMethodologyCategoryRule,
} from '@/features/methodologies/api';
import { PublishProblemKeys, publishProblemLines } from '@/features/methodologies/publishError';

/**
 * Правило категорії константи версії (L-2, `calc.CategoryRule`): адреса, МЕТОД і тіло.
 *
 * ⚠ Сервер на інший метод відповів би `405`, а серверні тести ходять у маршрут самі й цього не
 * побачили б; перевірка публікації називає проблеми правила ключами `publish.problem.categoryRule*`.
 */

const sent: { url: string; method: string; body: unknown }[] = [];

function mockServer(status: number, body?: unknown): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      sent.push({
        url: String(url),
        method: String(init?.method ?? 'GET'),
        body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined,
      });

      return body === undefined
        ? new Response(null, { status })
        : new Response(JSON.stringify(body), {
            status,
            headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
          });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('правило категорії константи версії', () => {
  it('читається GET-ом; відсутнє правило — expression = null', async () => {
    mockServer(200, { expression: null, updatedAt: null });

    const rule = await methodologyCategoryRule(3, 7);

    expect(rule.expression).toBeNull();
    expect(sent).toEqual([
      { url: '/api/v1/methodologies/3/versions/7/category-rule', method: 'GET', body: undefined },
    ]);
  });

  it('ставиться PUT-ом із виразом у тілі', async () => {
    mockServer(200, { expression: '!ECW_Category', updatedAt: '2026-10-07T09:00:00Z' });

    const rule = await saveMethodologyCategoryRule(3, 7, { expression: '!ECW_Category' });

    expect(rule.expression).toBe('!ECW_Category');
    expect(sent).toEqual([
      {
        url: '/api/v1/methodologies/3/versions/7/category-rule',
        method: 'PUT',
        body: { expression: '!ECW_Category' },
      },
    ]);
  });

  it('прибирається DELETE-ом на ту саму адресу', async () => {
    mockServer(204);

    await deleteMethodologyCategoryRule(3, 7);

    expect(sent).toEqual([
      { url: '/api/v1/methodologies/3/versions/7/category-rule', method: 'DELETE', body: undefined },
    ]);
  });

  it('відмова 422 доходить як EcrApiError з ключем причини', async () => {
    mockServer(422, {
      title: 'unprocessable',
      status: 422,
      errorCode: 'ECR-CALC-0422',
      correlationId: 'cid',
      messageKey: 'err.ECR-CALC-0422.categoryRuleNotText',
    });

    const error = await saveMethodologyCategoryRule(3, 7, { expression: '1 + 1' }).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(EcrApiError);
    expect((error as EcrApiError).problem.errorCode).toBe('ECR-CALC-0422');
  });
});

describe('проблеми публікації правила категорії', () => {
  it('усі чотири ключі відомі клієнтові, інакше пункт випав би з переліку', () => {
    expect(PublishProblemKeys).toEqual(
      expect.arrayContaining([
        'publish.problem.categoryRuleInvalid',
        'publish.problem.categoryRuleNotText',
        'publish.problem.categoryRuleUnknownConstant',
        'publish.problem.categoryRuleBadFormula',
      ]),
    );
  });

  it('пункт переліку з невідомим ключем не показується, відомий — показується', () => {
    const error = new EcrApiError({
      title: 'x',
      status: 422,
      errorCode: 'ECR-CALC-0422',
      correlationId: 'cid',
      problems: [
        { messageKey: 'publish.problem.categoryRuleBadFormula', args: { formula: 'T' } },
        { messageKey: 'publish.problem.unknownFuture', args: {} },
      ],
    } as never);

    expect(publishProblemLines(error)).toHaveLength(1);
  });
});
