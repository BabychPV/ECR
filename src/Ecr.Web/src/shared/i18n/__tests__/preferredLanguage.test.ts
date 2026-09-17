import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DefaultLanguage, preferredLanguage } from '@/shared/i18n';

/**
 * Мова браузера → внутрішній код мови продукту.
 *
 * ⛔ Дефект, який це закриває — ДЗЕРКАЛЬНИЙ до вже виправленого в `<html lang>`.
 * Там внутрішній код `kz` віддавався назовні як тег BCP-47 `kk`. Тут той самий
 * розрив діє у ЗВОРОТНОМУ напрямку і був пропущений: браузер оголошує
 * казахську як `kk-KZ` (`kk` — ISO 639-1, мова; `KZ` — ISO 3166, країна), а
 * `preferredLanguage()` брала перші дві літери й отримувала `kk` — коду, якого
 * в реєстрі `sys_ecr.Language` немає, бо там історично записано `kz`.
 *
 * Наслідок видимий: користувач із казахським браузером, який ще жодного разу
 * не перемикав мову вручну, отримував каталог `kk` — тобто НЕ існуючу мову, —
 * а не казахську. `loadCatalog('kk', …)` іде по неіснуючий каталог, і
 * сторінка входу показує англійську; вибір мови «за перевагами браузера»
 * мовчки не працює саме для тієї мови, заради якої розбіжність кодів і
 * виникла.
 *
 * ⚠ Твердження про `ru`/`en` тут не для повноти: вони фіксують, що виправлення
 * — це ТОЧКОВИЙ виняток, а не таблиця дозволених мов. Список мов у бандлі
 * заборонено вимогою «додавання мови — запис у реєстр, не збірка клієнта».
 */

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

beforeEach(() => {
  localStorage.clear();
});

function withNavigatorLanguage(value: string): void {
  vi.stubGlobal('navigator', { language: value });
}

describe('preferredLanguage(): мова браузера приводиться до коду реєстру', () => {
  it("тег BCP-47 казахської 'kk-KZ' дає внутрішній код 'kz'", () => {
    withNavigatorLanguage('kk-KZ');

    expect(preferredLanguage()).toBe('kz');
  });

  it("голий 'kk' (без регіону) теж дає 'kz'", () => {
    withNavigatorLanguage('kk');

    expect(preferredLanguage()).toBe('kz');
  });

  it("регістр не має значення: 'KK-kz' теж дає 'kz'", () => {
    withNavigatorLanguage('KK-kz');

    expect(preferredLanguage()).toBe('kz');
  });

  it("мова без розбіжності ('ru-RU') проходить як є", () => {
    withNavigatorLanguage('ru-RU');

    expect(preferredLanguage()).toBe('ru');
  });

  it('невідома мова браузера проходить як є, а не мапиться в щось', () => {
    // ⚠ Не список дозволених мов: четверта мова з реєстру мусить працювати
    // без перезбирання клієнта.
    withNavigatorLanguage('de-DE');

    expect(preferredLanguage()).toBe('de');
  });

  it("збережений вручну вибір 'kz' має перевагу над мовою браузера", () => {
    localStorage.setItem('uiLanguage', 'kz');
    withNavigatorLanguage('en-US');

    expect(preferredLanguage()).toBe('kz');
  });

  it('порожня мова браузера дає мову за замовчуванням', () => {
    withNavigatorLanguage('');

    expect(preferredLanguage()).toBe(DefaultLanguage);
  });
});
