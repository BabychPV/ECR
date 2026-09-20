import { describe, it, expect, vi, afterEach } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { LocalizedInput } from '@/shared/ui/LocalizedInput';
import { renderWithMantine } from '@/test/render';

/**
 * Невдалий запит реєстру мов робив форму НЕЗАПОВНЮВАНОЮ і мовчав про це.
 *
 * ⛔ Тут стояло `(languages.data ?? []).map(…)` — той самий взірець, проти
 * якого існує `AsyncBoundary`: «невдалий запит перетворюється на „даних
 * немає“». Але наслідок гірший за порожній перелік. При відмові
 * `GET /api/v1/languages` компонент малював НУЛЬ полів, а форма поруч лишалася
 * з заблокованою кнопкою і підказкою «ще потрібно: Назва»
 * (`CreateProjectModal.tsx`): застосунок вимагав заповнити поле, якого на
 * екрані не існує.
 *
 * ⚠ Компонент спільний — він стоїть у кожній формі створення (проєкт, шаблон,
 * довідник, одиниця), тож одна відмова ламала їх усі однаково тихо.
 */

function respond(reply: () => Response): void {
  vi.stubGlobal('fetch', vi.fn(async () => reply()));
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  renderWithMantine(
    <QueryClientProvider client={client}>
      <LocalizedInput label="Назва" value={{}} onChange={() => {}} />
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('LocalizedInput: відмова реєстру мов не виглядає як «полів немає»', () => {
  it('сервер відмовив — на екрані ВІДМОВА, а не порожнеча', async () => {
    respond(() =>
      json(
        {
          type: 'about:blank',
          title: 'Internal Server Error',
          status: 500,
          detail: 'реєстр мов прочитати не вдалося',
          errorCode: 'ECR-SYS-0500',
          correlationId: 'cid-lang-1',
          messageKey: 'err.ECR-SYS-0500.unexpected',
        },
        500,
      ),
    );

    show();

    /*
     * ⛔ Головне твердження, і саме воно було хибним: до правки тут не було
     * НІЧОГО — ані полів, ані натяку на збій.
     */
    const alert = await screen.findByRole('alert');

    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');

    // І дзеркало: полів справді немає — але тепер людині сказано чому.
    expect(screen.queryAllByRole('textbox')).toHaveLength(0);
  });

  it('реєстр мов ПОРОЖНІЙ — теж сказано, а не мовчки нуль полів', async () => {
    /*
     * ⚠ Порожній реєстр — не те саме, що відмова: причина лежить в
     * адмініструванні (`ФВ-2.2`), а не в мережі. Але мовчати про нього так
     * само не можна — форма без жодного поля назви незаповнювана.
     */
    respond(() => json([]));
    show();

    await waitFor(() => {
      expect(document.querySelector('[data-localized-input="empty"]')).not.toBeNull();
    });

    expect(screen.queryAllByRole('textbox')).toHaveLength(0);
  });

  it('мови приїхали — по полю на кожну, як і було', async () => {
    /*
     * ⛔ Дзеркало, без якого решта нічого не варта: правка не мала зламати
     * головний шлях. Два поля, підписані назвою мови.
     */
    respond(() =>
      json([
        { code: 'en', nameNative: 'English', isDefault: true },
        { code: 'kz', nameNative: 'Қазақша', isDefault: false },
      ]),
    );

    show();

    await waitFor(() => {
      expect(screen.queryAllByRole('textbox')).toHaveLength(2);
    });

    expect(screen.getByLabelText('Назва · English')).toBeDefined();
    expect(screen.getByLabelText('Назва · Қазақша')).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
