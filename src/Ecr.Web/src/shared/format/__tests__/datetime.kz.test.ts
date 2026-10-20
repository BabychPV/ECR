import { afterEach, describe, expect, it, vi } from 'vitest';

/**
 * CL-20 (B-6): у kz дата не має лишатися англійською, коли ICU браузера не знає `kk`.
 *
 * ⚠ Кожен тест бере СВІЖІ модулі (`resetModules` + динамічний імпорт): `formatLocale()` кешує розв'язану локаль на
 * весь час життя модуля, тож без цього другий тест успадкував би відповідь першого.
 * ⚠ Момент складено з ЛОКАЛЬНИХ складників — тест не залежить від часового поясу машини.
 */
const At = new Date(2026, 9, 18, 18, 40);

async function load(withKk: boolean) {
  vi.resetModules();

  if (!withKk) {
    const real = Intl.NumberFormat.supportedLocalesOf.bind(Intl.NumberFormat);
    vi.spyOn(Intl.NumberFormat, 'supportedLocalesOf').mockImplementation((tags, options) =>
      real(tags, options).filter((tag) => !tag.startsWith('kk')),
    );
  }

  const i18n = await import('@/shared/i18n');
  const format = await import('@/shared/format');
  i18n.setLanguage('kz');

  return format;
}

afterEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
});

describe('formatDate/formatDateTime/formatTime у kz без ICU kk', () => {
  it('дата й час — числовий запис kk-KZ, а не «Oct 18, 2026, 6:40 PM»', async () => {
    const { formatDate, formatDateTime, formatTime } = await load(false);

    expect(formatDate(At)).toBe('18.10.2026');
    expect(formatTime(At)).toBe('18:40');
    expect(formatDateTime(At)).toBe('18.10.2026, 18:40');
  });

  it('рядок ISO з сервера обробляється так само', async () => {
    const { formatDateTime } = await load(false);

    expect(formatDateTime(At.toISOString())).toBe('18.10.2026, 18:40');
  });

  it('порожнє значення — порожній рядок', async () => {
    const { formatDateTime } = await load(false);

    expect(formatDateTime(null)).toBe('');
  });

  it('опції місця виклику не перехоплюються (довгий місяць лишається на Intl)', async () => {
    const { formatDate } = await load(false);

    expect(formatDate(At, { year: 'numeric', month: 'long' })).not.toMatch(/^\d{2}\.\d{2}\.\d{4}$/);
  });

  it('мова не kz — запасний запис не діє', async () => {
    vi.resetModules();
    const i18n = await import('@/shared/i18n');
    const { formatDate } = await import('@/shared/format');
    i18n.setLanguage('en');

    expect(formatDate(At).replace(/\s/g, ' ')).toBe('Oct 18, 2026');
  });
});

describe('P3-N9: ICU «знає» kk, але без назв місяців', () => {
  it('«2026 M10 2» не потрапляє на екран - числовий запис', async () => {
    const Real = Intl.DateTimeFormat;
    vi.spyOn(Intl, 'DateTimeFormat').mockImplementation(function (locales?: string | string[], options?: Intl.DateTimeFormatOptions) {
      if (locales === 'kk') {
        return { format: () => (options?.month === 'long' ? 'M10' : '2026 M10 2') } as unknown as Intl.DateTimeFormat;
      }

      return new Real(locales, options);
    } as unknown as typeof Intl.DateTimeFormat);
    const { formatDate } = await load(true);

    expect(formatDate(At)).toBe('18.10.2026');
  });
});

describe('formatDateTime у kz з повною ICU', () => {
  it('використовує Intl kk, а не запасний числовий запис', async () => {
    const { formatDateTime } = await load(true);

    expect(formatDateTime(At)).not.toBe('18.10.2026, 18:40');
    expect(formatDateTime(At)).not.toMatch(/Oct/);
  });
});
