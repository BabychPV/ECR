import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { KitchenSinkPage } from '@/pages/KitchenSinkPage';
import { Shell } from '@/test/__tests__/a11yFixtures';

/**
 * Смоук + мутаційний доказ для галереї набору (`UI-10`, директива №15 §3).
 *
 * ⛔ Смоук: сторінка рендериться цілком, без падіння, і кожна секція, яку
 * `UI-10` мала додати, справді на екрані — перевіряється за власним
 * заголовком `<Title order={4}>` (`Section`, `pages/KitchenSinkPage.tsx`).
 *
 * ⛔ Мутаційний доказ (вимога DoD цього PR): приберіть будь-яку секцію зі
 * сторінки (видаліть `<Section title="…">…</Section>` цілком або лише
 * пропишіть `title` неточно) — рядок `it.each` нижче з ТОЧНО цим заголовком
 * почервоніє, решта лишаються зеленими. Перевірено вручну: тимчасове
 * видалення секції «Підказка (Hint)» з `KitchenSinkPage.tsx` валить рівно
 * один рядок цього набору («секція «Підказка (Hint)» присутня»), інші 15
 * — включно з «Стани подання» — лишаються зеленими.
 */
const SectionTitles = [
  "Стани комірки (ФВ-14.18, D-128)",
  'Стани подання (ФВ-14.21…14.25)',
  'Керування',
  'Текст і код (TwoLine, CodeText, KeyValue)',
  'Підказка (Hint)',
  'Статус (StatusBadge)',
  'Повідомлення (Banner, ResultBanner)',
  'Модальні вікна (ConfirmModal, ReasonModal)',
  'Шторка подробиць (DetailDrawer)',
  'ErrorAlert (окремо, поза AsyncBoundary — напр. у формі)',
  'Набір, шар 3 (директива №15 §2)',
  'Типографіка (ФВ-14.13)',
  'Довгий рядок (ФВ-14.30)',
] as const;

afterEach(cleanup);

describe('KitchenSinkPage — галерея набору (UI-10)', () => {
  it('рендериться без падіння: майстер закритий, усі стани поруч', async () => {
    render(
      <Shell colorScheme="light">
        <KitchenSinkPage />
      </Shell>,
    );

    // ⚠ Дочекатися таблиці, а не просто змонтувати: `DataTable` малює вміст
    // через `AsyncBoundary`, і перевірка до цього моменту бачила б лише скелет.
    expect(await screen.findByRole('table', { name: /L5/ })).toBeTruthy();
  });

  it.each(SectionTitles)('секція «%s» присутня', async (title) => {
    render(
      <Shell colorScheme="light">
        <KitchenSinkPage />
      </Shell>,
    );

    expect(await screen.findByRole('heading', { level: 4, name: title })).toBeTruthy();
  });

  it('жодного заголовка секції немає двічі (ім’я кожної унікальне)', () => {
    render(
      <Shell colorScheme="light">
        <KitchenSinkPage />
      </Shell>,
    );

    const headings = screen.getAllByRole('heading', { level: 4 }).map((h) => h.textContent);

    expect(headings).toHaveLength(new Set(headings).size);
  });
});
