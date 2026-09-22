import { useEffect, useState, type JSX, type ReactNode } from 'react';
import {
  Badge,
  Button,
  Card,
  Checkbox,
  Divider,
  Group,
  NumberInput,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
  useMantineColorScheme,
} from '@mantine/core';
import { EcrApiError } from '@/api/client';
import { cellStateClass, type CellStateName } from '@/features/grid/cellState';
import { AsyncBoundary, ErrorState, ForbiddenState } from '@/shared/ui/AsyncBoundary';
import { Banner, ResultBanner } from '@/shared/ui/Banner';
import { CodeText } from '@/shared/ui/CodeText';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { DataTable } from '@/shared/ui/DataTable';
import { DetailDrawer, useDetailPanel } from '@/shared/ui/DetailDrawer';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { FilterBar } from '@/shared/ui/FilterBar';
import { Hint } from '@/shared/ui/Hint';
import { KeyValue } from '@/shared/ui/KeyValue';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { StatStrip } from '@/shared/ui/StatStrip';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { TwoLine } from '@/shared/ui/TwoLine';
import { Wizard } from '@/shared/ui/Wizard';
import { applyDensity, density, setDensity, type Density } from '@/shared/theme/preferences';
import { cellState } from '@/shared/theme/theme';
import { loadCatalog, preferredLanguage } from '@/shared/i18n';
import { useCatalog } from '@/shared/i18n/useCatalog';

/**
 * Каталог компонентів (`ЕТАП 7`, модуль 7.8; розширено `UI-10`).
 *
 * ⛔ Це **не** Storybook і не документація. Це один екран, який рендерить усе
 * поруч, і сенс рівно в цьому: п'ять відтінків сірого, що розповзлися по
 * п'ятнадцяти областях, видно **лише поруч**. Коли компоненти на різних
 * екранах, розбіжність не помітна нікому, включно з тим, хто її створив.
 *
 * ⚠ Сторінка також є єдиним місцем, де перевіряються стани комірок **у
 * градаціях сірого**: перемикач знеколірення нижче застосовує фільтр до
 * зразків. Якщо стани при цьому зливаються — `ФВ-14.18` не виконана, хоч би
 * що казав `axe-core`.
 *
 * ⛔ Маршрут доступний **лише в режимі розробки** (`import.meta.env.DEV`) і
 * поза `AppLayout`: інакше він потребував би входу, тобто був би недоступний
 * саме тоді, коли потрібен — при налаштуванні вигляду.
 *
 * ✎ `UI-10` (директива №15 §3, таблиця §3, крок `UI-10`): «Прибирання:
 * `KitchenSinkPage` → галерея набору». Додано секції на компоненти шару 1–2,
 * яких на сторінці ще не було: `Banner`/`ResultBanner`, `CodeText`,
 * `ConfirmModal`, `ReasonModal`, `DetailDrawer`, `ErrorAlert` (окремо від
 * `AsyncBoundary`), `Hint`, `KeyValue`, `TwoLine`, `StatusBadge`, а також два
 * стани `AsyncBoundary`, яких бракувало: `ErrorState` із копіюванням
 * кореляції (лінивий `CorrelationCopy`) і `ForbiddenState` (403, «немає
 * права»). Шапку сторінки розширено пропами `badge`/`count`/`meta`/`back` і
 * дій, щоб на ній же спрацював лінивий `PageHeaderActions`.
 *
 * ⛔ Свідомо НЕ додано: `LanguageSwitcher`, `UserMenu`, `LocalizedInput` —
 * усі три тягнуть `GET /api/v1/languages`, а ця сторінка (і її a11y-тест)
 * мережу не заглушує; `BrandMark` — суто декоративний SVG без стану,
 * показаний у шапці застосунку; `UnsavedGuard` — синглтон на весь застосунок
 * (`AppLayout`), що читає модульне сховище `unsavedSources`, а не пропи;
 * другого екземпляра в галереї він не потребує. `ListPage` лишається поза
 * галереєю з причини, уже названої нижче в розділі «Набір, шар 3»: вкладений
 * шаблон дав би `h3` усередині `h4` і зламав би `heading-order`.
 */
/** Рядок зразкового переліку для шару 3. */
interface KitRow {
  readonly code: string;
  readonly name: string;
  readonly cells: number;
  readonly changedAt: string;
}

/** Дані майстра-зразка. */
interface KitDraft {
  readonly name: string;
}

/*
 * ⚠ Мить у зразку — СТАЛА, а не `new Date()`. Знімок екрана з поточним часом
 * відрізнявся б від попереднього щопрогону, і `screenshots.spec.ts` показував
 * би різницю там, де нічого не змінилося.
 */
const KitRows: readonly KitRow[] = [
  { code: 'ECR-2026-001', name: 'Викиди, цех 1', cells: 169440, changedAt: '2026-09-18T09:15:00Z' },
  { code: 'ECR-2026-002', name: 'Викиди, цех 2', cells: 84720, changedAt: '2026-09-17T16:40:00Z' },
];

/**
 * Зразкова відмова сервера — та сама форма, що показує `AsyncBoundary`,
 * `ErrorState` і `ErrorAlert`. Використовується у двох картках «Стани
 * подання» (звичайна помилка й помилка з копіюванням кореляції), щоб код і
 * текст помилки не розходились між ними.
 *
 * ⚠ `errorCode: 'HTTP-409'` навмисно НЕ з каталогу — родини `PER` не існує,
 * код вигадали для показу. `HTTP-409` — форма, яку `client.ts` породжує сам
 * у `problemOf`, коли відповідь не є `problem+json`; вона очевидно не з
 * каталогу, і показувати їй є що: сторінка демонструє ВИГЛЯД компонента, а
 * не сценарій.
 */
function sampleRefusal(correlationId: string): EcrApiError {
  return new EcrApiError({
    title: 'Період закрито',
    detail: 'Період закрито: зміни потребують окремого погодження.',
    status: 409,
    errorCode: 'HTTP-409',
    correlationId,
  });
}

export function KitchenSinkPage(): JSX.Element {
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const [rows, setRows] = useState<Density>(density);
  const [grey, setGrey] = useState(false);
  const [stat, setStat] = useState<string | null>(null);
  const [wizard, setWizard] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [reasonOpen, setReasonOpen] = useState(false);
  const [, setPanel] = useDetailPanel();

  // ⚠ Каталог тягнеться і тут: сторінка живе поза AppLayout, тобто рядків їй
  // ніхто не завантажить. Без цього зразок стану помилки показував би
  // state.errorTitle — тобто демонстрував би дефект A7-33 замість вимоги.
  useCatalog();
  useEffect(() => {
    void loadCatalog(preferredLanguage(), 'public');
  }, []);

  function changeDensity(value: Density): void {
    setRows(value);
    setDensity(value);
    applyDensity(value);
  }

  return (
    <Stack gap="lg" p="md" style={grey ? { filter: 'grayscale(1)' } : undefined}>
      <PageHeader
        title="Каталог компонентів"
        badge={<StatusBadge kind="job" state="Running" />}
        count={17}
        meta="Ця шапка навмисно показує всі пропи PageHeader/PageHeaderActions разом: badge, count, meta, back, primary, secondary (переливається в меню при третьому) і more."
        back={{ label: 'До застосунку', href: '/' }}
        primary={{ label: 'Головна дія', onClick: () => {} }}
        secondary={[
          { label: 'Другорядна 1', onClick: () => {} },
          { label: 'Другорядна 2', onClick: () => {} },
          { label: 'Другорядна 3', onClick: () => {} },
        ]}
        more={[{ label: 'Лише в меню', onClick: () => {} }]}
      />

      <Group gap="lg" align="flex-end">
        <div>
          <Text size="xs" c="dimmed" mb="xs">
            Тема
          </Text>
          <SegmentedControl
            size="xs"
            value={colorScheme}
            onChange={(v) => setColorScheme(v as 'light' | 'dark' | 'auto')}
            data={[
              { value: 'auto', label: 'Системна' },
              { value: 'light', label: 'Світла' },
              { value: 'dark', label: 'Темна' },
            ]}
          />
        </div>

        <div>
          <Text size="xs" c="dimmed" mb="xs">
            Щільність
          </Text>
          <SegmentedControl
            size="xs"
            value={rows}
            onChange={(v) => changeDensity(v as Density)}
            data={[
              { value: 'compact', label: 'Щільна' },
              { value: 'comfortable', label: 'Вільна' },
            ]}
          />
        </div>

        {/*
         * ⛔ Головна перевірка сторінки. Знеколірення — це не демонстрація, а
         * єдиний спосіб довести, що колір не є єдиним носієм: при `grayscale(1)`
         * від кольору не лишається нічого, і розрізняти стани мусить сама форма.
         */}
        <Switch
          checked={grey}
          onChange={(event) => setGrey(event.currentTarget.checked)}
          label="Градації сірого"
        />
      </Group>

      <Divider />

      {/*
       * ⛔ Стенд для ВИМІРЮВАННЯ, не для показу (`D-140`). Три умови, без яких
       * піксельний вимір бреше:
       *   1. шість комірок — п'ять станів плюс звичайна;
       *   2. ОДНАКОВИЙ вміст у всіх шести, інакше різниця вимірює текст;
       *   3. однакова геометрія, фіксований розмір.
       *
       * ⚠ Він не сховається за `display: none`: прихований елемент не
       * рендериться, і знімати з нього нема чого. Тому просто малий і зверху.
       */}
      <div data-measure="cell-states">
        {[...(Object.keys(cellState) as CellStateName[]), null].map((name) => (
          <div
            key={name ?? 'normal'}
            data-measure-cell={name ?? 'normal'}
            className={name === null ? '' : cellStateClass(name)}
            style={{ width: 100, height: 24, boxSizing: 'border-box', overflow: 'hidden' }}
          >
            1 234,56
          </div>
        ))}
      </div>

      <Section title="Стани комірки (ФВ-14.18, D-128)">
        <Text size="sm" c="dimmed">
          П'ять станів. Увімкніть «градації сірого»: якщо стани перестали
          розрізнятися — вимога не виконана.
        </Text>

        <Table withTableBorder withColumnBorders className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Стан</Table.Th>
              <Table.Th>Зразок</Table.Th>
              <Table.Th>Другий носій</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(Object.keys(cellState) as CellStateName[]).map((name) => (
              <Table.Tr key={name}>
                <Table.Td>
                  <Text size="sm" ff="monospace">
                    {name}
                  </Text>
                </Table.Td>
                <Table.Td
                  className={cellStateClass(name)}
                  data-cell-state={name}
                  style={{ minWidth: 120 }}
                >
                  1 234,56
                </Table.Td>
                <Table.Td>
                  <Text size="sm">{cellState[name].marker}</Text>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Section>

      <Section title="Стани подання (ФВ-14.21…14.25)">
        <Text size="sm" c="dimmed">
          П'ять станів `AsyncBoundary` поруч: завантаження, порожньо, помилка,
          дані, немає права. Порожньо і помилка не мають права виглядати
          однаково (`ФВ-14.22`, `A7-04`).
        </Text>

        <Group align="stretch" grow>
          <Card withBorder>
            <Text size="xs" c="dimmed" mb="xs">
              Завантаження
            </Text>
            <AsyncBoundary isPending error={null} data={undefined} skeleton="table">
              {() => <div />}
            </AsyncBoundary>
          </Card>

          <Card withBorder>
            <Text size="xs" c="dimmed" mb="xs">
              Порожньо
            </Text>
            <AsyncBoundary<string[]>
              isPending={false}
              error={null}
              data={[]}
              isEmpty={(d) => d.length === 0}
              emptyTitle="У цьому проєкті ще немає документів"
              emptyHint="Документи з'являються після відкриття періоду."
              emptyAction={<Button size="xs">Створити документ</Button>}
            >
              {() => <div />}
            </AsyncBoundary>
          </Card>
        </Group>

        <Group align="stretch" grow>
          <Card withBorder>
            <Text size="xs" c="dimmed" mb="xs">
              Помилка
            </Text>
            <AsyncBoundary
              isPending={false}
              error={sampleRefusal('cid-demo-1')}
              data={undefined}
              onRetry={() => {}}
            >
              {() => <div />}
            </AsyncBoundary>
          </Card>

          <Card withBorder>
            <Text size="xs" c="dimmed" mb="xs">
              Дані
            </Text>
            <AsyncBoundary<string[]>
              isPending={false}
              error={null}
              data={['ECR-2026-001', 'ECR-2026-002']}
              isEmpty={(d) => d.length === 0}
            >
              {(d) => (
                <Stack gap="xs">
                  {d.map((key) => (
                    <Text key={key} size="sm">
                      {key}
                    </Text>
                  ))}
                </Stack>
              )}
            </AsyncBoundary>
          </Card>
        </Group>

        <Group align="stretch" grow>
          <Card withBorder>
            <Text size="xs" c="dimmed" mb="xs">
              Помилка з копіюванням кореляції (`ErrorState`, лінивий `CorrelationCopy`)
            </Text>
            <ErrorState
              error={sampleRefusal('cid-demo-copy')}
              onRetry={() => {}}
              copyLabel="Скопіювати кореляцію"
            />
          </Card>

          <Card withBorder>
            <Text size="xs" c="dimmed" mb="xs">
              Немає права (`ForbiddenState`, 403)
            </Text>
            <ForbiddenState
              error={
                new EcrApiError({
                  title: 'Розділ недоступний',
                  detail: 'Розділ недоступний: потрібне право Security.ManageUsers.',
                  status: 403,
                  // ⚠ Реальний код каталогу (`ErrorCodes.Forbidden`,
                  // `src/Ecr.Domain/Errors/ErrorCodes.cs:30`) — той самий,
                  // що показує гард маршруту (`err.ECR-AUTH-0403`,
                  // `AccessDeniedPage.tsx`, `Q-279`). На відміну від
                  // `sampleRefusal`/`ErrorAlert` нижче, тут НЕ вигадка: 403
                  // від сервера завжди приходить саме цим кодом.
                  errorCode: 'ECR-AUTH-0403',
                  correlationId: 'cid-demo-403',
                })
              }
            />
          </Card>
        </Group>
      </Section>

      <Section title="Керування">
        <Group align="flex-end">
          <Button>Основна</Button>
          <Button variant="default">Звичайна</Button>
          <Button variant="subtle">Тиха</Button>
          <Button color="statusError">Небезпечна</Button>
          <Button loading>Триває</Button>
          <Button disabled>Недоступна</Button>
        </Group>

        <Group align="flex-end">
          <TextInput label="Текст" placeholder="—" />
          <NumberInput label="Число" decimalScale={2} />
          <Select label="Вибір" data={['Перше', 'Друге']} />
          <Checkbox label="Прапорець" />
        </Group>

        <Group>
          <Badge>Звичайний</Badge>
          <Badge color="statusWarning" variant="filled">
            Симуляція
          </Badge>
          <Badge color="statusError">Помилка</Badge>
        </Group>
      </Section>

      <Section title="Текст і код (TwoLine, CodeText, KeyValue)">
        <Text size="sm" c="dimmed">
          Людський підпис із кодом під ним, моноширинний код і перелік «підпис
          → значення». Порожні пари й порожній код не малюються (`D15-06`) —
          третій рядок нижче цю пару взагалі не показує.
        </Text>

        <Group align="flex-start" grow>
          <TwoLine primary="Викиди, цех 1" secondary="ECR-2026-001" mono />
          <CodeText>Document.Approve</CodeText>
        </Group>

        <CodeText block>{'IF([Cells].[CO2] > 0, [Cells].[CO2] * 1.05, 0)'}</CodeText>

        <KeyValue
          items={[
            { label: 'Код', value: 'ECR-2026-001', mono: true },
            { label: 'Комірок', value: 169440 },
            { label: 'Порожнє поле — рядка не буде', value: '' },
            {
              label: 'Змінено',
              value: <Timestamp value="2026-09-18T09:15:00Z" />,
              hint: 'Часовий пояс — локальний браузера',
            },
          ]}
        />
      </Section>

      <Section title="Підказка (Hint)">
        <Text size="sm" c="dimmed">
          Відкривається наведенням і фокусом, закривається `Escape`; опис
          доступний читалці й без відкриття (`aria-describedby`).
        </Text>

        <Group>
          <Hint label="Ідентифікатор запиту — для звернення в підтримку" focusable>
            <Badge variant="default">cid-demo-1</Badge>
          </Hint>

          <Hint label="Роз'яснення другорядної дії">
            <Button variant="subtle" size="xs">
              Довідка
            </Button>
          </Hint>
        </Group>
      </Section>

      <Section title="Статус (StatusBadge)">
        <Text size="sm" c="dimmed">
          Тон і підпис разом — другий носій змісту (`ФВ-14.18`); `quiet` —
          без заливки, для щільних таблиць.
        </Text>

        {/*
         * ⛔ Невідомий стан (`UnknownStateTone`, `StatusBadge.tsx`) тут
         * НАВМИСНО не показаний. Перша редакція малювала
         * `<StatusBadge kind="job" state="Superseded" />` саме заради цього
         * випадку — і впала на іншому гейті: `accessibility.part4.a11y.test.tsx`
         * («Технічні ключі на екрані», `ФВ-14.9`) сканує КОЖЕН маршрут,
         * включно з цим, на позначені ключі (`⟦…⟧`, `D-138`) і саме такий
         * ключ і знайшов. Дві вимоги зіткнулися по суті: ключ, що доводить
         * «невідомий стан не мовчить», для ЦЬОГО сторожа непроминучий ЯК
         * ключ. Рішення — не гасити жоден із гейтів, а показати поведінку
         * без бренду каталогу: сам компонент і тест `StatusBadge.test.tsx`
         * (`isKnownStatus`, `data-status-known="false"`) уже доводять її без
         * видимого тексту ключа на екрані.
         */}
        <Group>
          <StatusBadge kind="sheet" state="Draft" />
          <StatusBadge kind="sheet" state="Submitted" />
          <StatusBadge kind="sheet" state="Rejected" />
          <StatusBadge kind="job" state="Running" />
          <StatusBadge kind="job" state="Failed" quiet />
          <StatusBadge kind="period" state="Grace" />
        </Group>
      </Section>

      <Section title="Повідомлення (Banner, ResultBanner)">
        <Text size="sm" c="dimmed">
          Тон визначає РОЛЬ живої області, не лише колір: `info`/`success` —
          `status` (ввічливо), `warning`/`danger` — `alert` (негайно).
        </Text>

        <Stack gap="xs">
          <Banner
            tone="info"
            title="Період очікує відкриття"
            text="Дані з'являться після відкриття періоду."
          />
          <Banner
            tone="warning"
            title="Перевірте формулу"
            text="Дільник може дорівнювати нулю на частині рядків."
            actions={[{ label: 'Відкрити редактор', onClick: () => {} }]}
          />
          <Banner
            tone="danger"
            title="Період закрито"
            text="Зміни потребують окремого погодження."
            dismiss={{ label: 'Закрити повідомлення', onDismiss: () => {} }}
          />
          <ResultBanner title="Збережено" text="Далі: надіслати на погодження." />
        </Stack>
      </Section>

      <Section title="Модальні вікна (ConfirmModal, ReasonModal)">
        <Text size="sm" c="dimmed">
          Фокус за замовчуванням — на БЕЗПЕЧНІЙ дії (`L6`): `Enter` не виконує
          незворотне. Кнопка `ReasonModal` вимкнена, доки причина коротша за
          поріг.
        </Text>

        <Group>
          <Button variant="default" onClick={() => setConfirmOpen(true)}>
            Відкрити підтвердження
          </Button>
          <Button variant="default" onClick={() => setReasonOpen(true)}>
            Відкрити причину
          </Button>
        </Group>

        <ConfirmModal
          opened={confirmOpen}
          title='Видалити роль "Нічна зміна"?'
          text="Учасники ролі негайно втратять доступ до застосунку."
          consequences={[
            'Права ролі буде відкликано в усіх учасників.',
            { text: 'Дію не можна скасувати.', note: true },
          ]}
          verb="Видалити роль"
          typeToConfirm={{ value: 'Нічна зміна', label: 'Введіть назву ролі, щоб підтвердити' }}
          onConfirm={() => setConfirmOpen(false)}
          onClose={() => setConfirmOpen(false)}
        />

        <ReasonModal
          opened={reasonOpen}
          title="Відхилити аркуш"
          label="Причина відхилення"
          description="Побачить автор аркуша."
          confirmLabel="Відхилити"
          minLength={5}
          onConfirm={() => setReasonOpen(false)}
          onClose={() => setReasonOpen(false)}
        />
      </Section>

      <Section title="Шторка подробиць (DetailDrawer)">
        <Text size="sm" c="dimmed">
          Стан живе в адресі (`?panel=`): шторку можна надіслати посиланням.
          На широкому екрані сторінка позаду лишається живою (без затемнення
          й пастки фокуса).
        </Text>

        <Button variant="default" onClick={() => setPanel('kit-demo')}>
          Відкрити шторку
        </Button>

        <DetailDrawer
          panelId="kit-demo"
          title="ECR-2026-001"
          subtitle="Викиди, цех 1"
          badge={<StatusBadge kind="sheet" state="Draft" />}
          closeLabel="Закрити шторку"
          footer={
            <Button variant="default" onClick={() => setPanel(null)}>
              Закрити
            </Button>
          }
        >
          <KeyValue
            items={[
              { label: 'Код', value: 'ECR-2026-001', mono: true },
              { label: 'Комірок', value: 169440 },
            ]}
          />
        </DetailDrawer>
      </Section>

      <Section title="ErrorAlert (окремо, поза AsyncBoundary — напр. у формі)">
        <Text size="sm" c="dimmed">
          Той самий компонент, що й у межі станів вище: код і кореляція
          показуються завжди, кнопка «повторити» — лише коли є що повторювати.
        </Text>

        <ErrorAlert
          error={
            new EcrApiError({
              title: 'Помилка валідації',
              detail: 'Поле «Назва» обов’язкове.',
              status: 422,
              // ⚠ `HTTP-422`, НЕ `ECR-VAL-0422` — та сама причина, що й у
              // `sampleRefusal` вище: родини `VAL` у каталозі домену немає,
              // а `ClientErrorCodeTests.Клієнт_не_згадує_кодів_яких_немає_в_каталозі`
              // (`tests/Ecr.Architecture.Tests`) читає ВЕСЬ `src/Ecr.Web/src`,
              // включно з цією сторінкою, і саме так це вже ловилося раз
              // (`ECR-PER-0409`, коментар `ClientErrorCodeTests.cs`). Форма
              // `HTTP-<статус>` — та, яку `client.ts` породжує сам у
              // `problemOf`, коли відповідь не є `problem+json`.
              errorCode: 'HTTP-422',
              correlationId: 'cid-demo-422',
            })
          }
          onRetry={() => {}}
        />
      </Section>

      <Section title="Набір, шар 3 (директива №15 §2)">
        <Text size="sm" c="dimmed">
          Чотири компоненти, з яких збираються переліки. Увімкніть «градації
          сірого»: тон показника-проблеми має лишитися розрізненним, бо його
          несе не лише колір.
        </Text>

        <StatStrip
          label="Показники переліку"
          items={[
            { id: 'all', label: 'усього', value: 128 },
            { id: 'running', label: 'виконуються', value: 3 },
            { id: 'failed', label: 'помилок', value: 7, tone: 'danger' },
            { id: 'ok', label: 'помилок валідації', value: 0, tone: 'danger' },
          ]}
          active={stat}
          onSelect={setStat}
        />

        {/*
         * ⚠ Четвертий показник має `tone: 'danger'` і значення НУЛЬ — і саме
         * тому він нейтральний. Це `L3` на екрані: «0 помилок» — це «все
         * гаразд», і фарбувати його червоним означало б знецінити червоний там,
         * де він потрібен.
         */}
        <FilterBar
          search={{ label: 'Пошук', param: 'ks-q', placeholder: 'код або назва' }}
          filters={[
            {
              id: 'ks-state',
              label: 'Стан',
              options: [
                { value: 'Draft', label: 'Чернетка' },
                { value: 'Submitted', label: 'Подано' },
              ],
            },
          ]}
        />

        <DataTable<KitRow>
          columns={[
            { key: 'code', label: 'Код', mono: true, minWidth: 140 },
            { key: 'name', label: 'Назва', minWidth: 200 },
            { key: 'cells', label: 'Комірок', num: true },
            {
              key: 'changedAt',
              label: 'Змінено',
              render: (row) => <Timestamp value={row.changedAt} />,
            },
          ]}
          rows={KitRows}
          rowKey={(row) => row.code}
          caption="Зразок переліку: чотири колонки з семи дозволених (L5)"
        />

        <Group>
          <Button variant="default" onClick={() => setWizard(true)}>
            Відкрити майстер
          </Button>
          <Text size="xs" c="dimmed">
            Майстер закритий за замовчуванням — `L2`.
          </Text>
        </Group>

        <Wizard<KitDraft>
          opened={wizard}
          title="Publish version 3"
          initialData={{ name: '' }}
          applyLabel="Publish version"
          labels={{ back: 'Назад', next: 'Далі', review: 'Перевірка' }}
          summary={(data) => (data.name === '' ? null : <Text size="sm">{data.name}</Text>)}
          onClose={() => setWizard(false)}
          onApply={() => setWizard(false)}
          onExitUnsaved={() => true}
          steps={[
            {
              id: 'name',
              label: 'Назва',
              validate: (data) =>
                data.name.trim() === ''
                  ? { message: 'Без назви публікувати нічого', focus: 'input' }
                  : null,
              render: ({ data, update }) => (
                <TextInput
                  label="Назва"
                  value={data.name}
                  onChange={(event) => update({ name: event.currentTarget.value })}
                />
              ),
            },
          ]}
        />

        {/*
         * ⛔ `ListPage` у каталозі НЕ показується, і це не пропуск. Шаблон малює
         * `PageHeader`, тобто `<Title order={3}>`, а кожен розділ цієї сторінки
         * — `<Title order={4}>`. Вкладений шаблон дав би h3 усередині h4, тобто
         * `heading-order` у `axe` — і гейт `a11y` почервонів би на сторінці, яка
         * саме доступність і перевіряє.
         *
         * ⚠ Але це не «не влізло»: сама неможливість вкласти шаблон у чужу
         * сторінку і є властивістю, яку він гарантує — рівно одна шапка на
         * маршрут. Його місце — маршрут, і перший такий маршрут названо в
         * Next steps PR.
         */}
      </Section>

      <Section title="Типографіка (ФВ-14.13)">
        <Title order={2}>Заголовок h2</Title>
        <Title order={3}>Заголовок h3</Title>
        <Text size="md">md 15px — форми</Text>
        <Text size="sm">sm 13px — основний</Text>
        <Text size="xs">xs 11px — щільні таблиці</Text>
      </Section>

      <Section title="Довгий рядок (ФВ-14.30)">
        <Text size="sm" c="dimmed">
          Казахська й російська на 20–40 % довші за англійську; розмітка не має
          ламатися.
        </Text>

        <Group grow maw={420}>
          <Button>
            <span className="ecr-ellipsis">
              Қоршаған ортаны қорғау жөніндегі есептілікті бекіту
            </span>
          </Button>
        </Group>
      </Section>
    </Stack>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }): JSX.Element {
  return (
    <Stack gap="sm">
      <Title order={4}>{title}</Title>
      {children}
    </Stack>
  );
}
