import { afterEach, describe, expect, it } from 'vitest';
import { waitFor } from '@testing-library/react';
import { focusHeadingAfterNavigation, pageHeading } from '../focusHeadingAfterNavigation';

/**
 * P3-1: фокус після діалогу, що закривається разом із переходом. Реальний порядок: кнопка «More» у фокусі -> сторінка
 * міняється (кнопка зникає, фокус падає на BODY) -> з'являється заголовок сторінки-призначення -> фокус на ньому.
 *
 * Мутаційний доказ (вручну, відновлено): порожнє тіло `focusHeadingAfterNavigation` валить перші два тести (третій - негативний, захист від крадіжки фокуса).
 */
function mountPage(title: string, withMore: boolean): HTMLElement {
  document.body.replaceChildren();
  if (withMore) {
    const more = document.createElement('button');
    more.setAttribute('data-permit-more', '');
    more.textContent = 'More';
    document.body.append(more);
  }
  const h1 = document.createElement('h1');
  h1.className = 'ecr-route-heading';
  h1.tabIndex = -1;
  h1.textContent = title;
  document.body.append(h1);

  return h1;
}

const frame = (): Promise<void> => new Promise((resolve) => requestAnimationFrame(() => resolve()));

afterEach(() => document.body.replaceChildren());

describe('focusHeadingAfterNavigation', () => {
  it('нова сторінка з іншим h1: фокус переходить на нього, а не лишається на BODY', async () => {
    const oldHeading = mountPage('Old', true);
    document.querySelector<HTMLElement>('[data-permit-more]')?.focus();
    focusHeadingAfterNavigation({ previous: oldHeading });

    await frame();
    await frame();
    const next = mountPage('List', false); // перехід: кнопка зникла, фокус на BODY
    expect(document.activeElement).toBe(document.body);

    await waitFor(() => expect(document.activeElement).toBe(next));
  });

  it('той самий екран (Renew): чекає номер нової чернетки в заголовку, а не фокусує старий', async () => {
    const heading = mountPage('KZ-100', true);
    focusHeadingAfterNavigation({ previous: heading, expectText: 'KZ-100-R1' });

    for (let i = 0; i < 5; i += 1) await frame();
    expect(document.activeElement).not.toBe(heading);

    heading.textContent = 'KZ-100-R1';
    await waitFor(() => expect(document.activeElement).toBe(heading));
    expect(pageHeading()).toBe(heading);
  });

  it('якщо людина вже поставила фокус деінде - не відбирає його', async () => {
    const oldHeading = mountPage('Old', true);
    focusHeadingAfterNavigation({ previous: oldHeading });
    const input = document.createElement('input');
    document.body.append(input);
    input.focus();

    await frame();
    mountPage('List', false);
    document.body.append(input);
    input.focus();
    for (let i = 0; i < 4; i += 1) await frame();

    expect(document.activeElement).toBe(input);
  });
});
