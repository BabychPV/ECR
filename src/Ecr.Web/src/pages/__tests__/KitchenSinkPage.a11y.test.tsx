import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react';
import { KitchenSinkPage } from '@/pages/KitchenSinkPage';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';

/**
 * Доступність каталогу компонентів у ОБОХ темах — і разом із нею перша й поки
 * єдина перевірка `axe` для `DataTable` та `FilterBar` (`ФВ-14.16`, `D-127`).
 *
 * ⛔ Чому це не дублювання сусідніх файлів. `StatStrip.a11y` і `Wizard.a11y`
 * перевіряють свої компоненти ПООДИНЦІ, у мінімальній обгортці. Тут вони
 * стоять поруч, у тій самій розмітці, що й на екрані, і саме поруч видно те,
 * чого поодинці не видно: порядок заголовків між розділами, дві таблиці на
 * одній сторінці, смуга кнопок-тумблерів над рядком фільтрів із власними
 * полями.
 *
 * ⛔ `DataTable` і `FilterBar` прийшли БЕЗ власних `.a11y.`-файлів (#425,
 * #426). Тобто до цього PR обидва не бачив жоден із двох гейтів доступності —
 * компонент, який малює всю таблицю переліку, і компонент, який малює всі
 * поля фільтрів. Цей файл закриває саме це.
 *
 * ⚠ Сторінка сама тягне каталог рядків (`loadCatalog`) і живе поза
 * `AppLayout` — `Shell` дає їй `MemoryRouter`, без якого `FilterBar` не
 * змонтується: його стан живе в адресі.
 */

afterEach(cleanup);

describe('Каталог компонентів — axe без блокуючих порушень', { timeout: 30_000 }, () => {
  it.each(Themes)('тема %s: уся сторінка, майстер закритий', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        <KitchenSinkPage />
      </Shell>,
    );

    // ⚠ Дочекатися таблиці, а не просто змонтувати: `DataTable` малює вміст
    // через `AsyncBoundary`, і знімок до цього моменту перевіряв би скелет.
    await screen.findByRole('table', { name: /L5/ });

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });

  it.each(Themes)('тема %s: майстер ВІДКРИТИЙ — діалог поверх сторінки', async (scheme) => {
    /*
     * ⛔ Окремий випадок, бо це інша розмітка, а не той самий екран. Відкритий
     * `Modal` Mantine ставить `aria-hidden` на решту сторінки й переносить
     * себе в портал: перевіряти треба `document.body`, а не `container`, —
     * інакше `axe` дивиться на контейнер, у якому діалогу вже немає.
     */
    render(
      <Shell colorScheme={scheme}>
        <KitchenSinkPage />
      </Shell>,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Відкрити майстер' }));
    await waitFor(() => {
      expect(screen.getByRole('dialog')).toBeTruthy();
    });

    const violations = await findViolations(document.body);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
