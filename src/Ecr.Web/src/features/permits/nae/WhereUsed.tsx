import type { JSX } from 'react';
import { Text, Title } from '@mantine/core';
import { t } from '@/shared/i18n';

/**
 * «Де використовується» вузол структури: дозволи, що посилаються на нього (макет N2: дозвіл · тип · термін · речовини).
 *
 * ⛔ Даних тут немає СВІДОМО. Контракт N1/N2 не має читання «дозволи за вузлом»: `GET /registries/{code}/usage` відповідає
 * про довідник, а не про запис, а `rc814/entryUsage` збирає лише посилання полями `Lookup`. Дозволи-екземпляри живуть у
 * `PermitsController`, і вузол структури там - рядок таблиці викидів, не поле запису. Тому замість вигаданих рядків -
 * чесний стан «недоступно». Бракує серверу: читання на кшталт `GET /api/v1/registries/{code}/entries/{id}/references`
 * (або фільтр `nae` у переліку дозволів) з полями: дозвіл, тип, термін дії, кількість речовин. Щойно воно з'явиться,
 * таблиця нижче наповнюється, а стан «недоступно» зникає.
 *
 * ⚠ «Не знаю» ≠ «ніде»: порожній стан не пише «не використовується».
 */
export function WhereUsed(): JSX.Element {
  return (
    <section aria-labelledby="nae-used-title" data-nae-used="unavailable">
      <Title order={3} size="h5" id="nae-used-title">
        {t('permits.nae.used.title')}
      </Title>
      <table className="nae-used-table">
        <thead>
          <tr>
            <th scope="col">{t('permits.nae.used.permit')}</th>
            <th scope="col">{t('permits.nae.used.type')}</th>
            <th scope="col">{t('permits.nae.used.valid')}</th>
            <th scope="col">{t('permits.nae.used.substances')}</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td colSpan={4}>
              <Text size="sm" c="dimmed" role="status" data-nae-used-state="">
                {t('permits.nae.used.unavailable')}
              </Text>
            </td>
          </tr>
        </tbody>
      </table>
    </section>
  );
}
