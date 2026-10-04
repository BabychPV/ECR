import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { RegistryImportPanel } from '@/features/registries/RegistryImportPanel';
import { resetMissingReports } from '@/shared/i18n';
import { renderWithQuery } from '@/test/render';

/**
 * L9-44 (AUDIT-2026-10-03, 1E): панель імпорту живе далі, коли на сторінці обирають інший довідник —
 * `RegistriesPage` міняє лише проп `registryCode`. Звіт перевірки, отриманий для одного довідника, не
 * може стати кнопкою «Застосувати» для іншого.
 *
 * Мутаційні докази (перевірено руками 2026-10-04): показувати діалог за `report` без порівняння
 * `target === registryCode` → червоні обидва тести (діалог перевірки UNITS лишається над FUELS, а
 * «Застосувати» шле файл у FUELS).
 *
 * ⚠ Каталог не завантажений — підписи приходять ключами в `⟦…⟧`.
 */

const SlowEnvTimeout = 60_000;

let imports: string[] = [];
let release: (() => void) | null = null;
let holdPreview = false;

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(() => {
  imports = [];
  holdPreview = false;
  release = null;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/entries/import')) {
        imports.push(url);
        const dryRun = !url.includes('dryRun=false');
        if (dryRun && holdPreview) {
          await new Promise<void>((resolve) => {
            release = resolve;
          });
        }
        return json({ added: 1, updated: 0, unchanged: 0, errors: [], applied: !dryRun });
      }

      return json(null);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetMissingReports();
});

function pick(): void {
  const file = new File(['code,en\r\nKG,Kilogram\r\n'], 'units.csv', { type: 'text/csv' });
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (input === null) throw new Error('прихованого input[type=file] немає в DOM');
  Object.defineProperty(input, 'files', { value: [file], configurable: true });
  fireEvent.change(input);
}

function applyButton(): HTMLButtonElement | null {
  return screen.queryByRole('button', { name: /registry\.import\.apply/ }) as HTMLButtonElement | null;
}

describe('RegistryImportPanel: інший довідник під час перевірки (L9-44)', () => {
  it(
    'перевірено для UNITS, обрано FUELS — діалогу перевірки немає, у FUELS нічого не йде',
    async () => {
      const view = renderWithQuery(<RegistryImportPanel registryCode="UNITS" />);

      pick();
      await waitFor(() => expect(applyButton()).not.toBeNull(), { timeout: SlowEnvTimeout });

      view.rerender(<RegistryImportPanel registryCode="FUELS" />);

      const stale = applyButton();
      if (stale !== null) fireEvent.click(stale);

      await waitFor(() => expect(applyButton()).toBeNull(), { timeout: SlowEnvTimeout });
      expect(imports.some((url) => url.includes('/FUELS/'))).toBe(false);
      expect(imports.some((url) => url.includes('dryRun=false'))).toBe(false);
    },
    SlowEnvTimeout,
  );

  it(
    'перевірка UNITS ще йде, обрано FUELS — запізнілий звіт не відкриває діалог над FUELS',
    async () => {
      holdPreview = true;
      const view = renderWithQuery(<RegistryImportPanel registryCode="UNITS" />);

      pick();
      await waitFor(() => expect(release).not.toBeNull(), { timeout: SlowEnvTimeout });

      view.rerender(<RegistryImportPanel registryCode="FUELS" />);
      await act(async () => {
        release?.();
        await new Promise((resolve) => setTimeout(resolve, 50));
      });

      expect(applyButton()).toBeNull();
      expect(imports).toHaveLength(1);
      expect(imports[0]).toContain('/UNITS/');
    },
    SlowEnvTimeout,
  );
});
