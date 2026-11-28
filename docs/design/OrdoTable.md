# OrdoTable 設計

ゲーム実行中に Insert / Update / Remove が繰り返されるテーブルの設計. 対象環境は Unity (netstandard2.1 / C# 9 / Mono・IL2CPP).
メモリ効率とパフォーマンスを最優先とする. 構造の選定根拠は `OrdoTable.Benchmark.md`、サンプル実装は `Sample/`.

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
| 二次索引 | 用途で選ぶ: アリーナ B+Tree / Hash |
| 列挙 | struct 列挙子. ゼロアロケーション |

ECS (EnTT, Unity Entities 等) で使われている「世代付きハンドル + sparse set」の構成である.

---

## データ構造

```csharp
// 生存している Record だけを隙間無く並べる. All はこれを先頭から読むだけ.
Row[] dense;

// 生存件数. 次の書き込み先の添字でもある.
int count;

// スロット番号 → dense の位置. ハンドルの下位ビットがスロット番号.
struct Slot
{
    public int dense;  // 使用中: dense の位置 / 空き: 次の空きスロット (空きリスト)
    public uint generation;   // 世代. 1 から始め、スロットを再利用するたびに進める. 0 は未使用 / 使用停止
}
Slot[] slots;

// 使ったことのあるスロットの数.
int slotCount;

// 空きスロットのリストの先頭. 無ければ -1.
int freeHead;
```

- `Slot` は 1 つの struct にまとめ、Find で触るキャッシュラインを 1 本にする.
- Record (Row) は自身の PK を持つ. 検索や列挙で得た Record から、他のテーブルや外部のオブジェクトが参照するための PK を直接取り出せるようにするため.
- dense[i] を指しているスロットは、Record が持つ PK の下位ビット (スロット番号) から求める. そのため dense → スロットの逆引き表は持たない.
  - dense 上の Record の PK は常に正しくなければならない. Record を書き換えるのは Update のみとし、Update は PK を保持する.

### Record

- スキーマから struct を生成し、テーブルが唯一の実体を持つ.
  - Record ごとのオブジェクトヘッダと参照が無くなり、GC が管理するオブジェクトも配列数本だけになる.
  - 値で受け取るため、同じインスタンスを二重に Insert するという問題が起こり得ない.
- `partial struct` とする. `readonly struct` でもよい.
- PK のフィールドには `readonly` を必須とする (OM011). PK は Record とスロットを結び付けるため, Update でも書き換えない.
- PK 以外のフィールド (SecondaryKey を含む) は `readonly` を外してよい. 外したフィールドは Update がその場で書き換える.
  - Record 全体を作り直して上書きすると, 大きな Record の一部だけを頻繁に書き換える場合に遅いため (64 フィールド・約 260B の Record で 1 フィールドの更新が約 95ns に対し, その場で書き換えると約 43ns).
  - SecondaryKey (FK など) が変わる場合は, 索引を付け替えた後にそのフィールドだけを書き込む.
  - `readonly` のフィールドを変える場合だけ, 従来どおり Record 全体を作り直す. 値が同じキーを指定しても変更なしとして扱う.
  - テーブルは Record を `ref readonly` かコピーでしか返さないため, `readonly` を外してもテーブル内の Record を書き換えられるのは Update のみで, 索引と食い違うことはない.
- `readonly struct` でない場合, 利用者が定義するメソッド・プロパティには `readonly` 修飾子を付ける (付けなければ OM012 の警告). `ref readonly` / `in` で受け取ったときの隠れたコピー (defensive copy) を防ぐため. フィールドの読み取りではコピーは起きない.
  - 生成するメンバー (`IRow` の実装) には `readonly` を付ける.
- `[PrimaryKey]` を付けたフィールドを必ず 1 つ持つ. 型は PK の型 (`uint` / `ulong`) と一致させる.
  - フィールドは利用者が宣言する. 生成コードで追加すると partial struct のフィールドが複数の宣言に分かれ、並び順が定義されなくなるため (CS0282).
  - PK は Insert で払い出されるまで決まらない. 生成するコンストラクタは PK を引数に取らず、Insert / BulkInsert で払い出した PK を差し込んだコピーを格納する. Insert 時に渡された値は無視する.
  - Update の引数に PK は含めない.
- PK を差し込んだコピーの作成と、Update で Record 全体を作り直す場合は、`RowInitTag` を先頭に取る internal な専用コンストラクタで全フィールドを直接代入して行う.
  - 利用者が同じ型の並びで引数の順番が違うコンストラクタ (例: `(int b, int a)`) を定義していても、引数の位置で呼び違えてフィールドが入れ替わることがない.
- 生成時にフィールドをサイズの降順に並べ、パディングを減らす.

---

## 主キー (ハンドル)

PK は DB 側で払い出す.

```
PK = (Generation << SlotBits) | Slot
```

### 対応する型

PK は符号なし整数とする. 符号ビットも世代に使え、`>>` が論理シフトになるため.

| PK の型 | スロット | 世代 | 同時に持てる件数 | 同じスロットの再利用回数 |
|---|---|---|---|---|
| uint (既定) | 22 bit | 10 bit (1〜1023) | 約 419 万 | 1023 回 |
| ulong | 32 bit | 32 bit | 約 43 億 | 約 43 億回 |

- PK の型は `[PrimaryKey]` を付けたフィールドの型 (`uint` / `ulong`) で決まる. 属性で別途指定はしない.
- ビットの配分は生成時に決める. `[OrdoTable(SlotBits = n)]` で変えられ、省略時は上表の配分になる.
  - uint の 22 / 10 は、同時に持てる件数と同じスロットの再利用回数の釣り合いを取った値.
  - ulong は件数が配列の長さ (`int`) で頭打ちになるため、スロットは 32 bit で足りる. 残りはすべて世代に回す.
- PK は Record の識別子であり、大小関係に意味は無い. Key 順・挿入順を前提とした API は持たない.

### 無効値

- 世代は 1 から始める. そのため PK = 0 は払い出されず、`default` の PK がそのまま無効値 (`InvalidPrimaryKey`) になる.
  - 代入し忘れた PK が、スロット 0 の Record を静かに指す事故を防ぐため.
- スロットの `generation = 0` は「未使用 / 使用停止」を表す. 世代 0 の PK は Find で弾くため、どの PK とも一致しない.

### 世代の一周

- 世代が最大値に達したスロットは、`generation = 0` にして空きリストに戻さず、使用を止める.
  - 古いハンドルが、同じスロットを再利用した別の Record を指す事故 (ABA) を防ぐため.
  - 使用を止めたスロットは 1 つにつき 8B を消費し続ける.
- スロットを使い切った場合、Insert を失敗させる.
  - 各スロットは世代 1〜最大値の回数だけ使えるため、生涯の Insert 回数には `スロット数 × 世代の最大値` の上限がある. 到達するとテーブルが空でも Insert は失敗し続ける.
  - 既定 (uint, SlotBits = 22) では約 2^32 回 (4,290,772,992 回). 毎秒 1 万回 Insert / Remove するワークロードでは約 5 日で到達する.
  - **長時間稼働し Insert / Remove を繰り返すテーブルでは ulong の PK を推奨する.** 世代が 32 bit になり、同じスロットを約 43 億回再利用できる.

### 残容量の観測

| プロパティ | 型 | 意味 |
|---|---|---|
| `AvailableSlotCount` | `int` | 削除を挟まずに、今から続けて Insert できる件数. `スロット数の上限 − Count − RetiredSlotCount` |
| `RetiredSlotCount` | `int` | 世代を使い切り、使用を止めたスロットの数. 増える一方で減らない |
| `RemainingInsertCount` | `ulong` | 生存中の Record を削除してスロットを再利用する場合も含めた、生涯の Insert 回数の残り. `スロット数の上限 × 世代の最大値 − これまでに払い出した PK の数` |

- `AvailableSlotCount` は「今あと何件入るか」、`RemainingInsertCount` は「あと何回 Insert できるか」を表す. 世代の使い切りの兆候は `RemainingInsertCount` と `RetiredSlotCount` で監視する.
- `RemainingInsertCount` が 0 のとき、Remove を行っても Insert は成功しない.
- BulkInsert が Unique 違反で巻き戻した場合も、一度払い出した PK の分だけ `RemainingInsertCount` は減る.

---

## 操作

### Find (PK から Record の解決)

```csharp
var generation = primaryKey >> SlotBits;
var slot = primaryKey & SlotMask;
if (generation == 0 || slot >= (uint)slotCount) return miss;   // 無効値 / 使用停止との一致を弾く
ref var s = ref slots[slot];
if (s.generation != generation) return miss;               // 削除済み / 再利用済み
return ref dense[s.dense];
```

- 空きスロットの `generation` は Remove の時点で進めてあるため、削除済みの PK は世代の比較で弾かれる.

### Insert

1. 空きリストの先頭からスロットを取る. 無ければ `slotCount` 番目を新しく使い、`generation = 1` とする.
   - 直前に空いたスロットから使うため、キャッシュに乗っていることが多い.
2. 払い出す PK を差し込んだ Record を `dense[count]` に書き込み、`slots[slot].dense = count` とする.
3. PK = `(generation << SlotBits) | slot` を返す.

- Unique な SecondaryKey を持つ場合、重複の確認と挿入で索引を 2 回探索しないよう、次の順に行う.
  1. 容量を確保する (`TryReserve`). 確保できなければ失敗とする.
  2. 次に払い出す PK を、状態を変えずに求める (`RowStore.NextPrimaryKey`). `TryReserve` はこの値を変えない.
  3. その PK で Unique な索引へ挿入し、`Insert` の戻り値で重複を検出する. 重複した場合は、それまでに挿入した索引から取り除いて失敗とする.
     - 重複で失敗した場合も 1 の容量確保 (配列の拡張) は残るが、件数・内容は変わらない.
  4. Record を書き込み (上記 1〜3)、非 Unique な索引へ挿入する.
- 失敗した場合、Record・索引・version はいずれも変わらない.

### BulkInsert

- 払い出す PK は連続しないため、PK を書き込む出力先 (Span) を引数で受け取る.
- `dense` / `slots` を必要な件数分まとめて拡張してから書き込む.
- 上限を超える場合は何も挿入せずに失敗する.

### Update

- 指定した項目だけを更新する (`Optional<T>`). アロケーションは発生しない.
- SecondaryKey が変わらない場合は索引を更新しない. 変わる場合は、変更前の値で索引から削除し、変更後の値で挿入する.
- Unique な索引は、変更後の値を先に挿入し、`Insert` の戻り値で重複を検出する. 重複した場合は、それまでに挿入した変更後の値を取り除いて失敗とし、Record・索引・version は変わらない. すべて挿入できた後で変更前の値を削除する.

### Remove

1. Find と同様にスロットを確認する. 存在しない / 削除済みの場合は失敗とする.
2. 対応する二次索引のエントリを削除する.
3. 末尾の Record (`dense[count - 1]`) を `dense[s.dense]` に移し、移した Record のスロットの `dense` を書き換える.
4. 末尾を `default` で消す (Record が持つ参照を GC に解放させるため).
5. スロットの世代を進めて空きリストへ戻す (一周した場合は戻さない).

### All

- `dense[0 .. count)` を先頭から読む. 順序は保証しない (Remove のたびに変わる).

---

## 容量と参照の有効期間

- 初期容量は構築時に指定する. `count` が容量に達した場合、配列を倍に拡張する.
- `TrimExcess` で余った容量を返す. Ordered 索引は葉を詰め直して作り直すため、大量の削除で低充填になったノードもここで戻る.
- 次の操作の後は、`ref readonly` で受け取った参照を取り直す.
  - Remove: 末尾の Record が移動するため.
  - Insert / BulkInsert: 配列の拡張で再確保されるため.
- 列挙中の Insert / Remove は `version` で検出し、例外とする.
  - SecondaryKey が変わらない Update は構造を変えないため, 列挙中でも行える.

---

## インデックス構造

索引の値は PK (ハンドル) とする. PK → Record は Find と同じく O(1).
SecondaryKey ごとに、属性で種類を選ぶ.

| 種類 | 向くもの | 構造 | 範囲検索 |
|---|---|---|---|
| `Ordered` (既定) | 一般. 範囲検索・キー順の列挙が要るもの | アリーナ B+Tree, B = 64 | 可 |
| `Hash` | 完全一致でしか引かないもの (名前, コード等) | `Dictionary<TKey, ...>` | 不可 |

- 既定を `Ordered` とするのは、後から範囲検索が必要になっても索引を作り直さずに済むため.
- 等価検索しか行わないと分かっているキーにのみ `Hash` を指定する. 等価検索が O(log n) から平均 O(1) になる.

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

- 非 Unique で PK を正規化キーに詰めるのは、正規化キーだけで順序が確定する型 (`NeedsFullComparison` が false) で、かつ正規化キーと PK のビット数の和が 64 以下の場合に限る.
  - フル比較が必要な型で詰めると、正規化キーが異なる時点で (先頭部分, PK) の順に決まってしまい、キーの順序が崩れるため.

- 削除では木全体の再平衡 (merge-at-half) をしない. 1 回の削除で触るノードを限り、停止時間を O(b) に抑える.
  - 葉: 件数が b/4 以下になったら、同じ親の隣の葉 (左を優先) と合わせて b 件以下なら併合し、超えるなら隣から借りて均す. 触るのは葉 2 枚と親 1 つだけ.
  - 内部ノード: 併合も借り入れもせず、空になったものだけを解放する (free-at-empty). 根の子が 1 つになったら根を外して高さを下げる.
  - 親をまたいだ併合はしないため、親の唯一の子になった葉や内部ノードは低充填のまま残りうる. 大量に削除した後に充填率と容量を戻したい場合は `TrimExcess` で作り直す.
- 一括挿入は、件数が既存に比べて少なければ 1 件ずつ挿入し、多ければ整列して下から作り直す (目安: k < n / 16 なら 1 件ずつ).
  - 作り直しでは、容量が足りる限り既存のノード配列を使い回す. 足りない場合だけ倍以上に広げて確保し直す. 必要な分まで縮めるのは `TrimExcess` のみ.

#### 非 Unique な SecondaryKey

MySQL (InnoDB) の二次索引と同様に、B+Tree のキーを `(SecondaryKey, PK)` の組とする.

- 組で比較するため、SecondaryKey が重複していても索引のキーは一意になる.
- 特定の SecondaryKey の検索は、`(SecondaryKey, PK の最小値)` から `(SecondaryKey, PK の最大値)` までの範囲検索として扱う.
- Unique な SecondaryKey の索引は、SecondaryKey のみをキーとする.

### Hash

- Unique: `Dictionary<TKey, uint>`.
- 非 Unique: 値ごとに PK の配列を持つ. PK は単調増加しないため配列は順序を持たず、削除は末尾との入れ替えとする.

### 将来の候補: Bitmap

当初は持たない. 値の種類が少なく 1 つの値に大量の件数が集まるキー (enum, 種別, 状態フラグ等) の絞り込みが、計測でボトルネックと分かった場合に追加を検討する.

- 値ごとに、スロット番号のビットマップを持つ. 列挙で得たスロット番号に `slots` の世代を付けて PK に戻す.
- 64 スロットを 1 語でまとめて読めるため、ヒット件数が多いほど B+Tree より有利になる.
- 走査は結果の件数ではなくスロット数に比例するため、ヒットが少ない場合は不利になる.
- 見送る理由: 少数値のキーは非 Unique の Hash でも走査がヒット件数に比例し、実用上の差が出る規模が限られるため. 索引の種類を増やすと、生成コード・テスト・選択基準が増える.

---

## API

| Name           | Description                               |
|----------------|-------------------------------------------|
| Insert         | 新規 Record の挿入. 払い出した PK を返す        |
| BulkInsert     | 新規 Record の一括挿入. PK は出力先の Span に書く |
| Update         | 既存 Record の部分更新                        |
| Find           | PK での単一検索 (`TryFind` / `FindRef`)        |
| FindByXxx      | Unique な SecondaryKey での単一検索 (`TryFindByXxx` / `FindRefByXxx`) |
| FindAllByXxx   | 非 Unique な SecondaryKey での検索              |
| FindFirstByXxx | 非 Unique な SecondaryKey での単一検索 (索引上の先頭. `TryFindFirstByXxx` / `FindFirstRefByXxx`) |
| FindRangeByXxx | SecondaryKey での範囲検索 (`Ordered` のみ) |
| All            | 全 Record の取得. 順序は保証しない              |
| Remove         | PK に対応する Record の削除                    |
| TrimExcess     | 余った容量を返す                              |
| AvailableSlotCount / RetiredSlotCount / RemainingInsertCount | 残容量の観測 (「残容量の観測」を参照) |

```csharp
public sealed class SampleTable
{
    public const uint InvalidPrimaryKey = 0;

    public bool TryInsert(in SampleRow row, out uint primaryKey);
    public bool TryBulkInsert(ReadOnlySpan<SampleRow> rows, Span<uint> primaryKeys);

    public bool TryFind(uint primaryKey, out SampleRow row);
    public ref readonly SampleRow FindRef(uint primaryKey, out bool found);

    public bool Update(uint primaryKey, Optional<string> name = default, Optional<string> description = default);
    public bool Remove(uint primaryKey);

    public AllEnumerable All();
    public bool TryFindFirstByName(string name, out SampleRow row);
    public ref readonly SampleRow FindFirstRefByName(string name, out bool found);
    public NameEnumerable FindAllByName(string name);
    public NameEnumerable FindRangeByName(string min, string max);

    public void TrimExcess();

    public int AvailableSlotCount { get; }
    public int RetiredSlotCount { get; }
    public ulong RemainingInsertCount { get; }
}
```

- 列挙は struct で返す (`foreach (ref readonly var row in table.All())`).
- `FindRef` / `FindRefByXxx` / `FindFirstRefByXxx` は Record をコピーせず `ref readonly` で返す. 見つからない場合は `found = false` とし、`default` の Record への参照を返す. 参照はテーブルを変更する (Insert / Update / Remove / TrimExcess) までの間だけ有効.
- 比較子は struct をジェネリクスで受けるか、生成コードに埋め込む (仮想呼び出しを避けるため).
- 単一スレッド前提とする.
- 生成コードが使う実装用の型 (`RowStore`, 索引, traits, `KeyConvert`, `KeyComparer` 等) は、生成コードから参照するため public だが、`[EditorBrowsable(EditorBrowsableState.Never)]` を付けて補完に出さない. 利用者が直接使うことは想定しない.
  - `RowStore` の `Store` / `Remove` / `GetIndex` は前提 (容量の確保、添字の範囲、PK が格納済みであること) を検査し、満たさなければ例外を投げる.

---

## 計算量

記号: n = テーブルの件数, m = 返す件数, k = BulkInsert の件数, I = テーブルの二次索引の数.

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

| 操作 | Ordered (B+Tree) | Hash |
|---|---|---|
| 索引への挿入 | O(log n) | 平均 O(1) |
| 索引からの削除 | O(log n) | 平均 O(1) ※1 |
| FindByXxx (Unique) | O(log n) | 平均 O(1) |
| FindFirstByXxx | O(log n) | 平均 O(1) |
| FindAllByXxx | O(log n + m) | 平均 O(1 + m) |
| FindRangeByXxx | O(log n + m) | 不可 |
| 一括挿入 (k 件) | O(k log(n+k)), 作り直す場合 O((n+k) + k log k) | 平均 O(k) |

- Ordered: ノードの大きさ B は定数 (64). 高さ log_B n, ノード内の探索とずらしが O(B) で、合わせて O(log n). ノードの分割は償却 O(1).
- Hash: 最悪は衝突が偏った場合の O(n).
- ※1 非 Unique の Hash 索引では、値ごとの PK 配列の中での位置を別に持てば O(1). 持たずに探す場合は、その値の件数 p に対して O(p).

---

## メモリ

| 項目 | 1 件あたり |
|---|---|
| Row | Record の大きさ (SampleRow なら 16B, ベンチマークの Row なら 32B). PK のフィールド (4B / 8B) を含む |
| `slots` | 8B |
| **合計** | **Row + 8B** + 配列の余り |

二次索引は 1 件あたり、Ordered (int キー) で約 18B (ベンチマークの値).

---

## 未決事項

1. ~~非 Unique の Hash 索引で、PK 配列の中での位置を持つか (削除 O(1) とメモリの交換).~~ → 持たない (削除は O(p)). 計測で問題になれば見直す.
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
