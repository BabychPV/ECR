import { describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { DateField } from '../DateField';

/**
 * PA-U-10: поки вантажиться лінивий чанк дат, скелет поля мав `aria-label` на голому `div`
 * (axe serious `aria-prohibited-attr`). Перший рендер файлу гарантовано ловить саме скелет.
 */
describe('DateField — скелет завантаження', () => {
  it('підписаний скелет має роль, а не голий aria-label на div', () => {
    const { container } = render(
      <MantineProvider>
        <DateField label="Issued" value="2026-01-01" onChange={() => undefined} />
      </MantineProvider>,
    );
    const skeleton = container.querySelector('.mantine-Skeleton-root');
    expect(skeleton, 'скелет має бути в першому рендері').not.toBeNull();
    expect(skeleton?.getAttribute('role')).toBe('status');
    expect(screen.getByRole('status', { name: 'Issued' })).toBe(skeleton);
    cleanup();
  });
});
