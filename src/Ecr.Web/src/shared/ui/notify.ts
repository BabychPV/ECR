import { notifications } from '@mantine/notifications';
import { EcrApiError } from '@/api/client';

/**
 * Показує причину відмови так, як її назвав сервер.
 *
 * ⛔ Саме `error.message`, а не «не вдалося». Відмови цієї системи змістовні:
 * «період закрито — спершу відкрийте період» (`ECR-PRD-4223`), «затвердження
 * відхилено: маршрут не містить вашої ролі» (`ECR-ACCS-0403`), «коментар при
 * відхиленні обов'язковий» (`ECR-DOC-0422`). Замінити їх на «щось пішло не
 * так» означає викинути єдину підказку, яка веде до дії (`ФВ-14.24`).
 *
 * ⚠ Функція існує тому, що цей самий блок був скопійований у кожному екрані.
 * Скопійований — означає, що в одному з них рано чи пізно лишиться `String(error)`
 * без розбору, і саме там відмова стане німою.
 */
export function showApiError(error: unknown): void {
  notifications.show({
    color: 'statusError',
    message: error instanceof EcrApiError ? error.message : String(error),
  });
}

/** Показує підтвердження успішної дії. */
export function showDone(message: string): void {
  notifications.show({ color: 'statusSuccess', message });
}
