import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TemplateCard } from '@/api/types';
import {
  archiveTemplate,
  dependentWorkCount,
  fetchTemplateCard,
  renameTemplate,
  restoreTemplate,
} from '../templateApi';

/**
 * Директива №15, `BE-26`: картка шаблону, архівування, лічильник залежних.
 *
 * ⚠ Тут доводиться рівно те, за що відповідає клієнт: МЕТОД і АДРЕСА кожної
 * дії. Правила «архівований не пропонується» і «повторне архівування — 409»
 * ухвалює сервер, і перевіряються вони на живому HTTP
 * (`tests/Ecr.Api.Tests/TemplateCardTests.cs`) — повторювати їх тут на
 * замокненому `fetch` означало б перевіряти власну заглушку.
 */

function card(overrides: Partial<TemplateCard> = {}): TemplateCard {
  return {
    id: 7,
    code: 'ECR_AIR',
    nameL10n: { values: { en: 'Air emissions' } },
    isActive: true,
    createdAt: '2026-01-01T00:00:00Z',
    dependents: { versions: 3, publishedVersions: 2, projects: 4, documents: 91 },
    ...overrides,
  };
}

function mockServer(): { calls: { url: string; method: string; body: unknown }[] } {
  const calls: { url: string; method: string; body: unknown }[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init?: RequestInit) => {
      calls.push({
        url,
        method: init?.method ?? 'GET',
        body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined,
      });

      return Promise.resolve(
        new Response(JSON.stringify(card()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );

  return { calls };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('templateApi: адреса й метод кожної дії картки', () => {
  it('картка читається GET-ом за ідентифікатором шаблону', async () => {
    const { calls } = mockServer();

    const result = await fetchTemplateCard(7);

    expect(calls).toHaveLength(1);
    expect(calls[0]?.url).toBe('/api/v1/templates/7');
    expect(calls[0]?.method).toBe('GET');

    // Лічильник приходить ТІЄЮ САМОЮ відповіддю — другого запиту немає.
    expect(result.dependents.documents).toBe(91);
  });

  it('перейменування — PUT із самою лише назвою, без коду', async () => {
    const { calls } = mockServer();

    await renameTemplate(7, { en: 'Air emissions (2026)' });

    expect(calls[0]?.url).toBe('/api/v1/templates/7');
    expect(calls[0]?.method).toBe('PUT');

    // ⛔ Саме відсутність `code` у тілі. Код — бізнес-ключ, і поле, яке клієнт
    // надіслав би, а сервер мовчки проігнорував, обіцяло б неіснуючу дію.
    expect(calls[0]?.body).toStrictEqual({ nameL10n: { en: 'Air emissions (2026)' } });
  });

  it('архівування і повернення в обіг — різні адреси, обидві POST', async () => {
    const { calls } = mockServer();

    await archiveTemplate(7);
    await restoreTemplate(7);

    expect(calls[0]?.url).toBe('/api/v1/templates/7/archive');
    expect(calls[0]?.method).toBe('POST');
    expect(calls[1]?.url).toBe('/api/v1/templates/7/restore');
    expect(calls[1]?.method).toBe('POST');
  });
});

describe('dependentWorkCount: що саме показують перед архівуванням', () => {
  it('сумує проєкти й документи і НЕ рахує версій', () => {
    // 4 + 91 = 95; версій три, і вони в суму не входять: версія належить
    // самому шаблону, а питання перед архівуванням — «скільки чужого на нього
    // спирається».
    expect(dependentWorkCount(card())).toBe(95);
  });

  it('шаблон без залежних дає нуль, а не порожнечу', () => {
    expect(
      dependentWorkCount(
        card({ dependents: { versions: 0, publishedVersions: 0, projects: 0, documents: 0 } }),
      ),
    ).toBe(0);
  });
});
