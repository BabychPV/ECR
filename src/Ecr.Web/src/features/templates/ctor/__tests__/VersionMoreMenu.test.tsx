import { describe, expect, it, vi } from 'vitest';
import { fireEvent, screen } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { VersionMoreMenu } from '../VersionMoreMenu';

/**
 * b4b, KIT §1.2: шапка конструктора — одна головна дія, ≤ 2 другорядні, решта в «More».
 * Меню без жодної дії не малюється; кожен наявний пункт викликає свою дію.
 */
describe('VersionMoreMenu', () => {
  it('без жодної дії — кнопки «More» немає зовсім', () => {
    renderWithMantine(<VersionMoreMenu items={[null, null]} />);

    expect(screen.queryByTestId('version-more')).toBeNull();
  });

  it('показує лише наявні дії й викликає ту, яку обрали', async () => {
    const clone = vi.fn();
    const matrix = vi.fn();
    renderWithMantine(
      <VersionMoreMenu
        items={[
          { key: 'matrix', label: 'Access matrix', onClick: matrix },
          null,
          { key: 'clone', label: 'Clone version', onClick: clone },
        ]}
      />,
    );

    fireEvent.click(screen.getByTestId('version-more'));
    const items = await screen.findAllByRole('menuitem');
    expect(items.map((item) => item.textContent)).toEqual(['Access matrix', 'Clone version']);

    fireEvent.click(screen.getByRole('menuitem', { name: 'Clone version' }));
    expect(clone).toHaveBeenCalledTimes(1);
    expect(matrix).not.toHaveBeenCalled();
  });
});
