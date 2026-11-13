# Runtimeテーブル設計

ゲーム実行中に Insert / Update / Remove が繰り返されるテーブルの設計. 対象環境は Unity (netstandard2.1 / C# 9 / Mono・IL2CPP).
メモリ効率とパフォーマンスを最優先とする. 構造の選定根拠は `RuntimeTable.Benchmark.md`、サンプル実装は `Sample/`.

## 要件

- PK による範囲検索は持たない.
- `All` は持つが、順序は保証しない.
- SecondaryKey による検索 (Unique / 非 Unique / 範囲) を持つ.
- 検索でアロケーションを発生させない.
- 1 回の操作の停止時間に上限があること (コンパクションのような O(n) の処理を持たない).

## 概要

| 項目 | 方式 |
|------|------|
| Record の保持 | 生成した struct を隙間無く並べた配列 (dense) |
| PK | 世代付きハンドル (スロット番号 + 世代) |
| PK → Record | スロット表を 1 回引く. O(1) |
| 削除 | 末尾の Record を空いた位置へ移す. O(1). tombstone もコンパクションも無い |
| 二次索引 | 用途で選ぶ: アリーナ B+Tree / Hash / Bitmap |
| 列挙 | struct 列挙子. ゼロアロケーション |

ECS (EnTT, Unity Entities 等) で使われている「世代付きハンドル + sparse set」の構成である.

---

## データ構造

```csharp
// 生存している Record だけを隙間無く並べる. All はこれを先頭から読むだけ.
Row[] dense;

// dense[i] を指しているスロット. Remove で末尾を移すときに使う.
int[] denseToSlot;

// 生存件数. 次の書き込み先の添字でもある.
int count;

// スロット番号 → dense の位置. ハンドルの下位ビットがスロット番号.
struct Slot
{
    public int Dense;  // 使用中: dense の位置 / 空き: 次の空きスロット (空きリスト)
    public int Gen;    // 世代. スロットを再利用するたびに進める
}
Slot[] slots;

// 使ったことのあるスロットの数.
int slotCount;

// 空きスロットのリストの先頭. 無ければ -1.
int freeHead;
```

- `Slot` は 1 つの struct にまとめ、Find で触るキャッシュラインを 1 本にする.
- Record (Row) は PK を持たない. PK が必要な場合は、列挙子の `CurrentPk` か `denseToSlot` と `slots` から求める.

### Record

- スキーマから struct を生成し、テーブルが唯一の実体を持つ.
  - Record ごとのオブジェクトヘッダと参照が無くなり、GC が管理するオブジェクトも配列数本だけになる.
  - 値で受け取るため、同じインスタンスを二重に Insert するという問題が起こり得ない.
- メンバには `readonly` を付ける. `ref readonly` / `in` で受け取ったときの隠れたコピー (defensive copy) を防ぐため.
- 書き換えは Update 経由のみとする. 索引との食い違いを防ぐため.
- 生成時にフィールドをサイズの降順に並べ、パディングを減らす.

---

## 主キー (ハンドル)

PK は DB 側で払い出す.

```
PK = (Gen << SlotBits) | Slot
```

### 対応する型

| PK の型 | スロット | 世代 | 同時に持てる件数 | 同じスロットの再利用回数 |
|---|---|---|---|---|
| int (既定) | 22 bit | 9 bit | 約 419 万 | 512 回 |
| long | 32 bit | 31 bit | 約 21 億 | 約 21 億回 |

- ビットの配分は生成時に決める.
- PK は Record の識別子であり、大小関係に意味は無い. Key 順・挿入順を前提とした API は持たない.

### 世代の一周

- 世代が最大値に達したスロットは、空きリストに戻さず使用を止める.
  - 古いハンドルが、同じスロットを再利用した別の Record を指す事故 (ABA) を防ぐため.
  - 使用を止めたスロットは 1 つにつき 8B を消費し続ける.
- スロットを使い切った場合、Insert を失敗させる.

---

## 操作

### Find (PK から Record の解決)

```csharp
var slot = pk & SlotMask;
if ((uint)slot >= (uint)slotCount) return miss;
ref var s = ref slots[slot];
if (s.Gen != pk >> SlotBits) return miss;    // 削除済み / 再利用済み
return ref dense[s.Dense];
```

- 空きスロットの `Gen` は Remove の時点で進めてあるため、削除済みの PK は世代の比較で弾かれる.

### Insert

1. 空きリストの先頭からスロットを取る. 無ければ `slotCount` 番目を新しく使う.
   - 直前に空いたスロットから使うため、キャッシュに乗っていることが多い.
2. `dense[count]` に書き込み、`denseToSlot[count] = slot`、`slots[slot].Dense = count` とする.
3. PK = `(Gen << SlotBits) | slot` を返す.

### BulkInsert

- 払い出す PK は連続しないため、PK を書き込む出力先 (Span) を引数で受け取る.
- `dense` / `denseToSlot` / `slots` を必要な件数分まとめて拡張してから書き込む.
- 上限を超える場合は何も挿入せずに失敗する.

### Update

- 指定した項目だけを更新する (`Optional<T>`). アロケーションは発生しない.
- SecondaryKey が変わらない場合は索引を更新しない. 変わる場合は、変更前の値で索引から削除し、変更後の値で挿入する.

### Remove

1. Find と同様にスロットを確認する. 存在しない / 削除済みの場合は失敗とする.
2. 対応する二次索引のエントリを削除する.
3. 末尾の Record (`dense[count - 1]`) を `dense[s.Dense]` に移し、移した Record のスロットの `Dense` を書き換える.
4. 末尾を `default` で消す (Record が持つ参照を GC に解放させるため).
5. スロットの世代を進めて空きリストへ戻す (一周した場合は戻さない).

### All

- `dense[0 .. count)` を先頭から読む. 順序は保証しない (Remove のたびに変わる).

---

## 容量と参照の有効期間

- 初期容量は構築時に指定する. `count` が容量に達した場合、配列を倍に拡張する.
- `TrimExcess` で余った容量を返す.
- 次の操作の後は、`ref readonly` で受け取った参照を取り直す.
  - Remove: 末尾の Record が移動するため.
  - Insert / BulkInsert: 配列の拡張で再確保されるため.
- 列挙中の Insert / Remove は `version` で検出し、例外とする.

---

## インデックス構造

索引の値は PK (ハンドル) とする. PK → Record は Find と同じく O(1).
SecondaryKey ごとに、属性で種類を選ぶ.

| 種類 | 向くもの | 構造 | 範囲検索 |
|---|---|---|---|
| `Ordered` (既定) | 一般 | アリーナ B+Tree, B = 64 | 可 |
| `Hash` | 等価検索のみ | `Dictionary<TKey, ...>` | 不可 |
| `Bitmap` | 値の種類が少ないもの (enum, 種別等) | 値ごとのスロット番号のビットマップ | 可 (値の範囲) |

### Ordered: アリーナ B+Tree

- ノードはオブジェクトにせず、葉と内部ノードをそれぞれ数本の配列に置いて `int` の番号で指す.
  - 子への参照が 4B になり、参照を辿る回数とGC が走査するオブジェクトが減る.
- 葉ノードは SecondaryKey の昇順で連結し、`FindRangeByXxx` は葉を辿って範囲を取得する.
- 内部ノードの各エントリは (子の最小キー, 子) とする.
- キーを `ulong` に正規化して比較する.

| SecondaryKey の型 | 正規化キー |
|---|---|
| int / short / enum | 符号ビットを反転した値. 非 Unique は `(反転した値 << 32) \| PK` とし、組の比較を ulong 1 回にする |
| long / double | 順序を保つ変換 (double は符号に応じてビット反転). 非 Unique は PK を別に持つ |
| string | 先頭 4 文字 (UTF-16) を詰めた値. 等しいときだけ `string.CompareOrdinal` を呼ぶ |

- 削除では併合 (merge-at-half) をせず、空になったノードだけを解放する (free-at-empty).
- 一括挿入は、件数が既存に比べて少なければ 1 件ずつ挿入し、多ければ整列して下から作り直す (目安: k < n / 16 なら 1 件ずつ).

#### 非 Unique な SecondaryKey

MySQL (InnoDB) の二次索引と同様に、B+Tree のキーを `(SecondaryKey, PK)` の組とする.

- 組で比較するため、SecondaryKey が重複していても索引のキーは一意になる.
- 特定の SecondaryKey の検索は、`(SecondaryKey, PK の最小値)` から `(SecondaryKey, PK の最大値)` までの範囲検索として扱う.
- Unique な SecondaryKey の索引は、SecondaryKey のみをキーとする.

### Hash

- Unique: `Dictionary<TKey, int>`.
- 非 Unique: 値ごとに PK の配列を持つ. PK は単調増加しないため配列は順序を持たず、削除は末尾との入れ替えとする.

### Bitmap

- 値ごとに、スロット番号のビットマップを持つ. スロット数には上限があるため、ビットマップの大きさにも上限がある.
- 列挙で得たスロット番号に `slots` の世代を付けて PK に戻す.
- 走査は結果の件数ではなくスロット数に比例する. 1 つの値に多くの件数が集まるキーにのみ使う.

---

## API

| Name           | Description                               |
|----------------|-------------------------------------------|
| Insert         | 新規 Record の挿入. 払い出した PK を返す        |
| BulkInsert     | 新規 Record の一括挿入. PK は出力先の Span に書く |
| Update         | 既存 Record の部分更新                        |
| Find           | PK での単一検索 (`TryFind` / `FindRef`)        |
| FindByXxx      | Unique な SecondaryKey での単一検索            |
| FindAllByXxx   | 非 Unique な SecondaryKey での検索              |
| FindFirstByXxx | 非 Unique な SecondaryKey での単一検索 (索引上の先頭) |
| FindRangeByXxx | SecondaryKey での範囲検索 (`Ordered` / `Bitmap` のみ) |
| All            | 全 Record の取得. 順序は保証しない              |
| Remove         | PK に対応する Record の削除                    |
| TrimExcess     | 余った容量を返す                              |

```csharp
public sealed class SampleTable
{
    public bool TryInsert(in SampleRow row, out int pk);
    public bool TryBulkInsert(ReadOnlySpan<SampleRow> rows, Span<int> pks);

    public bool TryFind(int pk, out SampleRow row);
    public ref readonly SampleRow FindRef(int pk, out bool found);

    public bool Update(int pk, Optional<string> name = default, Optional<string> description = default);
    public bool Remove(int pk);

    public AllEnumerable All();
    public bool TryFindFirstByName(string name, out SampleRow row);
    public NameEnumerable FindAllByName(string name);
    public NameEnumerable FindRangeByName(string min, string max);

    public void TrimExcess();
}
```

- 列挙は struct で返す (`foreach (ref readonly var row in table.All())`).
- 比較子は struct をジェネリクスで受けるか、生成コードに埋め込む (仮想呼び出しを避けるため).
- 単一スレッド前提とする.

---

## 計算量

記号: n = テーブルの件数, m = 返す件数, k = BulkInsert の件数, I = テーブルの二次索引の数, d = Bitmap 索引の値の種類数, S = スロット数 (同時に存在した件数の最大), v = 範囲に含まれる値の数.

### テーブル

| API | 計算量 | 補足 |
|---|---|---|
| Find | **O(1)** (最悪) | スロット表を 1 回引いて世代を比べるだけ. ハッシュではないため衝突による悪化も無い |
| Insert | **O(1) 償却** + 索引への挿入 × I | 配列の拡張時のみ O(n). 初期容量を指定すれば最悪 O(1) |
| BulkInsert | **O(k) 償却** + 索引への一括挿入 × I | 配列の拡張は事前に 1 回 |
| Update | **O(1)** + SecondaryKey が変わった索引ごとに削除 + 挿入 | SecondaryKey が変わらなければ O(1) |
| Remove | **O(1)** + 索引からの削除 × I | 末尾の Record を移すだけ |
| All | **O(n)** | n は生存件数. 削除の履歴に依らない |
| TrimExcess | O(n) + 索引の作り直し | |

### 二次索引

| 操作 | Ordered (B+Tree) | Hash | Bitmap |
|---|---|---|---|
| 索引への挿入 | O(log n) | 平均 O(1) | O(log d) |
| 索引からの削除 | O(log n) | 平均 O(1) ※1 | O(log d) |
| FindByXxx (Unique) | O(log n) | 平均 O(1) | ― |
| FindFirstByXxx | O(log n) | 平均 O(1) | O(log d + S/64) ※2 |
| FindAllByXxx | O(log n + m) | 平均 O(1 + m) | O(log d + S/64) ※2 |
| FindRangeByXxx | O(log n + m) | 不可 | O(log d + v·S/64) ※2 |
| 一括挿入 (k 件) | O(k log(n+k)), 作り直す場合 O((n+k) + k log k) | 平均 O(k) | O(k log d) |

- Ordered: ノードの大きさ B は定数 (64). 高さ log_B n, ノード内の探索とずらしが O(B) で、合わせて O(log n). ノードの分割は償却 O(1).
- Hash: 最悪は衝突が偏った場合の O(n).
- ※1 非 Unique の Hash 索引では、値ごとの PK 配列の中での位置を別に持てば O(1). 持たずに探す場合は、その値の件数 p に対して O(p).
- ※2 Bitmap は 64 件ずつまとめて読むため S/64.

---

## メモリ

| 項目 | 1 件あたり |
|---|---|
| Row | Record の大きさ (SampleRow なら 16B, ベンチマークの Row なら 32B) |
| `denseToSlot` | 4B |
| `slots` | 8B |
| **合計** | **Row + 12B** + 配列の余り |

二次索引は 1 件あたり、Ordered (int キー) で約 18B, Bitmap で約 3B (ベンチマークの値).

---

## 未決事項

1. 非 Unique の Hash 索引で、PK 配列の中での位置を持つか (削除 O(1) とメモリの交換).
2. アリーナ B+Tree のメモリ対策. string キーでは class ノード版の約 2 倍 (44 vs 23 B/件) のため、配列をチャンク単位で伸ばす方式と、string で正規化キーを持たない方式を比べる.
3. 二次索引の再計測. ベンチマークは PK が単調増加する前提で測っているため、ハンドル (単調増加しない) で測り直す.
4. Unity の IL2CPP と Mono (Boehm GC) での計測.

---

## 参考文献

- EnTT (sparse set による ECS) — https://github.com/skypjack/entt
- EnTT Discussion #761 (世代の一周による誤判定の懸念) — https://github.com/skypjack/entt/discussions/761
- Unity Entities, *Archetypes / Chunks* — https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/concepts-archetypes.html
- P. Khuong, P. Morin, *Array Layouts for Comparison-Based Searching* — https://arxiv.org/abs/1509.05053
- J. Rao, K. A. Ross, *Making B+-Trees Cache Conscious in Main Memory*, SIGMOD 2000
- Abseil, *B-tree Ordered Containers* — https://abseil.io/blog/20190812-btree
- G. Graefe, *Modern B-Tree Techniques*, 2011 — https://nowpublishers.com/article/Details/DBS-028
- T. Johnson, D. Shasha, *B-trees with inserts and deletes: why free-at-empty is better than merge-at-half*, JCSS 1993
- S. Chambi, D. Lemire et al., *Better bitmap performance with Roaring bitmaps* — https://arxiv.org/abs/1402.6407
- Abseil, *Swiss Tables Design Notes* — https://abseil.io/about/design/swisstables
