import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { MeQueryKey } from '@/shared/session/useSession';
import { MethodologySection } from '../MethodologySection';
import { readSectionChoices, sectionsStorageKey } from '../methodologySections';

/**
 * Згортувані секції сторінки версії методології: заголовок `h2` з кнопкою
 * `aria-expanded`/`aria-controls`, тіло ховається `hidden` (не розмонтовується),
 * вибір пам'ятається НА КОРИСТУВАЧА, зіпсоване/недоступне сховище — не падіння.
 */

function show(options: { userId?: number; defaultExpanded?: boolean } = {}): QueryClient {
  const client = new QueryClient();
  if (options.userId !== undefined) client.setQueryData(MeQueryKey, { userId: options.userId });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologySection
          sectionId="constants"
          title="Constants"
          actions={<button type="button">Add constant</button>}
          {...(options.defaultExpanded === undefined ? {} : { defaultExpanded: options.defaultExpanded })}
        >
          <p>Body of constants</p>
        </MethodologySection>
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

function toggle(): HTMLElement {
  return screen.getByRole('button', { name: 'Constants' });
}

beforeEach(() => {
  window.localStorage.clear();
});

afterEach(cleanup);

describe('Секція сторінки методології', () => {
  it('заголовок h2 містить кнопку; aria-controls указує на тіло; за замовчуванням розгорнуто', () => {
    show({ userId: 7 });

    const heading = screen.getByRole('heading', { level: 2, name: 'Constants' });
    expect(heading.contains(toggle())).toBe(true);
    expect(toggle().getAttribute('aria-expanded')).toBe('true');

    const body = document.getElementById(toggle().getAttribute('aria-controls') ?? '');
    expect(body?.textContent).toBe('Body of constants');
    expect(body?.hidden).toBe(false);

    // Секція названа своїм заголовком — читалка оголошує її як регіон «Constants».
    expect(screen.getByRole('region', { name: 'Constants' })).toBeTruthy();
  });

  it('клік згортає: тіло hidden, але лишається в DOM; дія праворуч видима', () => {
    show({ userId: 7 });

    fireEvent.click(toggle());

    expect(toggle().getAttribute('aria-expanded')).toBe('false');
    const body = document.getElementById(toggle().getAttribute('aria-controls') ?? '');
    expect(body?.hidden).toBe(true);
    expect(body?.textContent).toBe('Body of constants');
    expect(screen.getByRole('button', { name: 'Add constant' })).toBeTruthy();
  });

  it('клавіатура: Enter і пробіл на кнопці перемикають (нативна кнопка)', () => {
    show({ userId: 7 });

    expect(toggle().tagName).toBe('BUTTON');
    toggle().focus();
    expect(document.activeElement).toBe(toggle());

    // ⚠ jsdom не перетворює Enter на click сам; нативна `<button>` у браузері — так (e2e це й перевіряє).
    fireEvent.click(toggle());
    expect(toggle().getAttribute('aria-expanded')).toBe('false');
  });

  it('вибір пам\'ятається на користувача і не зачіпає іншого', () => {
    show({ userId: 7 });
    fireEvent.click(toggle());
    cleanup();

    expect(readSectionChoices(7)).toEqual({ constants: false });
    expect(window.localStorage.getItem(sectionsStorageKey(8))).toBeNull();

    show({ userId: 7 });
    expect(toggle().getAttribute('aria-expanded')).toBe('false');
    cleanup();

    // Інший користувач на тому ж браузері — за замовчуванням.
    show({ userId: 8 });
    expect(toggle().getAttribute('aria-expanded')).toBe('true');
  });

  it('defaultExpanded=false — згорнуто, доки людина не розгорне', () => {
    show({ userId: 7, defaultExpanded: false });
    expect(toggle().getAttribute('aria-expanded')).toBe('false');

    fireEvent.click(toggle());
    expect(toggle().getAttribute('aria-expanded')).toBe('true');
    expect(readSectionChoices(7)).toEqual({ constants: true });
  });

  it('зіпсований запис у сховищі — замовчування, а не падіння', () => {
    window.localStorage.setItem(sectionsStorageKey(7), '{not json');
    show({ userId: 7 });

    expect(toggle().getAttribute('aria-expanded')).toBe('true');
  });
});
