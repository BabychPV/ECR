import { useEffect, useState, type JSX } from 'react';
import { Button, Checkbox, Group, Select, Skeleton, Stack, Table, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  getNotificationRules,
  listNotificationChannels,
  replaceNotificationRules,
  type NotificationChannel,
  type NotificationRule,
  type NotificationRuleMatrix,
} from '@/features/notifications/api';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { statusKey } from '@/shared/ui/StatusBadge';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** Подія, про яку сповіщають; перелік приходить із сервера (`BE-33b`). */
type EventKind = NotificationRule['eventKind'];

/** Межа серйозності правила. */
type Severity = NotificationRule['minSeverity'];

/**
 * Варіанти межі серйозності.
 *
 * ⛔ Перелік виписаний тут, а не зібраний із наявних правил: клітинка, якої ще
 * немає, теж мусить пропонувати всі три. `satisfies` звіряє його з типом
 * сервера — зникне чи додасться значення, і файл не скомпілюється.
 */
const Severities = ['Info', 'Warning', 'Error'] as const satisfies readonly Severity[];

/**
 * Межа для щойно ввімкненої клітинки — `Info`, тобто «пропускати все».
 *
 * ⛔ Рішення назване, бо тихий дефолт тут коштує дорого. Прапорець означає
 * «повідомляти мене про цю подію»; будь-яка вища межа означала б, що людина
 * щойно попросила сповіщення — і не отримає його, доки подія не дотягне до
 * обраного рівня. Мовчання, якого не просили, гірше за зайвий лист:
 * звузити межу видно на екрані, а не-надіслане не видно ніде.
 */
const DefaultSeverity: Severity = 'Info';

/** Ключі кешу. Свого домену у фабриці `queryKeys` сповіщення ще не мають. */
const RulesKey = ['notifications', 'rules'] as const;
const ChannelsKey = ['notifications', 'channels'] as const;

/** Клітинка чернетки: чи діє правило і з якою межею. */
interface Cell {
  readonly isEnabled: boolean;
  readonly minSeverity: Severity;
}

/** Адреса клітинки в чернетці. */
function cellKey(eventKind: EventKind, channelId: number): string {
  return `${eventKind}:${String(channelId)}`;
}

/** Чернетка з відповіді сервера. */
function draftOf(rules: readonly NotificationRule[]): ReadonlyMap<string, Cell> {
  return new Map(
    rules.map((rule) => [
      cellKey(rule.eventKind, rule.channelId),
      { isEnabled: rule.isEnabled, minSeverity: rule.minSeverity },
    ]),
  );
}

/**
 * Матриця правил сповіщень «подія × канал» (`BE-33b`, екран
 * `/admin/notifications`).
 *
 * ⚠ Блок самодостатній: обидва запити він робить сам. Пропів немає навмисно —
 * інакше сторінка мусила б знати про дві адреси сервера й про порядок, у якому
 * їх можна показувати, тобто правило `L10` довелося б дотримувати ще й там.
 *
 * ⛔ **Порядок станів — відмова → очікування → дані, і він тут не формальність**
 * (директива №15 §0, правило `L10`: «немає прав» ≠ «порожньо» ≠ «нічого не
 * заведено»). `replaceNotificationRules` замінює матрицю ЦІЛКОМ: клітинка, якої
 * немає в тілі, зникає. Отже матриця, домальована з невідомого стану, — це не
 * «показали менше, ніж є», а кнопка, яка збереже ПОРОЖНЮ матрицю поверх
 * наявних правил, тобто мовчки вимкне всі сповіщення системи. Тому при відмові
 * будь-якого з двох запитів таблиці немає зовсім, а разом із нею немає й
 * кнопки збереження.
 *
 * ⛔ `AsyncBoundary` тут не взято навмисно: вона малює власний `<Title
 * order={4}>` і рве `heading-order` (гейти `a11y (dark)`/`a11y (light)`). Це та
 * сама причина, з якої її не взяв `DocumentPage` для `ValidationPanel`.
 *
 * ⚠ «Каналів нуль» — ТРЕТІЙ стан, не той самий, що відмова: вісь матриці
 * порожня, малювати нічого, і сказати про це треба словами, а не таблицею з
 * однією колонкою заголовків.
 */
export function RulesMatrixPanel(): JSX.Element {
  const queryClient = useQueryClient();

  const rules = useQuery<NotificationRuleMatrix>({ queryKey: RulesKey, queryFn: getNotificationRules });
  const channels = useQuery<NotificationChannel[]>({
    queryKey: ChannelsKey,
    queryFn: listNotificationChannels,
  });

  const [draft, setDraft] = useState<ReadonlyMap<string, Cell>>(new Map());

  /*
   * ⚠ Чернетка наповнюється ВІДПОВІДДЮ, а не заводиться раз. Після збереження
   * сюди приїжджає матриця, яку повернув сервер (див. `onSuccess`), тож на
   * екрані лишається його правда, а не наше уявлення про неї.
   */
  useEffect(() => {
    if (rules.data !== undefined) setDraft(draftOf(rules.data.rules));
  }, [rules.data]);

  const save = useMutation({
    mutationFn: (next: NotificationRule[]) => replaceNotificationRules(next),
    onSuccess: (saved: NotificationRuleMatrix) => {
      // ⚠ Відповідь PUT — це ВЖЕ вся матриця (`NotificationRuleMatrix`), тож
      // інвалідація з повторним GET була б зайвим запитом за тими самими
      // даними — і зайвою миттю, коли екран показує старе.
      queryClient.setQueryData(RulesKey, saved);
      showDone(t('notifications.rulesSaved'));
    },
    onError: (error: unknown) => {
      showApiError(error);
    },
  });

  // ⛔ Крок 1 — ВІДМОВА. Байдуже, який із двох запитів упав: без каналів немає
  // осі, без правил немає значень, і в обох випадках зібрана таблиця була б
  // вигадкою, яку кнопка збереження зробила б правдою.
  const failure = rules.error ?? channels.error;

  if (failure !== null) {
    return (
      <ErrorAlert
        error={failure}
        onRetry={() => {
          void rules.refetch();
          void channels.refetch();
        }}
      />
    );
  }

  /*
   * ⛔ Крок 2 — ОЧІКУВАННЯ. Порожня матриця тут читалася б як «правил немає»,
   * а насправді про них ще нічого не відомо.
   *
   * ⚠ `data === undefined` перевіряється поруч із `isPending`, хоча після
   * кроку 1 це вже мало б випливати. Типи TanStack цього не доводять (відмова
   * й успіх — різні гілки об'єднання, і звуження по ДВОХ різних запитах разом
   * до `data` не доходить), а `!` на цьому місці означав би падіння всього
   * рендера рівно в тому випадку, який ми не передбачили.
   */
  if (
    rules.isPending ||
    channels.isPending ||
    rules.data === undefined ||
    channels.data === undefined
  ) {
    return (
      <Stack gap="xs" aria-busy="true">
        <Skeleton height={28} />
        <Skeleton height={28} />
        <Skeleton height={28} />
      </Stack>
    );
  }

  const channelList = channels.data;
  const matrix = rules.data;

  // ⛔ Крок 3 — ПОРОЖНЬО, і це успішна відповідь, а не збій: каналів справді
  // жодного. Вісь матриці порожня, тож таблиці немає — але причина названа
  // словами, а не відсутністю колонок.
  if (channelList.length === 0) {
    return (
      <Stack gap="xs">
        <Text>{t('notifications.noChannels')}</Text>
        <Text size="sm" c="dimmed">
          {t('notifications.noChannelsHint')}
        </Text>
      </Stack>
    );
  }

  /**
   * Тіло запиту: УСІ ввімкнені клітинки, у порядку екрана.
   *
   * ⛔ Вимкнена клітинка з тіла ПРИБИРАЄТЬСЯ, а не їде з `isEnabled: false`.
   * Сервер замінює матрицю цілком, тобто «клітинки немає» вже означає «правило
   * не діє» — а рядок із `isEnabled: false` сказав би те саме вдруге й іншими
   * словами. Два способи записати одну й ту саму правду розходяться: після
   * збереження сервер віддав би те, що ми надіслали, і наступний читач мусив
   * би знати, що порожньо й вимкнено — це одне. Ціна рішення названа: межа
   * серйозності вимкненої клітинки не зберігається, і повторне ввімкнення
   * починає з {@link DefaultSeverity}.
   *
   * ⚠ Канал, якого вже немає у відповіді `/channels`, у тіло не потрапляє: вісь
   * матриці — чинний перелік каналів, а не те, на що колись посилалися правила.
   */
  function enabledRules(): NotificationRule[] {
    const next: NotificationRule[] = [];

    for (const eventKind of matrix.eventKinds) {
      for (const channel of channelList) {
        const cell = draft.get(cellKey(eventKind, channel.id));

        if (cell === undefined || !cell.isEnabled) continue;

        next.push({
          channelId: channel.id,
          eventKind,
          isEnabled: true,
          minSeverity: cell.minSeverity,
        });
      }
    }

    return next;
  }

  function setCell(eventKind: EventKind, channelId: number, cell: Cell): void {
    setDraft((previous) => new Map(previous).set(cellKey(eventKind, channelId), cell));
  }

  return (
    <Stack gap="sm">
      {/*
       * ⚠ Ім'я таблиці — `aria-label`, а не власний заголовок: окремий
       * `<Title>` усередині блоку рве `heading-order` рівно так само, як його
       * рве `AsyncBoundary`, і саме через це її тут немає. Заголовок екрана
       * ставить сторінка.
       */}
      <Table striped withTableBorder aria-label={t('notifications.rules')} className="ecr-sticky-head">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('notifications.event')}</Table.Th>
            {channelList.map((channel) => (
              <Table.Th key={channel.id}>{channel.name}</Table.Th>
            ))}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {/*
           * ⛔ Рядки — `eventKinds` СЕРВЕРА, а не події, на які правило вже є.
           * Матриця з самих лише заповнених рядків не давала б завести перше
           * правило на подію: «правила немає» виглядало б як «події не існує»
           * (саме це й сказано в контракті `NotificationRuleMatrix`).
           */}
          {matrix.eventKinds.map((eventKind) => {
            const eventLabel = t(`notifications.event.${eventKind}`);

            return (
              <Table.Tr key={eventKind}>
                <Table.Th scope="row">{eventLabel}</Table.Th>

                {channelList.map((channel) => {
                  const cell = draft.get(cellKey(eventKind, channel.id)) ?? {
                    isEnabled: false,
                    minSeverity: DefaultSeverity,
                  };

                  return (
                    <Table.Td key={channel.id}>
                      <Group gap="xs" wrap="nowrap">
                        {/*
                         * ⚠ Підпис — `aria-label`, і він називає ОБИДВІ
                         * координати клітинки. Читалка не пов'язує `<th>`
                         * рядка й стовпця з полем усередині `<td>`: без імені
                         * користувач чує «прапорець» стільки разів, скільки в
                         * матриці клітинок, і жодного разу не дізнається, який
                         * із них який.
                         */}
                        <Checkbox
                          size="xs"
                          aria-label={`${eventLabel} · ${channel.name}`}
                          checked={cell.isEnabled}
                          onChange={(event) =>
                            setCell(eventKind, channel.id, {
                              ...cell,
                              isEnabled: event.currentTarget.checked,
                            })
                          }
                        />

                        {/*
                         * ⛔ Підписи варіантів — `t(statusKey('severity', …))`,
                         * а не самі коди: `Info`/`Warning`/`Error` — члени
                         * переліку сервера, вони не перекладаються й не несуть
                         * тону (директива №15 §2, той самий ключ, яким малює
                         * `StatusBadge`).
                         *
                         * ⚠ Межа недоступна, доки клітинка вимкнена: правило,
                         * якого не буде в матриці, не має межі — а поле, що
                         * приймає значення й мовчки його викидає, обіцяє
                         * більше, ніж робить.
                         */}
                        <Select
                          size="xs"
                          miw={120}
                          aria-label={`${t('notifications.minSeverity')} · ${eventLabel} · ${channel.name}`}
                          data={Severities.map((value) => ({
                            value,
                            label: t(statusKey('severity', value)),
                          }))}
                          value={cell.minSeverity}
                          disabled={!cell.isEnabled}
                          allowDeselect={false}
                          onChange={(value) => {
                            if (value === null) return;

                            setCell(eventKind, channel.id, {
                              ...cell,
                              minSeverity: value as Severity,
                            });
                          }}
                        />
                      </Group>
                    </Table.Td>
                  );
                })}
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>

      <Group gap="xs">
        <Button loading={save.isPending} onClick={() => save.mutate(enabledRules())}>
          {t('notifications.saveRules')}
        </Button>
      </Group>
    </Stack>
  );
}
