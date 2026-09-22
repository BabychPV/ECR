import { afterEach, describe, expect, it, vi } from 'vitest';
import { EcrApiError } from '@/api/client';
import {
  acceptSourceUnitChange,
  collectedPointsBlockingDelete,
  deleteEntityFieldMap,
  MappingHasCollectedDataKey,
  MappingStateConflictCode,
  pauseEntityFieldMap,
  resumeEntityFieldMap,
} from '@/features/mapping/api';

/**
 * Дії над мапінгом (`BE-27`): пауза / відновлення, приймання зміни одиниці,
 * видалення.
 *
 * ⛔ Перевіряються АДРЕСА, МЕТОД і ТІЛО кожного виклику. Хук, що пішов на
 * правильну адресу неправильним методом, на сервері отримує `405` — і жоден
 * серверний тест цього не бачить, бо він ходить у свій маршрут сам.
 */

/** Записані виклики `fetch`, у порядку надсилання. */
const sent: { url: string; method: string; body: unknown }[] = [];

function mockServer(status = 200): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      sent.push({
        url: String(url),
        method: String(init?.method ?? 'GET'),
        body: init?.body === undefined ? null : JSON.parse(String(init.body)),
      });

      if (status === 204) {
        return new Response(null, { status: 204 });
      }

      return new Response(JSON.stringify({ id: 7, isActive: false }), {
        status,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Дії над мапінгом', () => {
  it('пауза й відновлення — різні адреси одного мапінга, обидві POST', async () => {
    mockServer();

    await pauseEntityFieldMap(7);
    await resumeEntityFieldMap(7);

    expect(sent.map((c) => `${c.method} ${c.url}`)).toEqual([
      'POST /api/v1/entity-field-maps/7/pause',
      'POST /api/v1/entity-field-maps/7/resume',
    ]);
  });

  it('приймання зміни одиниці йде з ПОРОЖНІМ тілом — сервер бере одиницю з позначки', async () => {
    // ⛔ Тіло `{}`, БЕЗ `sourceUnitId`: контракт (`ФВ-16.9`) віддає рішення
    // «яку одиницю прийняти» серверу, а не клієнту — сервер знає, яку
    // одиницю щойно побачив збір (`pendingSourceUnitChange.actualUnitId`).
    mockServer();

    await acceptSourceUnitChange(7);

    expect(sent).toEqual([
      {
        url: '/api/v1/entity-field-maps/7/accept-unit-change',
        method: 'POST',
        body: {},
      },
    ]);
  });

  it('видалення йде методом DELETE і без тіла', async () => {
    mockServer(204);

    await deleteEntityFieldMap(7);

    expect(sent).toEqual([{ url: '/api/v1/entity-field-maps/7', method: 'DELETE', body: null }]);
  });
});

describe('Відмова «за мапінгом уже зібрано дані»', () => {
  function conflict(messageKey: string, collectedPoints?: number): EcrApiError {
    return new EcrApiError({
      title: 'conflict',
      status: 409,
      errorCode: MappingStateConflictCode,
      correlationId: 'cid',
      extensions2: { messageKey, ...(collectedPoints === undefined ? {} : { collectedPoints }) },
    });
  }

  it('віддає число точок — діалог має що показати замість «повторити»', () => {
    expect(collectedPointsBlockingDelete(conflict(MappingHasCollectedDataKey, 4812))).toBe(4812);
  });

  it('інший стан того самого коду не читається як «є дані»', () => {
    // ⛔ `ECR-INT-0409` покриває чотири стани. Якби розрізняв їх сам код,
    // клієнт пропонував би паузу у відповідь на «уже призупинено» — тобто
    // радив зробити те, що вже зроблено.
    expect(collectedPointsBlockingDelete(conflict('err.ECR-INT-0409.mappingAlreadyPaused', 4812)))
      .toBeNull();
  });

  it('відмова без числа не видає себе за відповідь', () => {
    // ⚠ `null`, а не `0`. Нуль означав би «даних немає», тобто пояснював би
    // відмову, якої за такої умови не буває.
    expect(collectedPointsBlockingDelete(conflict(MappingHasCollectedDataKey))).toBeNull();
    expect(collectedPointsBlockingDelete(new Error('boom'))).toBeNull();
  });
});
