# Runtimeテーブル設計案 (メモリ効率 / パフォーマンス最大化)

`RuntimeTable.md` (PR #10) に対する改訂案. 対象環境は Unity (netstandard2.1 / C# 9 / Mono・IL2CPP).
構造の選定は計測に基づく (`RuntimeTable.Benchmark.md`).

## 0. 決定事項

| 項目 | 現行案 (PR #10) | 本案 |
|------|------|------|
| 要件 | PK による範囲検索あり、All は Key 順 | **PK による範囲検索なし. All は順序を保証しない** |
| Record の保持 | class の参照配列 | 生成コードの struct を隙間無く並べた配列 (dense) |
| PK | 単調増加の連番 | **世代付きハンドル** (スロット番号 + 世代) |
| PK → Record | `keys[]` の二分探索 | スロット表を 1 回引くだけ. **O(1)** |
| 削除 | tombstone + 全体コンパクション | 末尾の Record を空いた位置へ移す. **O(1). tombstone もコンパクションも無い** |
| 二次索引 | B+Tree (詳細未定) | 用途で選ぶ: アリーナ B+Tree / Hash / Bitmap |
| 列挙 | `IEnumerable` + `yield` | struct 列挙子. ゼロアロケーション |

計測 (CoreCLR, 100 万件) での現行案との比較:

| | 現行案 | 本案 |
|---|---:|---:|
| Find | 73ns (削除が累積すると 528ns) | 32ns (累積後も 37ns) |
| 乱順の削除 + 挿入 | 581ns | 174ns |
| 1 回の操作の最大の停止 | 14〜24ms (コンパクション) | 0.1ms |
| All (1 件あたり) | 15.3ns | 2.5ns |
| 削除が累積した後のメモリ | 69 B/行 | 26 B/行 |

---

## 1. Record の保持: 世代付きハンドル + sparse set

ECS (EnTT, Unity Entities 等) で使われている構成.

```csharp
// dense: 生存している Record だけを隙間無く並べる. All はこれを先頭から読むだけ.
Row[] dense;
int[] denseToSlot;     // dense[i] を指しているスロット
int count;

// sparse: スロット番号 → dense の位置. ハンドルの下位ビットがスロット番号.
struct Slot
{
    public int Dense;  // 使用中: dense の位置 / 空き: 次の空きスロット (空きリスト)
    public int Gen;    // 世代. スロットを再利用するたびに進める
}
Slot[] slots;
int slotCount;
int freeHead = -1;
```

`Slot` は 1 つの struct にまとめ、Find で触るキャッシュラインを 1 本にする (ベンチマークでは配列 2 本で計測した).

### 1.1 PK (ハンドル)

- PK = `(Gen << SlotBits) | Slot`. 型は現行案どおり `short` / `int` / `long` から選べるようにし、ビットの配分を生成時に決める.

| PK の型 | スロット | 世代 | 同時に持てる件数 | 同じスロットの再利用回数 |
|---|---|---|---|---|
| int (既定) | 22 bit | 9 bit | 約 419 万 | 512 回 |
| long | 32 bit | 31 bit | 約 21 億 | 約 21 億回 |

- **世代が一周したスロットは、空きリストに戻さず使用を止める.** 古いハンドルが別の Record を指す事故 (ABA) を防ぐため. 使わなくなるスロットは 1 つにつき 8B だけ.
- ハンドルは Record の識別子であり、大小関係に意味は無い. 「Key 順」「挿入順」を前提にした API は持たない.

### 1.2 各操作

**Find(pk)**

```csharp
var slot = pk & SlotMask;
if ((uint)slot >= (uint)slotCount) return miss;
ref var s = ref slots[slot];
if (s.Gen != pk >> SlotBits) return miss;    // 削除済み / 再利用済み
return ref dense[s.Dense];
```

**Insert**

1. 空きリストの先頭からスロットを取る (無ければ `slotCount++`). 直前に空いたスロットから使うため、キャッシュに乗っていることが多い.
2. `dense[count]` に書き込み、`denseToSlot[count] = slot`, `slots[slot].Dense = count`.
3. PK = `(Gen << SlotBits) | slot`.

**Remove(pk)**

1. Find と同様にスロットを確認する.
2. 末尾の Record を `dense[s.Dense]` に移し、移した Record のスロットの `Dense` を書き換える.
3. スロットの世代を進めて空きリストへ戻す (一周した場合は戻さない).

**BulkInsert**

- 払い出す PK は連続しないため、PK を書き込む出力先 (配列 / Span) を引数で受け取る.
- `dense` と `slots` を必要な件数分まとめて拡張してから書き込む.

### 1.3 参照の有効期間

- Remove は末尾の Record を移動するため、**Remove の後は参照を取り直す**.
- Insert で `dense` が拡張された場合も参照は無効になる. 規約は「Insert / Remove の後は参照を取り直す」とする.
- 列挙中の変更は `version` で検出する.

### 1.4 メモリ

| 項目 | 1 件あたり |
|---|---|
| Row | 32B (例の SampleSchema の場合) |
| `denseToSlot` | 4B |
| `slots` | 8B |
| **合計** | **44B** + 配列の余り |

- 構築直後に 46 B/行、削除と挿入を繰り返した後に 26 B/行 (ベンチマークの値. 後者は余りが埋まったため).
- 余りを減らしたい場合は、初期容量の指定と `TrimExcess()` で調整する.

### 1.5 不要になるもの

- `Compact` / `AutoCompaction` / `CompactionThreshold` (tombstone が無いため)
- `FindRange(PK)`
- 現行案の `keys[]` / `alive[]` / `removedTotal`

---

## 2. 二次索引

索引の値は PK (ハンドル) とする. 解決は 1.2 の Find と同じく O(1).
索引ごとに属性で種類を選ぶ.

| 種類 | 向くもの | 構造 | 範囲検索 |
|---|---|---|---|
| `Ordered` (既定) | 一般 | アリーナ B+Tree, B = 64 | 可 |
| `Hash` | 等価検索のみ | `Dictionary<TKey, ...>` | 不可 |
| `Bitmap` | 値の種類が少ないもの (enum, 種別等) | 値ごとのスロット番号のビットマップ | 可 (値の範囲) |

### 2.1 Ordered: アリーナ B+Tree

- ノードはオブジェクトにせず、葉と内部ノードをそれぞれ数本の配列に置いて `int` の番号で指す.
- キーを `ulong` に正規化して比較する. int / short / enum の非 Unique 索引は `(反転したキー << 32) | PK` を 1 本の ulong に詰め、組の比較を 1 回にする.
- string は先頭 4 文字を詰めた値で比較し、等しいときだけ `string.CompareOrdinal` を呼ぶ.
- 削除は空になったノードだけを解放する (free-at-empty).
- 計測では class ノード版より 1.3〜2 倍速く、SortedSet より 3〜10 倍速い.
- **要対策**: string キーではメモリが class 版の約 2 倍 (44 vs 23 B/件). 配列を倍々に伸ばすことによる余りが一因なので、チャンク単位で伸ばす方式に変えて再計測する.

### 2.2 Hash

- Unique: `Dictionary<TKey, int>`.
- 非 Unique: 値ごとに PK の配列を持つ. ハンドルは単調増加しないため、配列は順序を持たせず、削除は末尾との入れ替えにする (PK → 配列内の位置は別に持つか、走査で探す).
- 計測では、等価検索が木の 3〜5 倍速い.

### 2.3 Bitmap

- 値ごとに、**スロット番号**のビットマップを持つ (ハンドルではなくスロット番号. スロット数は同時に持てる件数で上限があるため、ビットマップの大きさも上限がある).
- 列挙で得たスロット番号から、`slots` の世代を付けて PK に戻す.
- 計測ではメモリが 3.2 B/件 (木の約 1/6). 本案ではスロット番号が密なため、さらに効率が上がる見込み.

### 2.4 API の意味の変更

- `FindFirstByXxx` の「First」は、現行案では PK の最小を意味したが、ハンドルには順序が無い. 「該当するいずれか 1 件」とするか、索引上の先頭 (SecondaryKey の最小、同値なら任意) とする.

---

## 3. API

```csharp
public sealed class SampleTable
{
    public bool TryInsert(in SampleRow row, out int pk);
    public bool TryBulkInsert(ReadOnlySpan<SampleRow> rows, Span<int> pks);

    public bool TryFind(int pk, out SampleRow row);
    public ref readonly SampleRow FindRef(int pk, out bool found);

    public bool Update(int pk, Optional<string> name = default, Optional<string> description = default);
    public bool Remove(int pk);

    public AllEnumerable All();                         // 順序は保証しない
    public bool TryFindFirstByName(string name, out SampleRow row);
    public IndexRange FindAllByName(string name);
    public IndexRange FindRangeByName(string min, string max);   // Ordered 索引のみ

    public void TrimExcess();
}
```

- 列挙は struct (`foreach (ref readonly var row in table.All())`).
- Row のメンバには `readonly` を付け、`ref readonly` で受け取ったときの隠れたコピーを防ぐ.
- 比較子は struct をジェネリクスで受けるか、生成コードに埋め込む.
- 単一スレッド前提.

---

## 4. 計算量

記号: n = テーブルの件数, m = 返す件数, k = BulkInsert の件数, I = テーブルの二次索引の数, d = Bitmap 索引の値の種類数, S = スロット数 (同時に存在した件数の最大), v = 範囲に含まれる値の数.

### 4.1 テーブル

| API | 計算量 | 補足 |
|---|---|---|
| Find (TryFind / FindRef) | **O(1)** (最悪) | スロット表を 1 回引いて世代を比べるだけ. ハッシュではないため衝突による悪化も無い |
| Insert | **O(1) 償却** + 索引への挿入 × I | 配列の拡張時のみ O(n). 初期容量を指定すれば最悪 O(1) |
| BulkInsert | **O(k) 償却** + 索引への一括挿入 × I | 配列の拡張は事前に 1 回 |
| Update | **O(1)** + SecondaryKey が変わった索引ごとに削除 + 挿入 | SecondaryKey が変わらなければ O(1) |
| Remove | **O(1)** + 索引からの削除 × I | 末尾の Record を移すだけ. コンパクションは無い |
| All | **O(n)** | 隙間の無い配列を先頭から読むだけ. n は生存件数で、削除の履歴に依らない |
| TrimExcess | O(n) + 索引の作り直し | |

### 4.2 二次索引

| 操作 | Ordered (B+Tree) | Hash | Bitmap |
|---|---|---|---|
| 索引への挿入 | O(log n) | 平均 O(1) | O(log d) |
| 索引からの削除 | O(log n) | 平均 O(1) ※1 | O(log d) |
| FindByXxx (Unique) | O(log n) | 平均 O(1) | ― |
| FindFirstByXxx | O(log n) | 平均 O(1) | O(log d + S/64) ※2 |
| FindAllByXxx | O(log n + m) | 平均 O(1 + m) | O(log d + S/64) ※2 |
| FindRangeByXxx | O(log n + m) | 不可 | O(log d + v·S/64) ※2 |
| 一括挿入 (k 件) | O(k log(n+k)), 作り直す場合 O((n+k) + k log k) ※3 | 平均 O(k) | O(k log d) |

- Ordered: ノードの大きさ B は定数 (64). 高さ log_B n, ノード内の探索とずらしが O(B) で、合わせて O(log n). ノードの分割は償却 O(1).
- Hash: 最悪は衝突が偏った場合の O(n).
- ※1 非 Unique の Hash 索引では、値ごとの PK 配列の中での位置を別に持てば O(1). 持たずに探す場合は、その値の件数 p に対して O(p). メモリとの交換のため要決定.
- ※2 Bitmap は結果の件数ではなく**スロット数に比例して**走査する (64 件ずつまとめて読むため S/64). 1 つの値に多くの件数が集まるキー向けで、ヒットが少ないキーには向かない.
- ※3 既存の件数に比べて少なければ 1 件ずつ挿入し、多ければ整列して作り直す (目安: k < n / 16 なら 1 件ずつ).

### 4.3 現行案 (PR #10) との比較

| API | 現行案 | 本案 |
|---|---|---|
| Find | O(log n) (削除が累積すると探索範囲が広がる) | O(1) |
| Remove | O(1), 閾値を超えたときに O(n) のコンパクション | O(1) |
| All | O(n + tombstone の数) | O(n) |
| FindRange (PK) | O(log n + m) | 廃止 |

---

## 5. 残っている作業

1. **Unity の IL2CPP と Mono (Boehm GC) での計測.**
2. 二次索引の再計測: 本案では PK が単調増加しないため、Hash / Bitmap の実装 (2.2 / 2.3) を変えて測り直す.
3. アリーナ B+Tree のメモリ対策 (2.1).
4. `slots` を struct 1 本にした場合の Find の再計測 (1 章).

---

## 参考文献

- EnTT (sparse set による ECS) — https://github.com/skypjack/entt
- Unity Entities, *Archetypes / Chunks* (削除時に末尾を移して詰める) — https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/concepts-archetypes.html
- EnTT Discussion #761 (世代の一周による誤判定の懸念) — https://github.com/skypjack/entt/discussions/761
- P. Khuong, P. Morin, *Array Layouts for Comparison-Based Searching* — https://arxiv.org/abs/1509.05053
- J. Rao, K. A. Ross, *Making B+-Trees Cache Conscious in Main Memory*, SIGMOD 2000
- Abseil, *B-tree Ordered Containers* — https://abseil.io/blog/20190812-btree
- G. Graefe, *Modern B-Tree Techniques*, 2011 — https://nowpublishers.com/article/Details/DBS-028
- T. Johnson, D. Shasha, *B-trees with inserts and deletes: why free-at-empty is better than merge-at-half*, JCSS 1993
- S. Chambi, D. Lemire et al., *Better bitmap performance with Roaring bitmaps* — https://arxiv.org/abs/1402.6407
- Abseil, *Swiss Tables Design Notes* — https://abseil.io/about/design/swisstables
