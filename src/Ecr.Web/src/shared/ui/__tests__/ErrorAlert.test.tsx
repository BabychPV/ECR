import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { EcrApiError } from '@/api/client';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';

/**
 * `ФВ-14.24`: стан помилки веде до виходу — показує, що сталося (текст
 * сервера), стабільний код, ідентифікатор кореляції і дію.
 *
 * ⚠ `ErrorAlert` — ЄДИНЕ місце показу відмови (коментар у компоненті): ним
 * малюють і форми, і `AsyncBoundary`. Тому всі чотири складові перевіряються
 * тут, на одному рендері, а не поодинці на різних екранах — вимога про те,
 * що вони є РАЗОМ.
 *
 * ⚠ Подробиця з `messageKey`: без нього `problemText` навмисно ховає сирий
 * `detail` (рішення людини «залежно від обраної мови»), і тест перевіряв би
 * екран, на якому тексту сервера немає за задумом.
 */
function refusal(): EcrApiError {
  return new EcrApiError({
    title: 'Період закрито',
    detail: 'Період 2026-08 закрито: зміни потребують погодження.',
    status: 409,
    // ⚠ Код, який `client.ts` породжує сам, — не вигаданий із каталогу
    // (сторож `ClientErrorCodeTests`).
    errorCode: 'HTTP-409',
    correlationId: 'cid-fv-14-24',
    extensions2: { messageKey: 'err.HTTP-409.closed' },
  });
}

describe('ErrorAlert — стан помилки веде до виходу', () => {
  // ⚠ ФВ-14.24: мутаційно НЕ доведено (класифікатор дозволів, 2026-09-27/28;
  // рішення людини — здавати з приміткою).
  it('ФВ-14.24: показує текст сервера, код, кореляцію і дію «повторити»', () => {
    const retry = vi.fn();

    render(
      <MantineProvider>
        <ErrorAlert error={refusal()} onRetry={retry} />
      </MantineProvider>,
    );

    const alert = screen.getByRole('alert');

    // Що сталося — текст сервера, а не власне «щось пішло не так».
    expect(alert.textContent).toContain('Період закрито');
    expect(alert.textContent).toContain('Період 2026-08 закрито: зміни потребують погодження.');

    // Стабільний код і кореляція — те, з чим ідуть у підтримку.
    expect(alert.textContent).toContain('HTTP-409');
    expect(alert.textContent).toContain('cid-fv-14-24');

    // ⛔ Дія: тупикових екранів не буває. Кнопка є І справді повторює.
    fireEvent.click(screen.getByRole('button'));
    expect(retry).toHaveBeenCalledOnce();
  });
});
