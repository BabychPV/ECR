import type { JSX, ReactNode } from 'react';
import { Button, Code, Group, Skeleton, Stack, Table, Text, VisuallyHidden } from '@mantine/core';
import { useInfiniteQuery } from '@tanstack/react-query';
import { listNotificationDeliveries } from '@/features/notifications/api';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';

/**
 * Журнал доставок сповіщень (`BE-33b`) — блок екрана `/admin/notifications`.
 *
 * ⛔ **Журнал — твердження про МИНУЛЕ, і саме тому порядок станів тут не
 * косметика** (правило `L10`, директива №15 §0). Порожня таблиця під
 * заголовком «доставки» читається однозначно: «сповіщень не було». Якщо при
 * цьому запит насправді ВІДМОВИВ, застосунок щойно збрехав про минуле — і
 * збрехав переконливо, бо виглядає це точно так само, як правда. Тому
 * перевірки йдуть строго `error` → `isPending` → дані, і в стані відмови
 * таблиці немає ЗОВСІМ: банер замість неї, а не поруч із нею.
 *
 * ⛔ `AsyncBoundary` тут НЕ вживається, хоч саме цей порядок і реалізує.
 * Її порожній стан і стан «немає права» малюють власний `<Title order={4}>`,
 * а блок стоїть усередині сторінки, у якої вже є свої заголовки: рівень 4
 * після рівня 2 провалює `heading-order` в гейтах `a11y (dark)` і
 * `a11y (light)`. Тому стани тут власні, а `ErrorAlert` — той самий, що й
 * скрізь: друга подача відмови означала б, що на одному екрані код помилки
 * видно, а на іншому ні.
 *
 * ⚠ Фільтра за каналом і статусом НЕМАЄ навмисно. Сервер фільтрації ще не
 * підтримує, а відбір по вже завантаженій сторінці збрехав би тим самим
 * способом, що й порожня таблиця: «доставок цим каналом немає» при тому, що
 * вони лежать на наступній сторінці. Обіцянка фільтра дорожча за його
 * відсутність.
 */

/**
 * Рядків на сторінку.
 *
 * ⚠ Дефолт самого `listNotificationDeliveries` — теж 50; число названо тут
 * явно, бо від нього залежить видимість кнопки «показати ще», і мовчазна
 * зміна дефолту в API змінила б поведінку екрана.
 */
const PageSize = 50;

/** Рядків у скелеті: приблизно стільки видно без прокрутки. */
const SkeletonRows = 8;

export function DeliveriesPanel(): JSX.Element {
  /*
   * ⚠ `useInfiniteQuery`, а не `useQuery` з власним станом курсора:
   * «показати ще» в журналі ДОЧИТУЄ, а не гортає. Заміна сторінки означала б,
   * що рядок, який людина щойно читала, зникає з-під очей — а курсор позначає
   * позицію саме в цій видачі, і назад вороття немає.
   */
  const pages = useInfiniteQuery({
    queryKey: ['notification-deliveries', PageSize],
    queryFn: ({ pageParam }) => listNotificationDeliveries(PageSize, pageParam),
    initialPageParam: null as string | null,

    // ⚠ `null` від сервера означає «кінець»; react-query трактує `null`
    // і `undefined` однаково — сторінки більше немає, кнопки теж.
    getNextPageParam: (last) => last.nextCursor,
  });

  let body: ReactNode;

  // ⛔ 1. ВІДМОВА. Перша й безумовна: невдалий запит теж лишає перелік
  // порожнім, і будь-яка перевірка порожнечі перед цією перетворила б
  // «прочитати не вдалося» на «сповіщень не було».
  if (pages.isError) {
    body = <ErrorAlert error={pages.error} onRetry={() => void pages.refetch()} />;
  } else if (pages.isPending) {
    // ⚠ 2. У ДОРОЗІ. Скелет, а не порожня таблиця: форма майбутньої розмітки
    // видно одразу, і ніщо не стрибає під курсором (`ФВ-14.25`).
    body = (
      <Stack gap="xs" role="status" aria-busy="true">
        {/* ⚠ Скелет читалці не чутний — це порожні прямокутники. Прихований
            напис і є єдине, що відрізняє «вантажиться» від «нічого немає»
            для того, хто екрана не бачить. */}
        <VisuallyHidden>{t('common.loading')}</VisuallyHidden>
        <Skeleton height={28} radius="sm" />
        {Array.from({ length: SkeletonRows }, (_, index) => (
          <Skeleton key={index} height={24} radius="sm" />
        ))}
      </Stack>
    );
  } else {
    // 3. ДАНІ. Сюди потрапляємо лише після успішної відповіді, тож порожнеча
    // нижче — це вже справжнє твердження про минуле, а не незнання.
    const rows = (pages.data?.pages ?? []).flatMap((page) => page.items);

    body =
      rows.length === 0 ? (
        /*
         * ⚠ Пояснення, а не «таблиця без рядків» і не саме лише «Немає
         * даних»: порожній блок без слів виглядає як несправність
         * (`ФВ-14.23`).
         *
         * ⛔ Без `<Title>`: заголовки рівня тут завела б розмітка, а не
         * зміст, і порядок заголовків сторінки поїхав би саме в тому стані,
         * який найрідше відкривають, — тобто дефект помітили б останнім.
         */
        <Stack gap="xs" align="center" py="xl">
          <Text fw={600}>{t('notifications.noDeliveries')}</Text>
          <Text size="sm" c="dimmed" ta="center" maw={420}>
            {t('notifications.noDeliveriesHint')}
          </Text>
        </Stack>
      ) : (
        <>
          <Table striped className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('notifications.at')}</Table.Th>
                <Table.Th>{t('notifications.event')}</Table.Th>
                <Table.Th>{t('notifications.channel')}</Table.Th>
                <Table.Th>{t('notifications.status')}</Table.Th>
                <Table.Th>{t('notifications.error')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((row) => (
                <Table.Tr key={row.id}>
                  {/* ⛔ Лише `<Timestamp>`: сирий ISO у комірці — це
                      `2026-09-20T08:15:42.1234567Z` там, де людині потрібні
                      два числа з дев'яти. Точність не губиться — вона
                      лишається в `dateTime`/`title` (`D15-09`). */}
                  <Table.Td>
                    <Timestamp value={row.at} />
                  </Table.Td>

                  {/* ⚠ Вид події — з каталогу, а не кодом сервера:
                      `JobFailed` це член `enum`, він не перекладається. */}
                  <Table.Td>{t(`notifications.event.${row.eventKind}`)}</Table.Td>

                  <Table.Td>{channelCell(row.channelName, row.channelId)}</Table.Td>

                  {/* ⛔ Єдине місце, де стан стає видимим, — набір. Власна
                      трійка кольорів тут була б сімнадцятою за ліком і
                      розійшлася б з рештою мовчки (директива №15 §2). */}
                  <Table.Td>
                    <StatusBadge kind="notificationDelivery" state={row.status} />
                  </Table.Td>

                  {/* ⚠ Прочерк, а не порожнеча: порожня комірка читається
                      двояко — «причини немає» чи «не завантажилось». */}
                  <Table.Td>{row.error ?? '—'}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>

          {/* ⚠ Кнопка живе рівно доти, доки сервер віддає курсор. Кнопка
              «показати ще», яка нічого не дочитує, обіцяє дані, яких немає. */}
          {pages.hasNextPage && (
            <Group justify="center" mt="md">
              <Button
                size="xs"
                variant="default"
                loading={pages.isFetchingNextPage}
                onClick={() => void pages.fetchNextPage()}
              >
                {t('notifications.showMore')}
              </Button>
            </Group>
          )}
        </>
      );
  }

  return (
    <Stack gap="sm" data-deliveries-panel="">
      {/* ⚠ Підпис блоку — НЕ заголовок (`<Title>`/`<h*>`): рівень тут задала
          б розмітка блоку, а не будова сторінки, і будь-яке її перекомпонування
          ламало б `heading-order`. Жирний текст несе те саме, нічого не
          обіцяючи навігації по заголовках. */}
      <Text fw={600}>{t('notifications.deliveries')}</Text>
      {body}
    </Stack>
  );
}

/**
 * Канал у комірці.
 *
 * ⛔ `channelName === null` — це ФАКТ, а не збій: канал видалили, а журнал
 * лишився (`deleteNotificationChannel`: «видаляє канал разом із його
 * правилами; журнал доставок лишається»). Тому прочерку тут бути не може —
 * доставка ж була, і саме в цей канал.
 *
 * ⛔ Ідентифікатор показується `<code>`, а не текстом, і це не про шрифт.
 * Гола `17` у стовпці «Канал» читається як назва — поруч із «Пошта
 * бухгалтерії» вона виглядає таким самим значенням того самого роду.
 * Моноширинна рамка каже, що це машинний ключ, а `title` — чому назви немає.
 */
function channelCell(channelName: string | null, channelId: number): ReactNode {
  if (channelName !== null) return channelName;

  return <Code title={t('notifications.channelGone')}>{channelId}</Code>;
}
