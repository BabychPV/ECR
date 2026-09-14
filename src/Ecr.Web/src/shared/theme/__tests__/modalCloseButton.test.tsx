import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider, Modal } from '@mantine/core';
import { theme } from '../theme';

/**
 * Аудит-пас 5: кнопка закриття модалки не мала жодного `aria-label` —
 * скрінрідер оголошував лише «кнопка», без натяку на дію.
 *
 * ⛔ Рядок — статичний, НЕ через `t()`: `theme` створюється рівно один раз
 * при завантаженні модуля (ФВ-14.11), задовго до того, як каталог рядків
 * довантажиться з сервера. Підстановка через `t()` тут застигла б назавжди
 * як `⟦common.close⟧` — той самий клас пастки, що документує сам `theme.ts`.
 */
describe('Modal: кнопка закриття має aria-label', () => {
  it('дефолт теми задає aria-label, а не лишає скрінрідер із голою "кнопкою"', () => {
    render(
      <MantineProvider theme={theme}>
        <Modal opened onClose={() => {}} title="Test">
          content
        </Modal>
      </MantineProvider>,
    );

    expect(screen.getByRole('button', { name: 'Close' })).toBeDefined();
  });
});
