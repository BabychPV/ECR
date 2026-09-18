import { describe, it, expect, afterEach, beforeEach } from 'vitest';
import { act, render, screen, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { EcrApiError } from '@/api/client';
import { theme } from '@/shared/theme/theme';
import { showApiError, showDone } from '@/shared/ui/notify';

/**
 * UI-прохід, F8: у хрестика на тості має бути доступне ім'я.
 *
 * ⛔ Чому цього не бачив набір `accessibility.*.a11y.test.tsx`: `Shell`
 * (`src/test/__tests__/a11yFixtures.tsx`) монтує `MantineProvider`,
 * `QueryClientProvider` і `MemoryRouter` — і жодного `<Notifications />`.
 * Тост у сканованому дереві не з'являється НІКОЛИ, тож axe його кнопки не
 * бачить узагалі. Це не послаблення правила `button-name`, а межа фікстури:
 * сканується те, що змонтовано.
 *
 * ⚠ Другу половину F8 — стрілки лічильника `NumberInput` — цей файл не
 * перевіряє навмисно. Mantine 7.15.2 ставить на обидві стрілки
 * `aria-hidden="true"` і `tabIndex={-1}` (`NumberInput.mjs`), тобто з дерева
 * доступності вони вже прибрані: читалка їх не оголошує, і axe правильно їх
 * пропускає. Кнопка поза деревом доступності не потребує імені.
 */
function show(): void {
  render(
    <MantineProvider>
      <Notifications />
    </MantineProvider>,
  );
}

/*
 * ⚠ Сховище сповіщень — на рівні МОДУЛЯ, а не компонента: без очистки тост
 * попереднього тесту лишається в ньому й наступний `getByRole` падає на
 * «знайдено два» — тобто тест падав би не на своїй причині.
 */
beforeEach(() => {
  notifications.cleanQueue();
  notifications.clean();
});

afterEach(() => {
  cleanup();
});

describe('Сповіщення: кнопка закриття має ім’я', () => {
  it('F8: тост підтвердження — хрестик знаходиться за роллю та іменем', async () => {
    show();

    await act(async () => {
      showDone('Документ подано.');
    });

    // ⛔ Головне твердження, і воно ж мутаційний доказ: приберіть
    // `closeButtonProps` у `shared/ui/notify.ts` — кнопка лишиться на екрані,
    // але `getByRole('button', { name })` її вже не знайде, бо імені немає.
    expect(screen.getByRole('button', { name: 'Close notification' })).toBeDefined();
  });

  it('F8: тост відмови — той самий хрестик, те саме ім’я', async () => {
    show();

    await act(async () => {
      showApiError(
        new EcrApiError({
          title: 'Період закрито',
          detail: 'Період закрито: зміни потребують окремого погодження.',
          status: 409,
          errorCode: 'HTTP-409',
          correlationId: 'cid-notify-1',
        }),
      );
    });

    expect(screen.getByRole('button', { name: 'Close notification' })).toBeDefined();
  });

  it('F8: ім’я не підмінює саме повідомлення — текст відмови лишається видимим', async () => {
    show();

    await act(async () => {
      showDone('Документ подано.');
    });

    // ⚠ Сторож проти «полагодили ім'я, загубили зміст»: `closeButtonProps`
    // додається ПОРУЧ із `message`, а не замість нього.
    expect(screen.getByText('Документ подано.')).toBeDefined();
  });

  /**
   * ⛔ Найважливіше твердження файлу, і воно НЕ про `shared/ui/notify.ts`.
   *
   * Одинадцять місць кличуть `notifications.show(...)` повз обгортку
   * (`SheetActions.tsx`, `DocumentPage.tsx`, `ExportButton.tsx`,
   * `UserAccessEditor.tsx`, `CreateMappingModal.tsx`, `SourcesPage.tsx`,
   * `PeriodsPage.tsx`, `SnapshotsPage.tsx`, `GrantsPanel.tsx`). Виправлення
   * лише в обгортці закрило б два виклики з тринадцяти — і звіт «F8 закрито»
   * був би неправдою для більшості тостів у застосунку.
   *
   * Тому тут `notifications.show` кличеться НАПРЯМУ, а провайдер бере
   * СПРАВЖНЮ тему застосунку: доводиться, що ім'я приходить із `theme.ts`, а
   * не з обгортки.
   */
  it('F8: тост, показаний повз обгортку, теж має ім’я — воно з теми', async () => {
    render(
      <MantineProvider theme={theme}>
        <Notifications />
      </MantineProvider>,
    );

    await act(async () => {
      notifications.show({ message: 'Розрахунок поставлено в чергу.' });
    });

    // ⛔ Мутаційний доказ: приберіть `Notification` із `components` у
    // `theme.ts` — цей тест падає, а два перші лишаються зеленими.
    expect(screen.getByRole('button', { name: 'Close notification' })).toBeDefined();
  });
});
