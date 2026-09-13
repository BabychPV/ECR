import { afterEach, describe, expect, it, vi } from 'vitest';
import { act } from '@testing-library/react';
import type { DiagnosticInfo } from '@/api/types';
import { loadCatalog } from '@/shared/i18n';
import { markersFor, positionAt } from '../markers';

/**
 * Позиція зауваження в редакторі (`ФВ-9.15a`).
 *
 * ⛔ Тут сходяться три системи координат: сервер рахує ЗМІЩЕННЯ в символах від
 * нуля, Monaco адресує рядком і колонкою від одиниці. Помилка на одиницю не
 * падає нічим — вона підкреслює сусідній символ, і людина шукає помилку не
 * там, де вона є.
 */

const diagnostic = (position: number, length: number): DiagnosticInfo => ({
  code: 'ECR-TMPL-0422',
  message: 'Неочікувана лексема.',
  position,
  length,
});

describe('зміщення в позицію', () => {
  it('нуль — це перший символ першого рядка', () => {
    expect(positionAt('SUM([Jan])', 0)).toEqual({ lineNumber: 1, column: 1 });
  });

  it('колонка рахується від початку СВОГО рядка', () => {
    // ⚠ Не від початку тексту: інакше кожен наступний рядок зсувався б на
    // довжину всіх попередніх, і підкреслення поїхало б за правий край.
    expect(positionAt('SUM(\n[Jan])', 6)).toEqual({ lineNumber: 2, column: 2 });
  });

  it('зміщення за кінцем тексту не обрізається', () => {
    // ⛔ «Неочікуваний кінець виразу» приходить із позицією, що дорівнює
    // ДОВЖИНІ тексту — синтетична лексема `EndOfInput`. Обрізати її означало б
    // підкреслювати останній набраний символ замість порожнього місця за ним,
    // тобто вказувати на правильний текст як на помилку.
    expect(positionAt('SUM(', 4)).toEqual({ lineNumber: 1, column: 5 });
  });
});

describe('підкреслення', () => {
  it('охоплює рівно стільки символів, скільки сказав сервер', () => {
    const [marker] = markersFor('SUM([Apr])', [diagnostic(5, 3)]);

    expect(marker).toMatchObject({
      startLineNumber: 1,
      startColumn: 6,
      endLineNumber: 1,
      endColumn: 9,
      code: 'ECR-TMPL-0422',
    });
  });

  it('нульова довжина все одно щось підкреслює', () => {
    // ⛔ Усі зауваження рівня зв'язування сервер віддає з `Length = 1`
    // жорстко (`ReferenceResolver`, `TypeChecker`, `UnitChecker`), але нуль
    // прийти може. Підкреслення шириною в нуль пікселів означало б: зауваження
    // є, а на екрані немає нічого.
    const [marker] = markersFor('SUM([Apr])', [diagnostic(5, 0)]);

    expect(marker?.endColumn).toBe(7);
    expect(marker?.startColumn).toBe(6);
  });

  it('кожне зауваження отримує своє підкреслення', () => {
    const markers = markersFor('SUM([Apr], [Mai])', [diagnostic(5, 3), diagnostic(12, 3)]);

    expect(markers).toHaveLength(2);
    expect(markers.map((m) => m.startColumn)).toEqual([6, 13]);
  });
});

/**
 * Локалізація тексту зауваження (`Q-303`).
 *
 * ⛔ `diagnostic.message` сервера — англійський запасний варіант, не готовий
 * текст: `Ecr.Expressions` не має доступу до каталогу рядків, тож
 * локалізує клієнт за `messageKey`/`messageParams` через `t()`. До цієї
 * картки підкреслення несло `diagnostic.message` напряму — те саме
 * СИРЕ УКРАЇНСЬКЕ речення парсера, незалежно від обраної мови інтерфейсу.
 */
describe('локалізація тексту зауваження (Q-303)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  function stubCatalog(strings: Record<string, string>): void {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        Promise.resolve(
          new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings }), {
            status: 200,
            headers: { 'Content-Type': 'application/json', ETag: '"private-en-1"' },
          }),
        ),
      ),
    );
  }

  it('зауваження з messageKey показує розв`язаний каталогом текст, не сирий message сервера', async () => {
    stubCatalog({ 'expr.unexpectedToken': 'Unexpected token "{token}".' });
    await act(async () => {
      await loadCatalog('en', 'private');
    });

    const withKey: DiagnosticInfo = {
      code: 'ECR-TMPL-0422',
      message: 'Неочікувана лексема.',
      messageKey: 'expr.unexpectedToken',
      messageParams: { token: ')' },
      position: 0,
      length: 1,
    };

    const [marker] = markersFor('SUM(', [withKey]);

    expect(marker?.message).toBe('Unexpected token ")".');
    expect(marker?.message).not.toContain('Неочікувана');
  });

  it('зауваження БЕЗ messageKey (звірка типів/одиниць/посилань) лишає message сервера як є', () => {
    // ⚠ `ReferenceResolver`/`TypeChecker`/`UnitChecker` ця картка свідомо не
    // торкнулася (`docs/build/questions/Q-303.md`) — їхні зауваження досі не
    // несуть ключа, і показ їхнього `message` без змін — БАЖАНА поведінка,
    // не пропуск.
    const withoutKey: DiagnosticInfo = {
      code: 'ECR-TMPL-4223',
      message: 'Incompatible units.',
      position: 0,
      length: 1,
    };

    const [marker] = markersFor('SUM([Jan])', [withoutKey]);

    expect(marker?.message).toBe('Incompatible units.');
  });
});
