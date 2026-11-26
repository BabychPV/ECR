import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { NoticeLine, NoticeList } from '../NoticeLine';
import { NoticeRow } from '../NoticeRow';
import { NoticeSlot } from '../NoticeSlot';
import { publishNotice, resetNotices } from '../noticeStore';
import { useStatusRequests } from '../useOpenStatus';

/**
 * ✎ DOCUX L7 (доручення координатора: «без боргів»): блоки-повідомлення (відновлення правок, поділ рядків, передача
 * місяця, чужа зміна, збій збереження, конфлікт, округлення, зауваження) — на сторінці документа ЛИШЕ в рядку повідомлень
 * (підсумок + головна дія + Details) і повністю в Status; без рядка (інший екран, тест компонента) — на місці.
 * ⛔ Те, що зупиняє наступну дію (`blocking`), — завжди видиме власним рядком, не за «+N more».
 */
function Full(): JSX.Element {
  return <div data-testid="full-banner">full content</div>;
}

function Slot({ blocking = false }: { readonly blocking?: boolean }): JSX.Element | null {
  return (
    <NoticeSlot id="slot" priority={blocking ? 2 : 8} blocking={blocking} tone="warning" text="summary text" actions={[{ label: 'Act', run: vi.fn(), testId: 'slot-act' }]} testId="slot-row">
      <Full />
    </NoticeSlot>
  );
}

function StatusListener({ onOpen }: { readonly onOpen: () => void }): null {
  useStatusRequests(onOpen);
  return null;
}

afterEach(() => resetNotices());

describe('NoticeSlot', () => {
  it('без рядка повідомлень — повний блок на місці, як і раніше', () => {
    render(
      <MantineProvider theme={testTheme}>
        <Slot />
      </MantineProvider>,
    );
    expect(screen.getByTestId('full-banner')).toBeTruthy();
    expect(screen.queryByTestId('slot-row')).toBeNull();
  });

  it('є рядок — над сіткою підсумок із дією і «Details» (відкриває Status), повний вміст — лише у Status', () => {
    const onOpen = vi.fn();
    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter>
          <StatusListener onOpen={onOpen} />
          <NoticeLine />
          <main data-testid="page">
            <Slot />
          </main>
          <aside data-testid="status">
            <NoticeList />
          </aside>
        </MemoryRouter>
      </MantineProvider>,
    );

    expect(screen.getByTestId('page').querySelector('[data-testid="full-banner"]')).toBeNull();
    const row = screen.getByTestId('document-notice-line').querySelector('[data-testid="slot-row"]') as HTMLElement;
    expect(row.textContent).toContain('summary text');
    expect(screen.getByTestId('status').querySelector('[data-testid="full-banner"]')).not.toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /document\.notices\.details/ }));
    expect(onOpen).toHaveBeenCalledTimes(1);
  });

  it('blocking — у рядку завжди видиме, навіть коли важливіше за пріоритетом інше; решта — «+N more»', () => {
    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter>
          <NoticeLine />
          <Slot blocking />
        </MemoryRouter>
      </MantineProvider>,
    );
    act(() => {
      publishNotice({ id: 'restore', priority: 1, blocking: true, render: () => <NoticeRow tone="warning" text="restore" testId="restore-row" /> });
      publishNotice({ id: 'info', priority: 6, render: () => <NoticeRow tone="info" text="calc" testId="info-row" /> });
    });

    const line = screen.getByTestId('document-notice-line');
    expect(line.querySelector('[data-testid="restore-row"]')).not.toBeNull();
    expect(line.querySelector('[data-testid="slot-row"]')).not.toBeNull();
    expect(line.querySelector('[data-testid="info-row"]')).toBeNull();
    expect(screen.getByTestId('document-notice-more').textContent).toContain('count=1');
  });
});
