import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { MethodologyDraftVersionDto } from '@/api/types';
import {
  createMethodologyVersion,
  deleteMethodologyFormula,
  saveMethodologyFormula,
} from '@/features/methodologies/api';
import { defaultVersion, formulaBody, mayEditContent } from '@/features/methodologies/draft';

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

  it('ФВ-9.15: екран відкривається на чернетці, а не на першій версії', () => {
    const published = version({ id: 1, isEditable: false });
    const draft = version({ id: 2, versionNumber: '2.0', status: 'Draft', isEditable: true });

    // ⚠ Перелік іде за номером версії, тож опублікована стоїть першою. Взяти
    // «першу» означало б щоразу відкривати екран редагування на версії, де
    // жодної дії немає й бути не може.
    expect(defaultVersion([published, draft], null)?.id).toBe(2);

    // Явний вибір користувача важливіший за здогад.
    expect(defaultVersion([published, draft], '1')?.id).toBe(1);

    // Чернеток немає — показуємо хоч щось, а не порожнечу.
    expect(defaultVersion([published], null)?.id).toBe(1);
    expect(defaultVersion([], null)).toBeUndefined();
  });

  it('ФВ-16.6: текстовий результат іде без одиниці', () => {
    const draft = {
      versionId: 5,
      code: 'verdict',
      expression: "'Сверхнорматив'",
      resultType: 'Text',
      outputUnitId: 3,
      isNew: false,
    } as const;

    // ⛔ Вимір — властивість числа. Сервер таку пару відхиляє
    // (`ECR-CALC-0422`), і лишити одиницю від попереднього вибору означало б
    // показати відмову там, де людина все зробила правильно: вона перемкнула
    // тип, а поле одиниці при цьому зникло з очей.
    expect(formulaBody(draft).outputUnitId).toBeNull();

    // Числовий результат одиницю зберігає — на ній тримається перевірка
    // розмірностей при публікації.
    expect(formulaBody({ ...draft, resultType: 'Number' }).outputUnitId).toBe(3);
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

  it('ФВ-9.15: формула адресується кодом, а запис іде PUT', async () => {
    await saveMethodologyFormula(4, {
      versionId: 9,
      code: 'gsec',
      expression: '@Flow * CST.k1',
      resultType: 'Number',
      outputUnitId: 3,
      isNew: false,
    });

    const call = calls[0];

    // ⚠ Код у шляху — це те, чим на формулу посилаються вирази (`!Name`).
    // Саме тому створення й зміна — одна дія: повторний `PUT` із тим самим
    // тілом дає той самий стан.
    expect(call?.url).toBe('/api/v1/methodologies/4/versions/9/formulas/gsec');
    expect(call?.init?.method).toBe('PUT');

    expect(JSON.parse(String(call?.init?.body))).toStrictEqual({
      expression: '@Flow * CST.k1',
      resultType: 'Number',
      outputUnitId: 3,
    });
  });

  it('ФВ-9.15: видалення формули йде DELETE на ту саму адресу', async () => {
    await deleteMethodologyFormula(4, 9, 'gsec');

    expect(calls[0]?.url).toBe('/api/v1/methodologies/4/versions/9/formulas/gsec');
    expect(calls[0]?.init?.method).toBe('DELETE');
  });

  it('ФВ-9.1: клон версії називає джерело, а порожня чернетка — ні', async () => {
    await createMethodologyVersion(4, {
      versionNumber: '2.0',
      copyFromVersionId: 9,
      level: 'Configuration',
    });

    // ⛔ Без `copyFromVersionId` сервер зробив би ПОРОЖНЮ чернетку. Для
    // методології з сорока формулами це не «майже те саме»: опублікувати таку
    // версію означало б зупинити розрахунок, а не змінити одну формулу.
    expect(JSON.parse(String(calls[0]?.init?.body))).toStrictEqual({
      versionNumber: '2.0',
      copyFromVersionId: 9,
      level: 'Configuration',
    });

    await createMethodologyVersion(4, {
      versionNumber: '1.0',
      copyFromVersionId: null,
      level: 'Configuration',
    });

    expect(JSON.parse(String(calls[1]?.init?.body)).copyFromVersionId).toBeNull();
  });
});
