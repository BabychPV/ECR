import { afterEach, describe as suite, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe as report, findViolations } from '@/test/a11y';
import { RouteShell, Themes, createScanClient, settleQueries } from '@/test/__tests__/a11yFixtures';
import { routes } from '@/app/routes';
import { CompositionEditorPage } from '../CompositionEditorPage';
import { mockServer } from './compositionServer';

/**
 * WCAG 2.1 AA редактора master-detail (`ФВ-14.16`, RT-32 «a11y»): обидві панелі з даними — кейси
 * й склад обраного кейсу з полями вводу, Σ і кнопками.
 *
 * ⚠ Сканується стан ПІСЛЯ вибору кейсу: порожня права панель («оберіть рядок») нічого не порушує, і
 * axe був би зелений на екрані без того, заради чого сторінка існує.
 */

afterEach(() => {
  vi.unstubAllGlobals();
});

suite.each(Themes)('Доступність редактора master-detail (%s)', (colorScheme) => {
  it('ФВ-8.16: кейси й склад кейсу без порушень critical і serious', async () => {
    mockServer(['Registry.View', 'Registry.EditData']);
    const client = createScanClient();
    const { container } = render(
      <RouteShell
        colorScheme={colorScheme}
        client={client}
        path={routes.adminRegistryComposition.path}
        entry="/admin/registries/STREAM_CASE/composition"
      >
        <CompositionEditorPage />
      </RouteShell>,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'E77' }, { timeout: 10_000 }));
    await waitFor(() => {
      if (container.querySelector('[data-rc816-table="GAS_COMPOSITION"]') === null) throw new Error('склад ще їде');
    });
    await settleQueries(client);

    const violations = await findViolations(container);

    expect(violations, report(violations)).toHaveLength(0);
  });
});
