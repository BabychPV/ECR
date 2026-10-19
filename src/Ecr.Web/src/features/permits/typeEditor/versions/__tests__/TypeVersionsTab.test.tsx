import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { permitType } from '../../dialogs/__tests__/fixtures';
import { TypeVersionsTab, versionRows } from '../TypeVersionsTab';

/** Вкладка «Versions» (PM-08c): лише відомі версії, без вигаданої історії. */

const k = (key: string): string => `⟦permits.editor.versions.${key}⟧`;

const published = permitType({
  status: 'Published',
  version: 2,
  draftVersion: null,
  hasUnpublishedChanges: false,
  publishedAt: '2026-10-01T08:00:00Z',
});

describe('versionRows', () => {
  it('ніколи не опублікований: лише чернетка', () => {
    expect(versionRows(permitType({ version: 1, draftVersion: 1 }))).toEqual([
      { key: 'draft', version: 1, state: 'Draft', date: '2026-10-18T09:30:00Z' },
    ]);
  });

  it('опублікований без змін: лише опублікована версія з датою публікації', () => {
    expect(versionRows(published)).toEqual([
      { key: 'published', version: 2, state: 'Published', date: '2026-10-01T08:00:00Z' },
    ]);
  });

  it('опублікований із відкритою чернеткою: нова зверху, v(N+1)', () => {
    const rows = versionRows({ ...published, hasUnpublishedChanges: true, draftVersion: 3 });
    expect(rows.map((r) => [r.version, r.state])).toEqual([
      [3, 'Draft'],
      [2, 'Published'],
    ]);
  });

  it('чернетка без номера в DTO отримує version+1', () => {
    const rows = versionRows({ ...published, hasUnpublishedChanges: true, draftVersion: null });
    expect(rows[0]?.version).toBe(3);
  });
});

describe('TypeVersionsTab', () => {
  it('показує лише відомі колонки: Version, State, Date; Author і What changed немає', () => {
    renderWithMantine(<TypeVersionsTab type={published} />);

    expect(screen.getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      k('colVersion'),
      k('colState'),
      k('colDate'),
    ]);
    expect(screen.getAllByRole('row')).toHaveLength(2);
    expect(screen.getByText('v2')).toBeDefined();
    expect(screen.queryByText(/Author|What changed/)).toBeNull();
  });

  it('примітка «опубліковані версії не редагуються» присутня; дата — елемент time', () => {
    const { container } = renderWithMantine(<TypeVersionsTab type={published} />);
    expect(screen.getByText(k('note'))).toBeDefined();
    expect(container.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-01T08:00:00Z');
  });

  it('чернетка поруч з опублікованою — два рядки без вигаданих версій', () => {
    renderWithMantine(
      <TypeVersionsTab type={{ ...published, hasUnpublishedChanges: true, draftVersion: 3 }} />,
    );
    expect(screen.getAllByRole('row')).toHaveLength(3);
    expect(screen.getByText('v3')).toBeDefined();
    expect(screen.getByText('v2')).toBeDefined();
    expect(screen.queryByText('v1')).toBeNull();
  });
});
