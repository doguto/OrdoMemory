# Runtimeテーブル設計案 (メモリ効率 / パフォーマンス最大化)

`RuntimeTable.md` (PR #10) に対する改訂案. API の表面 (Insert / BulkInsert / Update / Find / FindRange / FindXxxByYyy / All / Remove) は維持し、内部構造を置き換える.
対象環境は Unity (netstandard2.1 / C# 9 / Mono・IL2CPP) とする.

## 0. 要約

| 項目 | 現行案 | 本案 |
|------|--------|------|
| Record の保持 | class の参照配列 | 生成コードの struct を 64 件単位のブロックに直接格納 |
| Key → 位置 | `keys[]` の二分探索 (削除が累積すると O(log n)) | ビットマップ + popcount で **常に O(1)** (`keys[]` 不要) |
| 削除 | tombstone + 全体コンパクション (O(n) のスパイク) | ブロック内で詰める (最大 63 件の移動). **全体コンパクション廃止** |
| Insert | 配列拡張時に全件コピー | 末尾ブロックへの追記のみ. **既存 Record は移動しない** |
| 二次索引 | B+Tree (ノードの詳細未定) | 配列アリーナ上の B+Tree. キーを `ulong` に正規化して比較 |
| 低カーディナリティ索引 | (SecondaryKey, PK) の組 | PK ブロックに揃えたビットマップ (Roaring 型) を選択可能 |
| 列挙 | `IEnumerable` + `yield` (呼び出し毎にアロケーション) | struct 列挙子. **ゼロアロケーション** |
| 1 Record あたりのメモリ (後述の例) | 約 53B + 拡張余り + tombstone | 約 25B |

---

## 1. Record の保持: struct + 64 件ブロック

### 1.1 Record は struct

スキーマから struct の Row 型を生成し、テーブルが唯一の実体を持つ.

- Record ごとのオブジェクトヘッダ (16B) と参照 (8B) が消える. GC 管理下のオブジェクト数も Record 数 → ブロック数 (約 1/64) に減る.
- 値で受け取るため、「同一インスタンスの二重 Insert」が起こり得ない. 現行の `IsStored` 判定と `HashSet` による重複検出 (唯一のアロケーション) が不要になる.
- 読み出しは `ref readonly` で返す. 書き換えは Update 経由のみ (索引との食い違いを防ぐ現行方針を維持).
- 生成時にフィールドをサイズの降順に並べ、パディングを減らす.
- Id (PK) は Row に持たなくても位置から復元できる (2.3). メモリを優先する場合は Row から外すオプションを用意する.

### 1.2 ブロック構造

Key の空間を `start` から 64 個ずつのブロックに区切る. ブロック b は Key `[start + 64b, start + 64b + 64)` を担当する.

```csharp
struct Block
{
    public ulong Mask;     // bit i = Key (start + 64b + i) が生存している
    public Row[] Rows;     // 生存 Record を Key の昇順で詰めて格納. 長さは容量クラス (8/16/32/64)
}

Block[] blocks;            // ブロックディレクトリ. blocks[b - firstBlock]
long firstBlock;           // 先頭側の空ブロックは切り詰める (後述)
long nextKey;              // 次に払い出す Key
```

これは HAMT (Bagwell, *Ideal Hash Trees*) のノード圧縮 ―「存在ビットマップ + 存在する要素だけを詰めた配列 + popcount で添字を求める」― を、単調増加 Key の空間に適用したもの.

### 1.3 各操作

**Find(key)**

```csharp
long off = key - start;
if ((ulong)off >= (ulong)(nextKey - start)) return miss;
long b = (off >> 6) - firstBlock;
if (b < 0) return miss;
ref Block blk = ref blocks[b];
int bit = (int)(off & 63);
if ((blk.Mask >> bit & 1) == 0) return miss;
return ref blk.Rows[PopCount(blk.Mask & ((1UL << bit) - 1))];
```

- 依存するメモリアクセスは「ディレクトリ → Rows」の 2 回. 削除の履歴に関わらず一定.
- `keys[]` / `alive[]` は不要になる. Find が触るキャッシュラインも減る.

**Insert**

- Key は単調増加のため、書き込み先は常に末尾ブロック. `Rows[PopCount(Mask)]` に書き、bit を立てる.
- 末尾ブロックは最初から容量 64 の配列を確保する (プールから). そのため **Insert で既存 Record が移動することは無い**.
- ディレクトリの拡張は 64 件に 1 回、1 エントリ 16B の追加のみ.

**BulkInsert**

- 払い出される Key は連続 (`firstKey .. firstKey + n - 1`) になるため、戻り値は先頭 Key だけでよい. Record への Id の書き戻しも不要.
- ブロック単位でまとめて `Array.Copy` し、Mask はブロックごとに 1 回で設定する.

**Remove**

1. Find と同様に bit と slot を求める.
2. bit を落とし、`Array.Copy(rows, slot + 1, rows, slot, count - slot - 1)` で詰め、末尾を `default` にする (GC 参照の解放).
3. ブロックが空になったら Rows をプールに返す.
4. 生存数が容量の 1/4 以下になったら、1 つ小さい容量クラスへ移す (ヒステリシス).

- 移動は最大 63 件. Row が 32B なら最大 2KB の memmove で、**1 回の Remove のコストに上限がある**. 現行案の「閾値を超えた Remove で O(n) のコンパクション」というフレームスパイクが無くなる.
- 末尾以外のブロックには Insert が来ない (Key が単調増加のため). 縮小したブロックが再び伸びることは無い.

**先頭の切り詰め**

- 先頭側の空ブロックは `firstBlock` を進めて切り捨てる. ディレクトリ前方の空きが半分を超えたら、拡張時に詰め直す (償却 O(1)).
- 削除が古い順に起こる使い方 (弾 / エフェクト / バフ / イベントログ等) では、ディレクトリは生存範囲だけを覆う.

### 1.4 計算量

| 操作 | 現行案 | 本案 |
|------|--------|------|
| Find | O(log(削除累計)) 〜 O(log n) | O(1) |
| Insert | 償却 O(1)、拡張時に O(n) コピー | O(1) (64 件に 1 回プールから取得) |
| Remove | O(1) + 閾値で O(n) | O(64) で上限あり |
| FindRange(min,max) | O(log n + 範囲内のスロット数) | O(1) + O(結果数 + ブロック数) |
| All | O(used) (tombstone を読み飛ばす) | O(n + ブロック数). 各ブロック内は連続 |

### 1.5 参照の有効期間

- Insert は既存 Record を移動しない → **Insert をまたいで参照が有効**. 現行案では配列拡張で全参照が無効になる.
- Remove は同じブロックの Record を移動し得る. 規約は単純に「Remove / TrimExcess の後は参照を取り直す」とする.

### 1.6 メモリ見積もり (SampleSchema: `int Id, string Name, string Description`)

| | 現行案 | 本案 |
|---|---|---|
| Record 本体 | オブジェクト 40B (ヘッダ 16 + フィールド 24) | Row 24B (Id を外すと 16B) |
| 参照 / Key / 生存フラグ | 8 + 4 + 1 = 13B | 0 |
| ディレクトリ | ― | 16B / 64 Key ≒ 0.25B |
| 配列ヘッダ | ― | 約 24B / ブロック ≒ 0.4B |
| 余り | 配列拡張 (最大 2 倍) + tombstone (最大 30%) | 容量クラスによる余り (生存数が減ったブロックのみ) |
| **合計 (満杯時)** | **約 53B** | **約 25B** |

### 1.7 弱点

- 非常に古い Record が 1 件だけ生き残ると、ディレクトリは「その Key から `nextKey` まで」を覆い続ける. 1 億 Key の範囲で 25MB.
  - 対策が必要な場合は、ディレクトリを 2 段 (4096 Key ごとのページ、空ページは null) にする. 代わりに Find のメモリアクセスが 1 回増える.
- netstandard2.1 には `System.Numerics.BitOperations` が無い. popcount / tzcnt は SWAR (定数個のシフトと乗算) で実装する (Vigna の broadword 手法).

---

## 2. PK による範囲取得と列挙

### 2.1 struct 列挙子

`FindRange` / `All` / 二次索引の検索は、`GetEnumerator()` パターンの struct を返す.

```csharp
public ref struct PrimaryRangeEnumerator   // または通常の struct
{
    // 現在のブロック, 残りの Mask, ブロック内の slot
    public bool MoveNext();
    public ref readonly Row Current { get; }
    public int CurrentKey { get; }          // Row から Id を外した場合に使う
}

foreach (ref readonly var row in table.FindRange(10, 20)) { ... }
```

- `foreach` はパターンベースで解決されるため、ボックス化もインターフェース呼び出しも無い.
- 次の要素は「Mask の最下位ビットを取り出して消す (`m & (m - 1)`)」で求める. 範囲外の Key や削除済みの Key は 1 命令で飛ばす.
- テーブルに `version` を持ち、Remove で加算する. 列挙中に変更されたら `InvalidOperationException` (Debug ビルドのみ検査、でも可).

### 2.2 FindRange の境界

- 開始位置は Find と同じく O(1). 終端は `max` を含むブロックの Mask を切ることで判定する.

### 2.3 Key の復元

- Key = `start + 64 * (firstBlock + b) + bit`. 列挙子は bit を持っているため追加コストは無い.

---

## 3. 二次索引: 配列アリーナ上の B+Tree

### 3.1 ノードはオブジェクトにしない

ノードを class にすると「ノード → キー配列 → 値配列」と参照を辿り、子へのポインタも 8B になる.
全ノードを数本の大きな配列に置き、ノードを `int` の番号で指す.

```csharp
// ノード n はスロット [n * B, (n + 1) * B) を持つ
ulong[] norm;      // 正規化キー (3.2). ノード内の探索はここだけを見る
TKey[]  full;      // 完全なキー. 正規化キーで順序が決まらない型 (string) のみ
int[]   payload;   // 葉: PK / 内部ノード: 子のノード番号
byte[]  count;
int[]   nextLeaf;  // 葉の連結. 範囲検索で使う
int     freeList;  // 空いたノードの再利用
```

- 子への参照が 4B になり、内部ノードの分岐数が増える. GC の走査対象も配列数本だけになる.
- ノードの大きさ B は、正規化キー領域が 256B 前後 (B = 32) から始めて計測で決める.
  - Abseil の `btree_map` はノードの目標サイズを 256B としている.
  - CSB+-Tree (Rao & Ross) 以降の研究では、キャッシュライン 1 本よりも数倍大きいノードの方が速い場合があると報告されている.
- ノード内の探索は分岐の無い二分探索 (条件付き移動) にする. Khuong & Morin は、小さな整列済み配列では、よく実装した二分探索が最速だとしている.

### 3.2 キーの正規化 (ulong 1 本で比較する)

| SecondaryKey の型 | 正規化キー | `full[]` |
|---|---|---|
| int / short / enum (Unique) | 符号ビットを反転した値 | 不要 |
| int / short / enum (非 Unique) | `(反転した値 << 32) \| PK` | 不要. **組の比較が ulong の比較 1 回** |
| long / double | 順序を保つ変換 (double は符号に応じてビット反転) | 非 Unique は PK を別配列に持つ |
| string | 先頭 4 文字 (UTF-16) をビッグエンディアンで詰めた値 | 必要 |

- string は「先頭を詰めた値が異なれば順序が確定、等しいときだけ `string.CompareOrdinal`」とする. Graefe の言う poor man's normalized key で、比較のほとんどが文字列本体を参照しない.
- 文字列の比較は Ordinal に固定する (カルチャ依存の比較は遅く、結果も環境で変わる).

### 3.3 非 Unique 索引

- 現行案どおり (SecondaryKey, PK) の組をキーとする. PK は単調増加のため、同じ SecondaryKey への追加は常にその連続区間の末尾に入る.
- SecondaryKey も単調な場合 (時刻など) は、右端の葉の分割を 50/50 ではなく 90/10 にする (右端への追記に特化した分割. 葉の充填率が上がる).

### 3.4 削除: 空になったノードだけを解放する

- 削除で併合 (merge-at-half) はせず、空になったノードだけを解放する (free-at-empty).
- Johnson & Shasha は、free-at-empty でも空間効率の低下はわずかで、再構成の回数が大きく減ると報告している. 削除が特定の範囲に偏ると充填率が落ちるという指摘 (Jannink) もあるため、`TrimExcess` で索引を作り直せるようにしておく.

### 3.5 一括構築

- BulkInsert の件数 m が既存件数 n に比べて小さい場合 (m < n / 16 程度) は 1 件ずつ挿入する. それ以上なら、整列済みの列とマージして下から一括構築する.
- 一時領域は `ArrayPool` から借りる.

### 3.6 値は PK のまま

- 索引の値は PK のまま (現行案を維持). 1.3 により PK → Record が O(1) になるため、スロット番号を持つ利点は無い.
- Record の移動 (Remove 時の詰め) で索引を書き換える必要も無い.

---

## 4. 索引の種類を選べるようにする (属性で指定)

| 種類 | 向くもの | 構造 | FindRange |
|---|---|---|---|
| `Ordered` (既定) | 一般 | 3 章の B+Tree | 可 |
| `Bitmap` | 値の種類が少ないもの (enum, 種別, 所属 ID 等) | 値ごとに、PK ブロックに揃えた `(ブロック番号, ulong Mask)` の列 | 可 (値の B+Tree + 列) |
| `Hash` | Unique かつ等価検索のみ | オープンアドレス法のハッシュ表 | 不可 |

### 4.1 Bitmap 索引

- Roaring bitmap (Chambi, Lemire ら) と同じく、件数で表現を切り替える. 疎な値は PK の整列済み配列、密な値はブロック単位のビットマップにする.
- 1.2 の PK ブロックと同じ 64 Key 単位なので、Mask をそのまま列挙子に渡せる. 列挙が Key の昇順になり、ブロックのビットマップとの AND も 1 命令で済む.
- メモリ: 組方式で string 以外のキーなら 1 件あたり約 12B / 充填率. ビットマップは 1 ブロック (最大 64 件) あたり 12B で、密な値なら 1 件あたり 1B 未満.
- PK が単調増加のため、追加は常に列の末尾になる.

### 4.2 Hash 索引

- まずは `Dictionary<TKey, int>` で十分 (内部が配列のみで、アロケーションは拡張時だけ).
- さらに詰める場合は、Swiss table のように 1 スロットあたり 1B の制御バイト (ハッシュの下位 7 bit) を別配列に持ち、比較を減らす. SIMD が使えない IL2CPP でも、8 バイト単位の SWAR で効果がある.

---

## 5. API (生成コードのイメージ)

```csharp
public sealed class SampleTable
{
    public bool TryInsert(in SampleRow row, out int key);
    public bool TryBulkInsert(ReadOnlySpan<SampleRow> rows, out int firstKey);   // Key は firstKey から連続

    public bool TryFind(int key, out SampleRow row);                // コピーで受け取る
    public ref readonly SampleRow FindRef(int key, out bool found); // 参照で受け取る (見つからない場合はダミーへの参照)

    public bool Update(int key, Optional<string> name = default, Optional<string> description = default);
    public bool Remove(int key);

    public PrimaryRange FindRange(int min, int max);   // struct. foreach 可
    public PrimaryRange All();
    public bool TryFindFirstByName(string name, out SampleRow row);
    public IndexRange FindAllByName(string name);
    public IndexRange FindRangeByName(string min, string max);

    public void TrimExcess();    // 旧 Compact. 余った配列をプールに返し、索引を詰め直す
}
```

- `Compact` / `AutoCompaction` / `CompactionThreshold` は不要になる (全体コンパクションが存在しないため).
- 比較子は `struct : IComparer<T>` をジェネリクスで受けるか、生成コードに直接埋め込む. 仮想呼び出しを避け、IL2CPP でのインライン化を効きやすくする.
- スレッド安全ではない (単一スレッド前提). 必要になったら、読み取り専用のスナップショット (MasterMemory の ImmutableBuilder 方式) を別に用意する.

---

## 6. 採らなかった案

| 案 | 理由 |
|----|------|
| 現行案 (keys 二分探索 + tombstone + 全体コンパクション) | 削除が累積すると Find が O(log n) に戻る. コンパクションで O(n) のスパイク. 1 件あたりのメモリが大きい |
| rank テーブル (1 本の連続配列 + ブロックごとの popcount 表) | Find は O(1) になるが、tombstone と全体コンパクションが残る. 本案はブロック内で詰めるため、どちらも不要 |
| 世代付きハンドル + sparse set (EnTT, Unity Entities 等の ECS) | Find / Insert / Remove が O(1) で、列挙は常に隙間が無い. ただし削除で末尾と入れ替えるため **Key の順序が失われ、FindRange(PK) と All の Key 順が成り立たない**. PK は世代込みのハンドルになる. これらの要件を捨てるなら最速 |
| 整列済み配列 (MasterMemory) / Eytzinger 配置 | 読み取り専用なら最速級だが、Insert が O(n). Master テーブル側で検討する |
| ART (Adaptive Radix Tree, Leis ら) | 順序を保ったまま、ハッシュ表に近い点検索性能を持つ. ただしノード型が 4 種類あり、C# では参照を辿る回数が多くなる. string キーの索引が重いと分かった時点で再検討する |

---

## 7. 検証計画

設計値 (ブロック幅 64、B+Tree の B、容量クラス、一括構築の閾値) は計測で決める.

- BenchmarkDotNet (.NET) に加え、**Unity の IL2CPP ビルドでの計測を必須とする**. Mono / IL2CPP / CoreCLR で、popcount や境界チェックのコストが大きく異なるため.
- シナリオ:
  1. 100 万件の Insert → 全件 Find (乱順)
  2. Insert / Remove を 1:1 で 1,000 万回 (削除累計が件数を大きく超える状態) → Find
  3. 古い順の削除 (FIFO) / 乱順の削除
  4. 1 フレームあたりの最大遅延 (Remove のスパイク)
  5. 二次索引: string キー / int キー / 低カーディナリティ (種類 10 程度) での FindAll と FindRange
  6. GC アロケーション量 (0 であることを確認) とメモリ使用量

---

## 参考文献

- P. Bagwell, *Ideal Hash Trees*, 2001. ビットマップ + popcount によるノード圧縮 — https://lampwww.epfl.ch/papers/idealhashtrees.pdf
- S. Vigna, *Broadword Implementation of Rank/Select Queries*, WEA 2008. SWAR による popcount / rank
- S. Chambi, D. Lemire et al., *Better bitmap performance with Roaring bitmaps*, 2014 — https://arxiv.org/abs/1402.6407
- P. Khuong, P. Morin, *Array Layouts for Comparison-Based Searching* — https://arxiv.org/abs/1509.05053
- J. Rao, K. A. Ross, *Making B+-Trees Cache Conscious in Main Memory*, SIGMOD 2000
- Abseil, *B-tree Ordered Containers* — https://abseil.io/blog/20190812-btree
- G. Graefe, *Modern B-Tree Techniques*, Foundations and Trends in Databases, 2011 — https://nowpublishers.com/article/Details/DBS-028
- T. Johnson, D. Shasha, *B-trees with inserts and deletes: why free-at-empty is better than merge-at-half*, JCSS 1993
- V. Leis, A. Kemper, T. Neumann, *The Adaptive Radix Tree: ARTful Indexing for Main-Memory Databases*, ICDE 2013 — https://db.in.tum.de/~leis/papers/ART.pdf
- C. Binnig et al., *Dictionary-based Order-preserving String Compression for Main Memory Column Stores*, SIGMOD 2009 (string キーの辞書化を検討する際の参考)
- Abseil, *Swiss Tables Design Notes* — https://abseil.io/about/design/swisstables
- Unity Entities, *Archetypes / Chunks* — https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/concepts-archetypes.html
- Cysharp, *MasterMemory* — https://github.com/Cysharp/MasterMemory
