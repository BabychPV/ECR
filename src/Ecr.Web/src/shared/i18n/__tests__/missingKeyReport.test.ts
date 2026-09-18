import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { loadCatalog, resetMissingReports, t } from '@/shared/i18n';

/**
 * UI-прохід, F11: попередження «Немає рядка інтерфейсу» було НЕПРАВДИВИМ.
 *
 * ⛔ Що відбувалося в застосунку. `AppLayout` не малює текст, доки приватний
 * каталог не доїде (`catalogReady`) — але `useRouteTransitionFocus`, який
 * рахує `document.title` з ланцюжка крихт, стоїть ВИЩЕ цієї перевірки й кличе
 * `t(route.handle.labelKey)` уже в першому рендері. Тобто на кожному холодному
 * відкритті будь-якого маршруту в консоль летіла скарга на `nav.*`, хоча всі
 * 18 ключів у каталозі є і підписи на екрані правильні.
 *
 * ⚠ Ціна шуму не нульова: скарга існує, щоб ловити СПРАВЖНІ прогалини, а
 * поруч із двадцятьма неправдивими справжню не видно. Тому попередження не
 * заглушене, а відкладене до моменту, коли каталог відстоявся, і ключ тоді
 * перевіряється ще раз.
 *
 * ⚠ Обидва тести користуються фальшивими таймерами: поріг відкладення
 * (`CatalogSettleMs`) — внутрішня деталь модуля, і чекати його по-справжньому
 * означало б додати секунду до прогону заради нічого.
 */
function catalogResponse(languageCode: string, strings: Record<string, string>): Response {
  return new Response(JSON.stringify({ languageCode, revision: 1, strings }), {
    status: 200,
    headers: { 'Content-Type': 'application/json', ETag: `"private-${languageCode}-1"` },
  });
}

function serve(strings: Record<string, string>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return Promise.resolve(catalogResponse('en', strings));

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

let complaints: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  vi.useFakeTimers();
  resetMissingReports();
  complaints = vi.spyOn(console, 'error').mockImplementation(() => {});
});

afterEach(() => {
  complaints.mockRestore();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('Скарга на відсутній рядок інтерфейсу', () => {
  it('F11: ключ, якого ще немає лише тому, що каталог їде, скарги НЕ дає', async () => {
    serve({ 'nav.templates': 'Templates' });

    // Перший рендер застосунку: `t()` кличеться ДО того, як ефект замовив
    // каталог — саме той кадр, на який і сипались попередження.
    expect(t('nav.templates')).toBe('⟦nav.templates⟧');

    // ⛔ Головне твердження №1, і воно ж мутаційний доказ: поверніть у
    // `report()` прямий `console.error` — цей рядок стане червоним негайно.
    expect(complaints).not.toHaveBeenCalled();

    const load = loadCatalog('en', 'private');
    await vi.advanceTimersByTimeAsync(10_000);
    await load;

    // Каталог доїхав, підпис правильний — скаржитись не було на що.
    expect(t('nav.templates')).toBe('Templates');

    // ⛔ Головне твердження №2: відкладена скарга ПЕРЕВІРЯЄ ключ ще раз і
    // мовчить. Приберіть повторну перевірку у `flushMissingReports` — і
    // попередження надрукується із запізненням, тобто лишиться неправдивим.
    expect(complaints).not.toHaveBeenCalled();
  });

  it('F11: ключ, якого в доїханому каталозі справді немає, скаргу дає', async () => {
    serve({ 'nav.templates': 'Templates' });

    const load = loadCatalog('en', 'private');
    await vi.advanceTimersByTimeAsync(10_000);
    await load;

    expect(t('section.simply.absent')).toBe('⟦section.simply.absent⟧');

    // Рішення ще не ухвалене — саме тому попереднього тесту й не видно.
    expect(complaints).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(10_000);

    // ⛔ Головне твердження, і воно ж мутаційний доказ протилежного боку:
    // заглушіть попередження зовсім (`return` на початку `report()`) — цей
    // рядок стане червоним. Тобто сторож ловить і шум, і його втрату.
    expect(complaints).toHaveBeenCalledTimes(1);
    expect(String(complaints.mock.calls[0]?.[0])).toContain('section.simply.absent');
  });

  it('F11: про той самий відсутній ключ скаржаться рівно один раз', async () => {
    serve({ 'nav.templates': 'Templates' });

    const load = loadCatalog('en', 'private');
    await vi.advanceTimersByTimeAsync(10_000);
    await load;

    t('section.repeated.absent');
    t('section.repeated.absent');
    await vi.advanceTimersByTimeAsync(10_000);

    t('section.repeated.absent');
    await vi.advanceTimersByTimeAsync(10_000);

    expect(complaints).toHaveBeenCalledTimes(1);
  });
});
