import { readFileSync } from 'node:fs';
import path from 'node:path';
import type { JSX } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { NoticeLine, NoticeList } from '../NoticeLine';
import { NoticeRow } from '../NoticeRow';
import { publishNotice, resetNotices, withdrawNotice } from '../noticeStore';

/**
 * ✎ DOCUX L7 (AC L7-1): 5 активних повідомлень → над сіткою ОДИН рядок із найважливішим і «+4 more»; «+4 more» відкриває
 * вкладку Status (`?panel=status`), де перелічено всі. Без фіксу червоні: кожне повідомлення було окремим блоком.
 */
function Where(): JSX.Element {
  return <output data-testid="where">{useLocation().search}</output>;
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/documents/1']}>
        <NoticeLine />
        <aside data-testid="status-tab">
          <NoticeList />
        </aside>
        <Where />
      </MemoryRouter>
    </MantineProvider>,
  );
}

function add(id: string, priority: number): void {
  publishNotice({ id, priority, render: () => <NoticeRow tone="info" text={`text ${id}`} testId={`row-${id}`} /> });
}

afterEach(() => resetNotices());

describe('NoticeLine / Status', () => {
  it('немає повідомлень — рядка немає; Status каже, що все гаразд', () => {
    show();
    expect(screen.queryByTestId('document-notice-line')).toBeNull();
    expect(screen.getByTestId('document-notice-list-empty')).toBeTruthy();
  });

  it('5 повідомлень — один рядок із найважливішим за пріоритетом і «+4 more» → Status; знято — рядок оновився', () => {
    show();
    act(() => {
      add('live', 7);
      add('calc', 6);
      add('blocked', 4);
      add('split', 5);
      add('conflict', 3);
    });

    const line = screen.getByTestId('document-notice-line');
    expect(line.getAttribute('data-notice-top')).toBe('conflict');
    expect(line.querySelectorAll('.ecr-notice-row')).toHaveLength(1);
    expect(screen.getByTestId('document-notice-more').textContent).toContain('document.notices.more (count=4)');

    const list = screen.getByTestId('document-notice-list');
    expect([...list.querySelectorAll('[data-notice-id]')].map((node) => node.getAttribute('data-notice-id'))).toEqual([
      'conflict',
      'blocked',
      'split',
      'calc',
      'live',
    ]);

    fireEvent.click(screen.getByTestId('document-notice-more'));
    expect(screen.getByTestId('where').textContent).toContain('panel=status');

    act(() => withdrawNotice('conflict'));
    expect(screen.getByTestId('document-notice-line').getAttribute('data-notice-top')).toBe('blocked');
    expect(screen.getByTestId('document-notice-more').textContent).toContain('count=3');
  });

  it('одне повідомлення — без «+N more»; повторна публікація того самого id не міняє порядку', () => {
    show();
    act(() => {
      add('a', 4);
      add('b', 4);
      add('a', 4);
    });
    expect(screen.getByTestId('document-notice-line').getAttribute('data-notice-top')).toBe('a');
    act(() => withdrawNotice('b'));
    expect(screen.queryByTestId('document-notice-more')).toBeNull();
  });

  // ✎ DOCUX (живий прохід 27.11): довгий текст обрізався посеред слова без «…» (рядок над сіткою) і давав горизонтальну
  // прокрутку інспектора (Status). Розкладку jsdom не рахує — сторож правил CSS і класів, на яких вони тримаються.
  it('рядок — один, з «…» і повним текстом у підказці; Status — переносить повний текст', () => {
    const long = `Calculation finished with problems in June 2026 · submission may be refused — ${'148 no calculation rule · '.repeat(6)}`;
    publishNotice({ id: 'calc', priority: 6, render: () => <NoticeRow tone="error" text={long} testId="row-calc" /> });
    show();

    const [line, full] = screen.getAllByTestId('row-calc');
    expect(line?.closest('.ecr-notice-list--full')).toBeNull();
    expect(line?.querySelector('.ecr-notice-text')?.getAttribute('title')).toBe(long);
    expect(full?.closest('.ecr-notice-list--full')).not.toBeNull();

    const css = readFileSync(path.resolve(process.cwd(), 'src/features/documents/notices/notices.css'), 'utf8');
    const rule = (selector: string): string => {
      const start = css.indexOf(`${selector} {`);
      return start < 0 ? '' : css.slice(start, css.indexOf('}', start));
    };
    // Колонка сітки не ширша за контейнер — інакше «…» не з'являється, а текст ріже край батька.
    expect(rule('.ecr-notice-list')).toContain('grid-template-columns: minmax(0, 1fr)');
    expect(rule('.ecr-notice-list > *')).toContain('min-width: 0');
    expect(rule('.ecr-notice-text')).toContain('text-overflow: ellipsis');
    // Status: переносить і рядок повідомлення, і смугу розрахунку.
    expect(css).toMatch(/\.ecr-notice-list--full \.ecr-notice-text,\s+\.ecr-notice-list--full \.ecr-calc-strip-text \{/);
    const wrap = rule('.ecr-notice-list--full .ecr-calc-strip-text');
    expect(wrap).toContain('white-space: normal');
    expect(wrap).toContain('overflow-wrap: anywhere');
  });
});
