import { beforeEach, describe, expect, it, vi } from 'vitest';
import { getConditionalFormats, saveConditionalFormats } from '../conditionalFormatApi';

const apiFetch = vi.hoisted(() => vi.fn());
vi.mock('@/api/client', () => ({ apiFetch }));

describe('conditionalFormatApi (ФВ-2.6/2.7)', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    apiFetch.mockResolvedValue([]);
  });

  it('читає правила версії з GET …/conditional-formats', async () => {
    await getConditionalFormats(42);

    expect(apiFetch).toHaveBeenCalledWith('/api/v1/template-versions/42/conditional-formats');
  });

  it('замінює набір PUT-ом із тілом { rules } у порядку списку', async () => {
    const rules = [
      { columnCode: 'VOL', operator: 'gt', value: '10', valueTo: null, backgroundHex: '#ff0000', foregroundHex: null, isBold: true },
      { columnCode: 'VOL', operator: 'empty', value: null, valueTo: null, backgroundHex: null, foregroundHex: null, isBold: false },
    ];

    await saveConditionalFormats(42, rules);

    const [path, init] = apiFetch.mock.calls[0] as [string, { method: string; body: string }];
    expect(path).toBe('/api/v1/template-versions/42/conditional-formats');
    expect(init.method).toBe('PUT');
    expect(JSON.parse(init.body)).toEqual({ rules });
  });
});