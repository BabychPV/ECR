import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiFetch, setRequestLanguageTag } from '@/api/client';

/**
 * Мова інтерфейсу доходить до сервера заголовком `Accept-Language`.
 *
 * ⛔ Дефект, який це закриває: вибір мови в застосунку на сервер НЕ ПОТРАПЛЯВ
 * узагалі. Мова живе лише в `localStorage` браузера, у профілі користувача її
 * немає, а claim `ecr:lang`, який читає `ICurrentUser.Language`, у
 * репозиторії НІХТО не записує — це єдина згадка на все дерево, читач без
 * письменника. Отже серверні тексти (заголовок і подробиця відмови)
 * визначалися системною мовою машини: оператор перемикав застосунок на
 * англійську, а помилки приходили російською, і перемикач у шапці на це не
 * впливав ніяк.
 *
 * ⚠ Перевіряється саме ЗАГОЛОВОК ЗАПИТУ, а не наслідок на екрані: наслідок
 * залежить від каталогу сервера, тобто тест про нього був би про чужу
 * поведінку. Тут же твердження вузьке й повне — те, що клієнт зобов'язаний
 * надіслати.
 */

const original = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = original;
  setRequestLanguageTag(null);
});

/** Перехоплює запит і повертає надіслані заголовки. */
function capture(): { headers: () => Headers } {
  let sent = new Headers();

  globalThis.fetch = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
    sent = new Headers(init?.headers);

    return new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } });
  }) as typeof globalThis.fetch;

  return { headers: () => sent };
}

describe('мова запиту', () => {
  it('оголошена мова йде на сервер у Accept-Language', async () => {
    const captured = capture();
    setRequestLanguageTag('kk');

    await apiFetch('/api/v1/projects');

    expect(captured.headers().get('Accept-Language')).toBe('kk');
  });

  it('без оголошеної мови заголовок не додається — браузер вирішує сам', async () => {
    const captured = capture();

    await apiFetch('/api/v1/projects');

    // ⚠ Саме `null`, а не порожній рядок: клієнт, який нічого не знає про
    // мову, мусить лишити заголовок браузера недоторканим, а не стерти його.
    expect(captured.headers().get('Accept-Language')).toBeNull();
  });

  it('власний заголовок викликача має перевагу', async () => {
    const captured = capture();
    setRequestLanguageTag('kk');

    // ⛔ Це не дрібниця: каталог мови завантажується явним запитом ЗА
    // КОНКРЕТНОЮ мовою, і перебити його активною означало б попросити каталог
    // не тієї мови, яку щойно обрали.
    await apiFetch('/api/v1/ui-strings/ru', { headers: { 'Accept-Language': 'ru' } });

    expect(captured.headers().get('Accept-Language')).toBe('ru');
  });

  it('вибір мови в застосунку доходить до сервера — і саме ТЕГОМ, а не кодом реєстру', async () => {
    // ⛔ Це головне твердження файлу. Попередні три перевіряють транспорт сам
    // по собі; воно нічого не варте, якщо шар i18n мову туди не штовхає, —
    // а доти саме цього й не було: `setLanguage` міняв каталог і `<html lang>`
    // і на цьому спинявся.
    const { setLanguage } = await import('@/shared/i18n');

    const captured = capture();
    setLanguage('kz');

    await apiFetch('/api/v1/projects');

    // ⚠ `kk`, а не `kz`: у заголовок BCP-47 кладеться ТЕГ. Внутрішній код
    // реєстру там був би власним словником під виглядом стандарту — і сервер
    // (`LanguageCodes.FromTag`) переводить назад саме тег.
    expect(captured.headers().get('Accept-Language')).toBe('kk');
  });
});
