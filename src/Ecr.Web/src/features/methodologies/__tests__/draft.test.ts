import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { MethodologyDraftVersionDto } from '@/api/types';
import { createMethodologyVersion } from '@/features/methodologies/api';
import { mayEditContent } from '@/features/methodologies/draft';

/**
 * Редагування методології у вебі (`ФВ-9.15`).
 *
 * ⛔ Вимога говорить саме про **веб**, і покрити її серверними перевірками
 * публікації не можна: до `A7-53` трейт `ФВ-9.15` уже стояв на тестах
 * `MethodologyPublishChecks`, екрана при цьому не існувало, а матриця
 * трасування показувала покриття. Тому тут перевіряється те, що робить
 * клієнт: куди він шле правку, що кладе в тіло і коли взагалі показує дії.
 *
 * ⚠ Перевіряються чисті модулі, а не рендер: Mantine у jsdom малює цю сторінку
 * хвилинами, і тест, який іде дві хвилини, вимикають. Сам рендер лишається
 * під сторожем доступності, який обходить кожен маршрут.
 */

function version(over: Partial<MethodologyDraftVersionDto>): MethodologyDraftVersionDto {
  return {
    id: 1,
    versionNumber: '1.0',
    status: 'Published',
    level: 'Configuration',
    effectiveFrom: '2026-01-01',
    numericMode: 'Legacy',
    calendarMode: 'Actual',
    traceLevel: 'ErrorsOnly',
    isEditable: false,
    createdByUserId: 7,
    ...over,
  };
}

describe('Конфігуратор методологій', () => {
  it('ФВ-9.15: дії правки з’являються лише на чернетці й лише з правом', () => {
    const published = version({ id: 1, isEditable: false });
    const draft = version({ id: 2, versionNumber: '2.0', status: 'Draft', isEditable: true });

    // ⛔ Головне твердження екрана: опублікована версія незмінна (`ФВ-13.2`),
    // бо її числа вже в поданих формах. Кнопка правки на ній вела б у
    // `ECR-CALC-0409` — тобто обіцяла б дію, якої система не робить ніколи.
    expect(mayEditContent(published, true)).toBe(false);
    expect(mayEditContent(draft, true)).toBe(true);

    // Друга умова окремо: без права правка дасть `403`.
    expect(mayEditContent(draft, false)).toBe(false);
    expect(mayEditContent(undefined, true)).toBe(false);
  });
});

describe('Звернення конфігуратора методологій', () => {
  let calls: { url: string; init: RequestInit | undefined }[];

  beforeEach(() => {
    calls = [];

    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
        calls.push({ url: String(input), init });

        return Promise.resolve(
          new Response(JSON.stringify({ id: 1 }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('ФВ-9.1: клон версії називає джерело, а порожня чернетка — ні', async () => {
    await createMethodologyVersion(4, {
      versionNumber: '2.0',
      copyFromVersionId: 9,
      level: 'Configuration',
      engine: 'Script',
    });

    // ⛔ Без `copyFromVersionId` сервер зробив би ПОРОЖНЮ чернетку. Для
    // методології з сорока формулами це не «майже те саме»: опублікувати таку
    // версію означало б зупинити розрахунок, а не змінити одну формулу.
    expect(JSON.parse(String(calls[0]?.init?.body))).toStrictEqual({
      versionNumber: '2.0',
      copyFromVersionId: 9,
      level: 'Configuration',
      engine: 'Script',
    });

    await createMethodologyVersion(4, {
      versionNumber: '1.0',
      copyFromVersionId: null,
      level: 'Configuration',
      engine: 'Script',
    });

    expect(JSON.parse(String(calls[1]?.init?.body)).copyFromVersionId).toBeNull();
    // ⛔ Рушій іде завжди явно (✎ 24.11: лише `Script` — формульний рушій виведено з інтерфейсу).
    expect(JSON.parse(String(calls[1]?.init?.body)).engine).toBe('Script');
  });
});
