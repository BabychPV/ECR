import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { MethodologyDraftVersionDto } from '@/api/types';
import { VersionDiffModal } from '@/features/methodologies/VersionDiffModal';
import {
  defaultBaseVersion,
  groupDiff,
  type MethodologyDiffItem,
} from '@/features/methodologies/versionDiff';
import { testTheme } from '@/test/render';

/**
 * Порівняння двох версій методології (`BE-25`).
 *
 * ⚠ Адреса перевіряється по-справжньому (`fetch`-стаб): без `baseVersionId`
 * сервер відповів би `400`, а серверні тести ходять у маршрут самі й цього не
 * побачили б.
 */

function version(id: number, status: 'Draft' | 'Published' | 'Deprecated', number: string): MethodologyDraftVersionDto {
  return {
    calendarMode: 'Actual',
    createdByUserId: 1,
    effectiveFrom: status === 'Draft' ? null : '2026-01-01',
    id,
    isEditable: status === 'Draft',
    level: 'Configuration',
    numericMode: 'Strict',
    status,
    traceLevel: 'ErrorsOnly',
    versionNumber: number,
  };
}

function item(overrides: Partial<MethodologyDiffItem>): MethodologyDiffItem {
  return {
    after: null,
    before: null,
    category: null,
    change: 'Changed',
    changedFields: [],
    code: 'X',
    kind: 'Formula',
    substanceEntryId: null,
    validFrom: null,
    ...overrides,
  };
}

const sent: string[] = [];

function mockServer(status: number, body: unknown): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      sent.push(String(url));

      return new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
      });
    }),
  );
}

const Versions = [
  version(10, 'Deprecated', '2025.1'),
  version(11, 'Published', '2026.1'),
  version(12, 'Deprecated', '2026.1a'),
  version(13, 'Draft', '2026.2'),
];

function show(target: MethodologyDraftVersionDto = version(13, 'Draft', '2026.2')): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <VersionDiffModal methodologyId={5} versions={Versions} target={target} onClose={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('defaultBaseVersion', () => {
  it('найближча попередня ОПУБЛІКОВАНА, а не просто попередня', () => {
    expect(defaultBaseVersion(Versions, 13)?.id).toBe(11);
  });

  it('попередньої опублікованої немає — просто попередня', () => {
    expect(defaultBaseVersion([version(1, 'Deprecated', 'a'), version(2, 'Draft', 'b')], 2)?.id).toBe(1);
  });

  it('найстаріша версія — найближча наступна; одна версія — бази немає', () => {
    expect(defaultBaseVersion(Versions, 10)?.id).toBe(11);
    expect(defaultBaseVersion([version(1, 'Draft', 'a')], 1)).toBeUndefined();
  });
});

describe('groupDiff', () => {
  it('розкладає за наборами', () => {
    const groups = groupDiff([
      item({ kind: 'TestCase', code: 'T' }),
      item({ kind: 'Formula', code: 'F' }),
      item({ kind: 'Constant', code: 'C' }),
    ]);

    expect(groups.Formula.map((i) => i.code)).toEqual(['F']);
    expect(groups.Constant.map((i) => i.code)).toEqual(['C']);
    expect(groups.TestCase.map((i) => i.code)).toEqual(['T']);
  });
});

describe('VersionDiffModal', () => {
  it('питає різницю з baseVersionId попередньої опублікованої й групує результат', async () => {
    mockServer(200, {
      baseVersionId: 11,
      methodologyVersionId: 13,
      items: [
        item({
          kind: 'Formula',
          code: 'E_NOX',
          change: 'Changed',
          changedFields: ['expression'],
          before: 'M * K1',
          after: 'M * K2',
        }),
        item({ kind: 'Formula', code: 'E_OLD', change: 'Removed', before: 'M * 2' }),
        item({
          kind: 'Constant',
          code: 'K2',
          change: 'Added',
          after: '0.35',
          category: 'Boiler',
          validFrom: '2026-01-01',
        }),
        item({ kind: 'TestCase', code: 'GOLD-1', change: 'Changed', changedFields: ['expected'], before: '1', after: '2' }),
      ],
    });
    show();

    const formulas = await screen.findByRole('region', { name: '⟦methodologies.formulas⟧' });

    expect(sent).toEqual(['/api/v1/methodologies/5/versions/13/diff?baseVersionId=11']);

    // Змінена формула: бейдж, змінене поле, «до → після» моноширинним.
    const changed = within(formulas).getByText('E_NOX').closest('tr');
    expect(changed?.getAttribute('data-change')).toBe('Changed');
    expect(within(changed as HTMLElement).getByText('⟦methodologies.changeChanged⟧')).toBeDefined();
    expect(within(changed as HTMLElement).getByText('expression')).toBeDefined();
    const values = within(changed as HTMLElement).getAllByTestId('diff-formula');
    expect(values.map((v) => v.textContent)).toEqual(['M * K1', 'M * K2']);
    expect(values[0]?.getAttribute('style') ?? '').toContain('monospace');

    // ⛔ Прибрана — «прибрано», а не «додано».
    const removed = within(formulas).getByText('E_OLD').closest('tr') as HTMLElement;
    expect(within(removed).getByText('⟦methodologies.changeRemoved⟧')).toBeDefined();
    expect(within(removed).queryByText('⟦methodologies.changeAdded⟧')).toBeNull();

    const constants = screen.getByRole('region', { name: '⟦methodologies.constants⟧' });
    const added = within(constants).getByText('K2').closest('tr') as HTMLElement;
    expect(within(added).getByText('⟦methodologies.changeAdded⟧')).toBeDefined();
    expect(added.textContent).toContain('Boiler');
    expect(added.textContent).toContain('0.35');

    expect(screen.getByRole('region', { name: '⟦methodologies.tests⟧' }).textContent).toContain('GOLD-1');
    expect(screen.queryByTestId('version-diff-same')).toBeNull();
  });

  it('змін немає — окреме речення і ЖОДНОГО бейджа', async () => {
    mockServer(200, { baseVersionId: 11, methodologyVersionId: 13, items: [] });
    show();

    expect((await screen.findByTestId('version-diff-same')).textContent).toBe('⟦methodologies.compareSame⟧');
    expect(document.querySelectorAll('[data-change-badge]')).toHaveLength(0);
    expect(screen.queryByRole('region')).toBeNull();
  });

  it('відмова — банер із причиною сервера, а не «змін немає» (L10)', async () => {
    mockServer(404, {
      title: 'err.ECR-CALC-0404',
      status: 404,
      errorCode: 'ECR-CALC-0404',
      correlationId: 'cid-diff',
      detail: 'Methodology version 11 does not exist in this methodology.',
      messageKey: 'err.ECR-CALC-0404.version',
      methodologyVersionId: '11',
    });
    show();

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Methodology version 11 does not exist in this methodology.');
    expect(alert.textContent).toContain('ECR-CALC-0404');
    expect(screen.queryByTestId('version-diff-same')).toBeNull();
  });

  it('правил і режимів у порівнянні немає — ні групи, ні підпису', async () => {
    mockServer(200, {
      baseVersionId: 11,
      methodologyVersionId: 13,
      items: [item({ kind: 'Formula', code: 'F', change: 'Added', after: '1' })],
    });
    show();

    await screen.findByRole('region', { name: '⟦methodologies.formulas⟧' });
    const dialog = screen.getByRole('dialog');

    expect(dialog.textContent).not.toMatch(/rules|bindings|numericMode|modes/i);
  });
});
