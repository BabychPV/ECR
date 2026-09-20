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
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { DataTable } from '@/shared/ui/DataTable';
import { FilterBar } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatStrip } from '@/shared/ui/StatStrip';
import { Timestamp } from '@/shared/ui/Timestamp';
import { Wizard } from '@/shared/ui/Wizard';
import { applyDensity, density, setDensity, type Density } from '@/shared/theme/preferences';
import { cellState } from '@/shared/theme/theme';
import { loadCatalog, preferredLanguage } from '@/shared/i18n';
import { useCatalog } from '@/shared/i18n/useCatalog';

/**
 * Каталог компонентів (`ЕТАП 7`, модуль 7.8).
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

export function KitchenSinkPage(): JSX.Element {
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const [rows, setRows] = useState<Density>(density);
  const [grey, setGrey] = useState(false);
  const [stat, setStat] = useState<string | null>(null);
  const [wizard, setWizard] = useState(false);

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
      <PageHeader title="Каталог компонентів" />

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
          Чотири стани поруч. Порожньо і помилка не мають права виглядати
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
              error={
                new EcrApiError({
                  title: 'Період закрито',
                  detail: 'Період закрито: зміни потребують окремого погодження.',
                  status: 409,
                  // ⚠ Плейсхолдер навмисно НЕ з каталогу. Тут стояв
                  // `ECR-PER-0409` — родини `PER` не існує, код вигадали для
                  // показу. Формат `ECR-<ДОМЕН>-<HTTP>` — це маршрут: за
                  // родиною клієнт вирішує, у який обробник віддати відмову,
                  // тож вигаданий код у прикладі стає зразком, який копіюють.
                  //
                  // `HTTP-409` — форма, яку `client.ts` породжує сам у
                  // `problemOf`, коли відповідь не є `problem+json`. Вона
                  // очевидно не з каталогу, і показувати їй є що: сторінка
                  // демонструє ВИГЛЯД компонента, а не сценарій.
                  errorCode: 'HTTP-409',
                  correlationId: 'cid-demo-1',
                })
              }
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
