import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { SearchLauncher } from '@/features/search/SearchLauncher';

/**
 * Відкрита палітра пошуку даних (BE-19) під `axe` в обох темах.
 *
 * ⚠ Перевіряється `document.body`, а не контейнер: `Modal` живе в порталі.
 * ⚠ Сценарій — відкритий перелік зі збігами: саме там ролі
 * `combobox`/`listbox`/`group`/`option` і `aria-activedescendant` мають
 * зійтися між собою.
 *
 * ⛔ Модуль палітри прогрівається в `beforeAll`: холодний `import()` під
 * навантаженням (1.6 с, заміряно) не вміщався в 1 с `findByRole('combobox')`
 * першого тесту. Розбір — у `DataSearchPalette.test.tsx`.
 */

beforeAll(async () => {
  await import('@/features/search/DataSearchPalette');
});

const original = globalThis.fetch;

afterEach(() => {
  cleanup();
  globalThis.fetch = original;
});

const Hits = [
  { kind: 'document', id: 42, code: 'DOC-42', title: 'Permit 2026' },
  { kind: 'template', id: 7, code: 'T-7', title: 'Emissions' },
  { kind: 'registry', id: 3, code: 'FUEL', title: 'Fuel types' },
];

describe('Палітра пошуку даних — axe без блокуючих порушень', { timeout: 60_000 }, () => {
  it.each(Themes)('тема %s: перелік збігів відкритий', async (scheme) => {
    globalThis.fetch = vi.fn(
      async () =>
        new Response(JSON.stringify(Hits), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    ) as typeof globalThis.fetch;

    render(
      <Shell colorScheme={scheme}>
        <SearchLauncher />
      </Shell>,
    );

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: /search\.open/ }));
    await user.type(await screen.findByRole('combobox'), 'Pe');
    await screen.findByRole('listbox');

    const violations = await findViolations(document.body);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
