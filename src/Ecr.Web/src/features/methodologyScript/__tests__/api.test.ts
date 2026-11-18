import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  formatMethodologyScriptConst,
  methodologyScript,
  methodologyScriptSubstances,
  previewMethodologyScript,
  saveMethodologyScript,
  validateMethodologyScript,
} from '../api';

interface Call {
  readonly url: string;
  readonly init: RequestInit | undefined;
}

let calls: Call[] = [];

describe('виклики API скрипта', () => {
  beforeEach(() => {
    calls = [];
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
        calls.push({ url: String(input), init });
        return Promise.resolve(
          new Response(JSON.stringify({ text: 'x', kind: 'map', diagnostics: [] }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('адреси й методи збігаються з контролером', async () => {
    await methodologyScript(3, 9);
    await saveMethodologyScript(3, 9, { text: 'a', expectedHash: 'h' });
    await validateMethodologyScript(3, 9, { text: 'a' });
    await previewMethodologyScript(3, 9, {
      documentId: 5,
      periodKey: 202604,
      text: null,
      tableInstanceId: null,
      maxRows: 50,
    });
    await formatMethodologyScriptConst(3, 9, { text: '{ a: 1 }' });
    await methodologyScriptSubstances(3, 9);

    expect(calls.map((c) => `${c.init?.method ?? 'GET'} ${c.url}`)).toEqual([
      'GET /api/v1/methodologies/3/versions/9/script',
      'PUT /api/v1/methodologies/3/versions/9/script',
      'POST /api/v1/methodologies/3/versions/9/script/validate',
      'POST /api/v1/methodologies/3/versions/9/script/preview',
      'POST /api/v1/methodologies/3/versions/9/script/format-const',
      'GET /api/v1/methodologies/3/versions/9/substances',
    ]);
  });

  it('збереження несе expectedHash; format-const віддає text, kind і діагностики як є', async () => {
    await saveMethodologyScript(3, 9, { text: 'a', expectedHash: 'h' });
    expect(JSON.parse(String(calls[0]?.init?.body))).toEqual({ text: 'a', expectedHash: 'h' });

    const formatted = await formatMethodologyScriptConst(3, 9, { text: '{ a: 1 }' });
    expect(formatted).toEqual({ text: 'x', kind: 'map', diagnostics: [] });
  });
});
