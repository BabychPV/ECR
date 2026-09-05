# 05i — Скелет: `Ecr.Web` (React SPA)

> Частина [`05-skeleton.md`](05-skeleton.md).
> Обсяг і області — [`B21`](../reference/backend/B21-frontend-spec.md), з
> поправкою `D-52`: **конструктор звітів і веб-переглядач звітів поза обсягом**,
> областей 15, а не 16.
>
> ⚠ **Тут очікуй найбільше `BOOTSTRAP-FIX`.** npm-екосистема дрейфує швидше за
> NuGet, і версії з [`04-environment.md`](04-environment.md) §4 — гіпотеза.
> Правило те саме: мажорну лінію не міняти, мінорну — вільно, кожну зміну
> записати.

---

### `src/Ecr.Web/package.json`
MODULE: web | STAGE: 0

```json
{
  "name": "ecr-web",
  "private": true,
  "version": "0.1.0",
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc -b && vite build",
    "preview": "vite preview",
    "typecheck": "tsc --noEmit",
    "test": "vitest run",
    "test:watch": "vitest",
    "lint": "eslint src --ext .ts,.tsx",
    "api:types": "openapi-typescript http://localhost:5080/openapi/v1.json -o src/api/schema.d.ts"
  },
  "dependencies": {
    "react": "19.0.0",
    "react-dom": "19.0.0",
    "react-router-dom": "7.18.3",
    "@mantine/core": "7.15.2",
    "@mantine/hooks": "7.15.2",
    "@mantine/dates": "7.15.2",
    "@mantine/form": "7.15.2",
    "@mantine/notifications": "7.15.2",
    "@revolist/react-datagrid": "4.11.0",
    "@tanstack/react-query": "5.62.11",
    "zustand": "5.0.2",
    "react-hook-form": "7.54.2",
    "zod": "3.24.1",
    "@formulajs/formulajs": "4.4.9",
    "dayjs": "1.11.13"
  },
  "devDependencies": {
    "typescript": "5.7.2",
    "vite": "6.4.3",
    "@vitejs/plugin-react": "4.3.4",
    "@types/react": "19.0.2",
    "@types/react-dom": "19.0.2",
    "vitest": "2.1.9",
    "@testing-library/react": "16.1.0",
    "@testing-library/user-event": "14.5.2",
    "jsdom": "25.0.1",
    "openapi-typescript": "7.5.0",
    "eslint": "9.39.5",
    "@typescript-eslint/eslint-plugin": "8.18.2",
    "@typescript-eslint/parser": "8.18.2"
  }
}
```

---

### `src/Ecr.Web/tsconfig.json`
MODULE: web | STAGE: 0

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "jsx": "react-jsx",
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "noImplicitOverride": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "exactOptionalPropertyTypes": true,
    "skipLibCheck": true,
    "esModuleInterop": true,
    "resolveJsonModule": true,
    "isolatedModules": true,
    "verbatimModuleSyntax": true,
    "noEmit": true,
    "baseUrl": ".",
    "paths": { "@/*": ["./src/*"] }
  },
  "include": ["src"]
}
```

---

### `src/Ecr.Web/vite.config.ts`
MODULE: web | STAGE: 0

```typescript
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  server: {
    port: 5173,
    proxy: {
      // Проксі на API, щоб cookie працювала без CORS у розробці
      '/api': { target: 'http://localhost:5080', changeOrigin: true, secure: false },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
  },
});
```

---

### `src/Ecr.Web/src/api/client.ts`
MODULE: web-api | STAGE: 6
CONTRACT: 02-contracts.md#error-model

```typescript
import type { paths } from './schema';

/**
 * Помилка API у форматі EcrProblemDetails.
 * Клієнт розрізняє причини **за кодом**, а не за текстом: текст локалізований
 * і може змінюватися, код — ні.
 */
export interface EcrProblem {
  type?: string;
  title: string;
  status: number;
  detail?: string;
  errorCode: string;
  correlationId: string;
  extensions2?: Record<string, unknown>;
}

/** Виняток клієнта API. */
export class EcrApiError extends Error {
  constructor(readonly problem: EcrProblem) {
    super(problem.detail ?? problem.title);
    this.name = 'EcrApiError';
  }

  /** Чи це конфлікт паралельного редагування. */
  get isConflict(): boolean {
    return this.problem.errorCode === 'ECR-CELL-0409';
  }

  /** Перелік конфліктів, якщо вони є. */
  get conflicts(): unknown[] {
    return (this.problem.extensions2?.['conflicts'] as unknown[]) ?? [];
  }
}

/**
 * Базовий HTTP-клієнт.
 * TODO: реалізувати fetch-обгортку:
 *  - credentials: 'include' (автентифікація на cookie, не на токені);
 *  - заголовок X-Correlation-Id генерувати на клієнті і логувати — це єдиний
 *    спосіб звірити скаргу користувача з серверним логом;
 *  - на 401 — редирект на сторінку входу, БЕЗ спроби мовчазного повторного входу;
 *  - на не-2xx — розібрати EcrProblemDetails і кинути EcrApiError;
 *  - на 202 — повернути jobId і statusUrl для стеження за фоновою операцією.
 */
export async function apiFetch<T>(_path: string, _init?: RequestInit): Promise<T> {
  throw new Error('TODO: реалізувати обгортку fetch за описом вище');
}
```

---

### `src/Ecr.Web/src/features/grid/DocumentGrid.tsx`
MODULE: web-grid | STAGE: 6
CONTRACT: 02-contracts.md#dto
SCOPE: найважчий і найдорожчий компонент системи (10–12 тижнів FE).
NOT IN SCOPE: бізнес-правила — вони на сервері; клієнт лише показує і надсилає.

```tsx
import type { TableSliceDto } from '@/api/types';

/** Властивості grid. */
export interface DocumentGridProps {
  /** Документ. */
  documentId: number;
  /** Екземпляр таблиці. */
  tableInstanceId: number;
  /** Ключ періоду. */
  periodKey: number;
  /** Чи доступне редагування на рівні всієї таблиці. */
  readOnly: boolean;
}

/**
 * Grid-редактор документа.
 *
 * Обов'язкові можливості (`B21` §12, критерії FQ-1):
 *  1. навігація клавіатурою як в Excel: Tab / Enter / стрілки / Home-End / Ctrl+стрілки;
 *  2. виділення діапазону мишею, Shift+стрілки, Ctrl+A;
 *  3. **вставка з Excel**: Ctrl+V багатоклітинного буфера з правильним розбором
 *     роздільників і десяткової коми;
 *  4. копіювання у форматі, який Excel приймає;
 *  5. fill-handle (протягування);
 *  6. **Undo/Redo ≥50 кроків** у межах таблиці;
 *  7. віртуалізація: 5000 рядків без просідання;
 *  8. права по комірках із візуальним відрізненням.
 *
 * ⚠ Пункти 3 і 6 — найчастіша причина, з якої grid-бібліотека не підходить.
 * Undo потребує **власної моделі команд**, і не всі бібліотеки дозволяють її
 * вбудувати без форку. Це перевіряється прототипом на Етапі 0, а не після
 * того, як на бібліотеці вже написано половину UI.
 *
 * ⚠ Поведінка вставки в read-only комірки визначена **до** реалізації:
 * якщо у вставленому діапазоні є заборонені комірки — **відхиляється весь
 * батч**, і користувач бачить перелік заборонених. Часткове застосування
 * заборонене на рівні API (B04 §2.3), і UI не має його імітувати.
 */
export function DocumentGrid(_props: DocumentGridProps): JSX.Element {
  throw new Error(
    'TODO: реалізувати grid на RevoGrid:\n' +
    '1) завантажити зріз через useTableSlice; порожні комірки взяти з DefaultValue колонки;\n' +
    '2) побудувати колонки з ColumnDto: тип редактора за dataType, dropdown для Lookup,\n' +
    '   селектор одиниці для Unit, read-only для Formula/Calculated;\n' +
    '3) права з CellPermissions — сірий фон і підказка з причиною;\n' +
    '4) зміни накопичувати в локальному стані і надсилати batch-PATCH із debounce ~500 мс\n' +
    '   або за Ctrl+S; надсилати ТІЛЬКИ змінені комірки;\n' +
    '5) на 409 показати діалог порівняння значень — «перезаписати мовчки» не є опцією;\n' +
    '6) undo/redo — власний стек команд, ≥50 кроків, скидається при зміні таблиці;\n' +
    '7) клієнтський перерахунок через formulajs — ЛИШЕ підказка під час введення;\n' +
    '   збережене значення завжди рахує сервер (D-20).'
  );
}
```

---

### `src/Ecr.Web/src/features/grid/useCellPatch.ts`
MODULE: web-grid | STAGE: 6

```typescript
import type { PatchCellsRequest, PatchCellsResponse } from '@/api/types';

/**
 * Хук пакетного збереження комірок.
 *
 * TODO: реалізувати:
 *  - накопичувати зміни в Map<`${rowKey}:${columnCode}`, value>;
 *  - надсилати batch-PATCH із baseVersion кожного зачепленого рядка;
 *  - новий рядок → baseVersion: null (R-B2);
 *  - розрізняти три операції (R-B4): значення, `value: null` (стерти),
 *    `isEmpty: true` (явна порожнеча). Це різні наміри користувача, і UI має
 *    давати спосіб виразити кожен: Delete → стерти, Ctrl+Delete → явна порожнеча;
 *  - після успіху оновити RowVersion із відповіді — інакше наступний патч
 *    отримає 409 на власних змінах;
 *  - оптимістичне оновлення UI з відкатом при помилці.
 */
export function useCellPatch(_documentId: number): {
  patch: (request: PatchCellsRequest) => Promise<PatchCellsResponse>;
  isPending: boolean;
} {
  throw new Error('TODO: реалізувати за описом вище');
}
```

---

### `src/Ecr.Web/src/app/App.tsx`
MODULE: web | STAGE: 6

```tsx
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router-dom';
import { router } from './router';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Дані звітності змінюються рідко, але помилково показати застаріле — дорого
      staleTime: 30_000,
      retry: (failureCount, error) => {
        // Не повторювати 4xx: 403 і 422 повторення не виправить
        const status = (error as { problem?: { status: number } })?.problem?.status;
        if (status !== undefined && status >= 400 && status < 500) return false;
        return failureCount < 2;
      },
    },
  },
});

/** Корінь застосунку. */
export function App(): JSX.Element {
  return (
    <MantineProvider defaultColorScheme="auto">
      <QueryClientProvider client={queryClient}>
        <Notifications position="top-right" />
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>
  );
}
```

---

### `src/Ecr.Web/src/app/router.tsx`
MODULE: web | STAGE: 6

```tsx
import { createBrowserRouter } from 'react-router-dom';

/**
 * Маршрути застосунку.
 *
 * TODO: створити з lazy-завантаженням сторінок:
 *   /login                              — вхід (два способи: Windows і локальний)
 *   /                                   — список документів
 *   /documents/:id                      — документ, вкладки аркушів, вибір періоду
 *   /admin/templates                    — конструктор шаблонів
 *   /admin/templates/:id/versions/:vid  — редактор структури
 *   /admin/registries                   — конструктор реєстрів
 *   /admin/methodologies                — конфігуратор методологій
 *   /admin/security                     — ролі, користувачі, матриця прав
 *   /admin/periods                      — календар періодів і матриця доступу
 *   /admin/sources                      — конфігуратор джерел і розклад збору
 *   /admin/jobs                         — черга, прогрес, історія перерахунку
 *   /admin/health                       — операційний дашборд і консистентність
 *
 * ⛔ Маршрутів /reports/* НЕМАЄ: звітність лишається в SSRS (D-52).
 */
export const router = createBrowserRouter([
  // TODO: заповнити за описом вище
]);
```

---

### `src/Ecr.Web/src/shared/i18n/index.ts`
MODULE: web | STAGE: 6

```typescript
/**
 * Локалізація (D-11, D-95, ФВ-14.9).
 *
 * ⛔ Словників у збірці НЕМАЄ. Рядки приходять із сервера
 * (`GET /api/v1/ui-strings/{lang}`), інакше обіцянка «додати мову = запис у
 * реєстр, не збірка клієнта» невиконувана: назви аркушів приходили б із БД,
 * а меню й кнопки лишалися б у бандлі.
 *
 * З тієї ж причини мова — `string`, а не union: `'en' | 'ru' | 'kz'` не
 * скомпілювався б із четвертою мовою, тобто робив би саме те, що заборонено.
 *
 * TODO:
 *  - два запити: Scope=Public анонімно (сторінка входу), Scope=Private після
 *    входу (D-114). Кожен зі своїм ETag;
 *  - кеш у localStorage за ключем `uiStrings:{lang}:{scope}:{revision}`;
 *    `If-None-Match` → 304 і беремо кеш;
 *  - fallback: немає ключа в мові → мова за замовчуванням → сам ключ.
 *    Порожнечу не показувати ніколи;
 *  - мова з профілю користувача, до входу — з localStorage або з браузера;
 *  - **тексти помилок звідси ж**, під ключами `err.<код>` (ФВ-14.9a).
 *    Резолвить СЕРВЕР: у відповіді вже є локалізований текст плюс стабільний
 *    errorCode. Клієнт показує текст сервера, а код використовує для логіки;
 *    ключ `err.*` у каталозі потрібен для екранів, які формують повідомлення
 *    самі (офлайн-валідація форми до відправки).
 */
export type Language = string;

/** Повертає переклад за ключем із завантаженого каталогу. */
export function t(_key: string, _params?: Record<string, string | number>): string {
  throw new Error('TODO: пошук у каталозі з сервера + підстановка параметрів');
}
```

---

### `src/Ecr.Web/src/test/setup.ts`
MODULE: web-test | STAGE: 0

```typescript
import '@testing-library/react';

// TODO: за потреби додати мок matchMedia і ResizeObserver — Mantine і RevoGrid
// їх використовують, а jsdom не реалізує.
```

---

## Решта файлів SPA

Створюються за тим самим зразком: типізовані пропси, XML-подібний JSDoc
українською, тіло — `throw new Error('TODO: ...')` зі змістовним описом.

| Область (`B21`) | Тижнів FE | Ключові файли |
|---|---:|---|
| Grid-редактор | 10–12 | `features/grid/*` |
| Конструктор шаблону | 8–10 | `features/template-builder/*` |
| Конструктор реєстрів | 8–10 | `features/registries/*` |
| Конфігуратор методологій | 8–10 | `features/methodologies/*` |
| Адміністрування безпеки | 6–8 | `features/security/*` |
| Workflow + імпорт/експорт + i18n | 6–8 | `features/workflow/*`, `features/import/*` |
| Редактор зв'язків | 5–6 | `features/relations/*` |
| Конфігуратор джерел | 5–6 | `features/sources/*` |
| Редактор конвеєра | 5–6 | `features/pipeline/*` |
| Редактор виразів (Monaco + тести) | 4–5 | `features/expressions/*` |
| Правила і покриття | 4–5 | `features/rules/*` |
| Періоди і матриця доступу | 3–4 | `features/periods/*` |
| Розклад збору | 3–4 | `features/schedule/*` |
| Перерахунок і черга | 3–4 | `features/jobs/*` |
| Операційний дашборд + консистентність | 3–4 | `features/ops/*` |
| ~~Конструктор звітів~~ | — | **поза обсягом (D-52)** |

**Наскрізні вимоги до всіх областей:**

1. Типи API — **згенеровані** з нашого OpenAPI (`npm run api:types`), не написані руками.
2. Помилка показується з кодом і причиною, а не «щось пішло не так».
3. Довгі операції — з прогресом; UI не блокується.
4. Права з `/me` враховуються **до** показу кнопки: користувач не має тиснути
   те, що все одно дасть 403.
5. `npm run typecheck` — зелений; `any` у продуктивному коді не використовується.
