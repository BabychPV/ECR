import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { VersionMigrationDialog } from '@/features/documents/VersionMigrationDialog';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * Діалог «Перенести на нову версію шаблону»: закривна кнопка (X) модалки має доступне ім'я
 * (axe `button-name`, critical) в обох темах. Ім'я йде з каталогу (`common.close`).
 */
function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function stubServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      json({
        projectId: 7,
        currentVersionId: 1,
        currentVersion: '1.0',
        targets: [{ id: 2, version: '2.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: 1, publishedAt: null }],
      }),
    ),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('VersionMigrationDialog — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: закривна кнопка має ім'я, порушень немає`, async () => {
      stubServer();
      render(
        <Shell colorScheme={colorScheme}>
          <VersionMigrationDialog documentId={42} onClose={() => undefined} jobId={null} onJobChange={() => undefined} />
        </Shell>,
      );

      const dialog = await screen.findByRole('dialog');
      expect(screen.getByRole('button', { name: /^(Close|⟦common\.close⟧)$/ })).toBeTruthy();

      const violations = await findViolations(dialog);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
