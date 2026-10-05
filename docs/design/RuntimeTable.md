# Runtimeテーブル設計

## データ構造

Key は再利用せず単調増加で払い出す. Remove は論理削除（フラグ）とし, 空きスロットはコンパクションで回収する.
検索は `keys` に対する二分探索とするが, 探索範囲を後述の方法で絞るため、実質的にはほぼ直接引きとなる.

```csharp
// 実データ. Key の昇順に並ぶ.
Record[] data;

// data と同じ添字の Key. 昇順. 二分探索の対象.
Key[] keys;

// スロットの使用状況. false は削除済み (tombstone). All / FindRange で読み飛ばす.
bool[] alive;

// 使用中スロット数 (alive / tombstone の合計). 次の書き込み先の添字でもある.
int used;

// tombstone の数.
int deletedCount;

// これまでに削除された Record の累計 (コンパクション済みの分を含む). 探索範囲の絞り込みに使う.
long removedTotal;

// 次に払い出す Key. この Key 以降は一度も使われていない.
Key nextKey;
```

### 主キー

Runtime の DB であるため、主キーは DB 側で生成、割り当てるものとする.

- Insert 時は `nextKey` を払い出し、`nextKey` を進める.
- 削除された Key は再利用しない. Key は常に単調増加する.
- 払い出した Key は `data` の末尾 (`used` 番目) に追記する. Key が昇順に並ぶため、Insert 時に Sort をし直す必要は無い.
- 構築コストは `capacity` に依存しない.

#### 対応する型

- 主キーの型は `short` / `int` / `long` とする.
- Key は開始値 `start` から連番で払い出す.

#### Key の枯渇

Key は再利用しないため、`nextKey` が Key 型の最大値を超える場合は Insert を失敗させる.
コンパクションを行っても Key は回収されない.

### 検索 (Key から添字の解決)

`keys` は昇順で、`start` から連番で払い出した Key のうち削除されたものだけが欠けている.
そのため、Key の添字は次の範囲に収まる.

- 上限: `key - start`
- 下限: `max(0, key - start - removedTotal)`

この範囲 `[下限, min(上限, used - 1)]` に対して二分探索を行う.
削除が無ければ範囲は 1 点になり、直接引きと同じコストになる.
見つかった添字の `alive` が false の場合は、存在しないものとして扱う.

### 削除 (論理削除)

Remove は次の処理を行う.

1. Key から添字を解決する. 存在しない / 削除済みの場合は失敗とする.
2. `alive[index]` を false にし、`deletedCount` と `removedTotal` を加算する.
3. 対応する二次索引のエントリを削除する (削除済みの Record が索引から返らないようにするため).
4. 自動コンパクションが有効な場合、閾値を確認する (後述).

`data[index]` の中身は、コンパクションまでそのまま残す.
Find / Update / 二次索引経由の検索は、`alive` が false のスロットを存在しないものとして扱う.

### 容量と配列の拡張

- 初期 `capacity` は構築時に指定する.
- Insert / BulkInsert で `used` が `capacity` に達している場合、`data` / `keys` / `alive` を拡張する.
- Key が枯渇する場合は Insert を失敗させる.
- 拡張時は各配列を再確保するため、`data` への参照（ref return / Span）は拡張後に無効となる.
- BulkInsert は、必要な件数分を事前に拡張してから挿入する. 上限を超える場合は何も挿入せずに失敗する.

## コンパクション

tombstone を取り除き、`data` / `keys` / `alive` を前方に詰める.

- 生存している Record の相対順序（= Key の昇順）は保つ.
- 詰めた後、`used` を生存数に更新し、`deletedCount` を 0 にする. `removedTotal` はリセットしない.
- `capacity` は縮小しない.
- 二次索引が保持するのは PrimaryKey であり、Key はコンパクションで変化しないため、索引の再構築は不要.
- `data` への参照（ref return / Span）はコンパクション後に無効となる.
- 計算量は `O(used)`.

### 自動コンパクション

Remove の後に、次の条件を満たした場合にコンパクションを実行する.

```
deletedCount / used >= CompactionThreshold
```

- Insert / Update では自動コンパクションを行わない. 参照を無効化するタイミングを Remove に限定するため.
- BulkRemove のような一括削除を設ける場合は、一括処理の完了後に 1 度だけ判定する.

### 設定

構築時にオプションで指定する.

| Name                  | Type     | Default | Description                                         |
|-----------------------|----------|---------|-----------------------------------------------------|
| AutoCompaction        | bool     | true    | false の場合、自動コンパクションを行わない                         |
| CompactionThreshold   | double   | 0.3     | 自動コンパクションの閾値. 使用中スロットのうち tombstone が占める割合 (N割 = N / 10) |

- `CompactionThreshold` は `0 < 値 <= 1` とする. 範囲外の場合は構築時に例外とする.
- `AutoCompaction` が false の場合、`CompactionThreshold` は参照されない.
- 自動コンパクションを無効にした場合でも、手動コンパクション (`Compact`) は利用できる.

## インデックス構造

PrimaryKey は `keys` の二分探索で引くため、専用の索引を持たない. SecondaryKey の索引には B+Tree を使用する.

- SecondaryKey ごとに B+Tree を 1 本持つ.
- 葉ノードは SecondaryKey の昇順で連結し、`FindRangeByXxx` は葉を辿って範囲を取得する.
- 索引が保持する値は Record の PrimaryKey とする. Record 本体は `keys` で添字を解決して `data` から引く.
  - Record の SecondaryKey が変わらない Update では、索引を更新しない.
- Insert / Update / Remove の際に、対応する索引を同期して更新する.
- BulkInsert は 一括構築する

### 非 Unique な SecondaryKey

MySQL (InnoDB) の二次索引と同様に、非 Unique な SecondaryKey の索引では、B+Tree のキーを `(SecondaryKey, PrimaryKey)` の組とする.

- 組で比較するため、SecondaryKey が重複していても索引のキーは一意になる.
- 同じ SecondaryKey を持つ Record は、葉ノード上で PrimaryKey の昇順に連続して並ぶ.
- 特定の SecondaryKey の検索は、`(SecondaryKey, PrimaryKey の最小値)` から `(SecondaryKey, PrimaryKey の最大値)` までの範囲検索として扱う.
- Unique な SecondaryKey の索引は、SecondaryKey のみをキーとする.

## API

| Name           | Description                      |
|----------------|----------------------------------|
| Insert         | 新規 Record の挿入                    |
| BulkInsert     | 新規 Record の一括挿入                  |
| Update         | 既存 Record の更新                    |
| Find           | PrimaryKey での単一検索                |
| FindRange      | PrimaryKey での範囲検索                |
| FindByXxx      | Unique な SecondaryKey での単一検索     |
| FindAllByXxx   | 非 Unique な SecondaryKey での単一範囲検索  |
| FindFirstByXxx | 非 Unique な SecondaryKey での単一検索   |
| FindRangeByXxx | SecondaryKey での範囲検索              |
| All            | 全 Record の取得 (削除済みを除く)          |
| Remove         | PrimaryKey に対応する Record の論理削除    |
| Compact        | 手動コンパクション. 回収したスロット数を返す            |

`Compact` は `AutoCompaction` の設定に関わらず、tombstone が 1 件以上あれば実行する. tombstone が無い場合は何もせず 0 を返す.
