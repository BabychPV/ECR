import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { testTheme } from '@/test/render';
import { DocumentLockBanner } from '../DocumentLockBanner';

/**
 * ✎ UAT 08.11 (F13-p209 P2-1): банер закритого місяця казав «Period 202601 is closed…» — сирий ключ періоду.
 * ⛔ Червоне без фіксу: у параметрі тексту стояло `period=202601`.
 */
describe('DocumentLockBanner — період назвою', () => {
  for (const lock of ['periodClosed', 'periodNotOpen'] as const) {
    it(`Water lock: ${lock} — «January 2026», не 202601`, () => {
      render(
        <MantineProvider theme={testTheme}>
          <DocumentLockBanner lock={lock} periodKey={202601} />
        </MantineProvider>,
      );

      const text = document.querySelector(`[data-document-lock="${lock}"]`)?.textContent ?? '';
      expect(text).toContain(`document.lock.${lock}`);
      expect(text).not.toContain('202601');
      expect(text).toMatch(/period=[^)]*2026/);
    });
  }
});
