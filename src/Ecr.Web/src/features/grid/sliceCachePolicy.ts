import type { QueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';

/** Скільки зріз вважається свіжим (`CL-02`: `staleTime` ≥ 5 хв). */
const SliceStaleTime = 5 * 60_000;

/**
 * Політика кешу зрізів: без перезапиту на фокус вікна, свіжість 5 хв.
 *
 * ⛔ `refetchOnWindowFocus` увімкнений за замовчуванням, і саме на цьому
 * екрані він коштує найдорожче: оператор звіряє числа з Excel, повертається
 * до вкладки через 31 с — і кожен змонтований зріз (до 91 на аркуші)
 * перезапитується одночасно. За ті 31 с у документі, який він сам тримає
 * відкритим, не змінилося нічого.
 *
 * ⚠ Функція, а не рядок усередині `App.tsx`: політику треба перевіряти, а
 * `App.tsx` тягне за собою роутер з усіма сторінками. Тут вона перевіряється
 * на порожньому `QueryClient` за десяток мілісекунд.
 *
 * ⚠ Дефолти за ПРЕФІКСОМ ключа: діють і на зрізи, яких у кеші ще немає, —
 * тобто не залежать від того, чи встиг `DocumentGrid` змонтуватися.
 */
export function applySliceCachePolicy(client: QueryClient): void {
  client.setQueryDefaults(queryKeys.slices.all(), {
    staleTime: SliceStaleTime,
    refetchOnWindowFocus: false,
    // ⛔ Perf: `replaceEqualDeep` (structural sharing, query-core 5.62.9) робить `keys.includes(key)` на кожен ключ
    // плоскої мапи, тобто O(n²): `cellPermissions` ~77 тис. ключів на аркуші Land — 7–25 с заморозки головного
    // потоку на КОЖЕН перезапит зрізу (і ~150 мс на кожен `setQueryData` у `onMutate`). Зріз і так замінюється
    // цілком, а посилальну стабільність частин (`cellPermissions` тощо) тримає `sliceApply`/`applyStylePatches`
    // через spread, тож порівнювати вміст нема навіщо. Діє і на `setQueryData` (опції запиту).
    structuralSharing: false,
  });
}

