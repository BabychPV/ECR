import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { loadCatalog } from '@/shared/i18n';
import { permissionLabel, permissionLabelKey } from '@/features/security/permissionLabel';

/**
 * `U-11`: 41 колонка матриці прав була підписана сирим кодом сервера.
 *
 * ⛔ Головне твердження цього файлу — НЕ «назва з'явилася». Воно про запасний
 * варіант: каталог прав приходить із сервера, і право, під яке рядка в
 * `09-seed.sql` ще немає, мусить показати САМ КОД, а не `⟦permission.…⟧`.
 * Інакше поява 42-го права псувала б екран рівно в той момент, коли на ньому
 * з'являється щось нове, — тобто «виправлення» було б гіршим за дефект.
 */

const Strings: Record<string, string> = {
  'permission.Calculation.EditConstant': 'Edit methodology constants',
  'permission.System.ManageLocalization': 'Manage interface strings and languages',
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

beforeEach(async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      return json(null);
    }),
  );

  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Назва права проти коду права (U-11)', () => {
  it('відомий код підписується рядком каталогу, а не собою', () => {
    expect(permissionLabel('Calculation.EditConstant')).toBe('Edit methodology constants');
    expect(permissionLabel('System.ManageLocalization')).toBe(
      'Manage interface strings and languages',
    );
  });

  it('НЕВІДОМИЙ код показується сам, а не позначеним ключем', () => {
    const label = permissionLabel('Future.NotSeededYet');

    // ⛔ Саме це й ламається, якщо `permissionLabel` перевести на голий `t()`:
    // на екрані з'явилося б `⟦permission.Future.NotSeededYet⟧`.
    expect(label).toBe('Future.NotSeededYet');
    expect(label).not.toContain('⟦');
    expect(label).not.toContain('permission.');
  });

  it('ключ несе код так, як його називає сервер — без приведення регістру', () => {
    // ⚠ `A7-02`: будь-яке перетворення коду на шляху до ключа дало б другу
    // відповідність, яку нема кому перевірити.
    expect(permissionLabelKey('Calculation.EditConstant')).toBe(
      'permission.Calculation.EditConstant',
    );
  });
});

/**
 * Друга половина доказу: запасний варіант не має бути ПОВСЯКДЕННИМ.
 *
 * ⛔ Тест вище робить промах каталогу нешкідливим, і саме тому потрібен цей:
 * без нього забутий у сіді рядок мовчки повертав би екран у стан `U-11` —
 * колонка знову підписана кодом, і жодна перевірка не червона.
 */
describe('Сід покриває ВЕСЬ каталог прав (U-11)', () => {
  const seed = readFileSync(
    path.resolve(process.cwd(), '../Ecr.Infrastructure/Persistence/Sql/09-seed.sql'),
    'utf8',
  );

  /** Блок `MERGE sec.Permission` — саме він є каталогом прав. */
  const start = seed.indexOf('MERGE sec.Permission');
  const end = seed.indexOf(') AS s (Code, [Group], IsDangerous)', start);
  const section = seed.slice(start, end);

  const codes = new Set(
    [...section.matchAll(/\(N'([A-Z][A-Za-z]+\.[A-Za-z]+)',\s+N'/g)].map((m) => m[1] ?? ''),
  );

  const named = new Set([...seed.matchAll(/N'permission\.([A-Za-z.]+)'/g)].map((m) => m[1] ?? ''));

  it('блок каталогу прав у сіді знайдено — інакше решта перевірок нічого не варта', () => {
    expect(start).toBeGreaterThan(-1);
    expect(end).toBeGreaterThan(start);
  });

  it('у каталозі 41 право — рівно стільки колонок і має матриця', () => {
    expect(codes.size).toBe(41);
  });

  it('кожне право має назву в каталозі рядків', () => {
    const withoutName = [...codes].filter((code) => !named.has(code)).sort();

    expect(withoutName).toEqual([]);
  });

  it('жодна назва не заведена під право, якого немає', () => {
    const orphans = [...named].filter((code) => !codes.has(code)).sort();

    expect(orphans).toEqual([]);
  });
});
