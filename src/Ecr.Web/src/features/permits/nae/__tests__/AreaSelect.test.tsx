import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch } from '@/api/client';
import { renderWithQuery } from '@/test/render';
import { AreaSelect } from '../AreaSelect';

vi.mock('@/api/client', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/api/client')>()), apiFetch: vi.fn() }));

/** Район NAE - вибір зі списку `AIR_AREA` (людина 04.11), порожній варіант - «без району». */
describe('AreaSelect', () => {
  beforeEach(() => {
    vi.mocked(apiFetch).mockReset();
    vi.mocked(apiFetch).mockResolvedValue({
      items: [{ id: 11, code: 'OFFSHORE', display: 'Offshore' }, { id: 12, code: 'ONSHORE', display: 'Onshore' }],
      nextCursor: null,
      totalCount: 2,
    });
  });
  afterEach(cleanup);

  it('читає AIR_AREA, дає порожній варіант і віддає код вибраного запису', async () => {
    const onChange = vi.fn();
    renderWithQuery(<AreaSelect label="Area" value="" onChange={onChange} />);

    const select = (await screen.findByLabelText('Area')) as HTMLSelectElement;
    await waitFor(() => expect(select.options.length).toBe(3));
    expect(vi.mocked(apiFetch).mock.calls[0]?.[0]).toContain('/registries/AIR_AREA/rows');
    expect(select.options[0]?.value).toBe('');
    await userEvent.setup().selectOptions(select, 'ONSHORE');
    expect(onChange).toHaveBeenCalledWith('ONSHORE');
  });

  it('значення, якого немає в довіднику (старі дані), лишається видимим, а не підміняється', async () => {
    renderWithQuery(<AreaSelect label="Area" value="_____" onChange={vi.fn()} />);

    const select = (await screen.findByLabelText('Area')) as HTMLSelectElement;
    await waitFor(() => expect(select.options.length).toBe(4));
    expect(select.value).toBe('_____');
  });
});