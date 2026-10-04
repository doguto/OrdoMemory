# Runtimeテーブル設計

## データ構造

主キーから計算した添字で配列を直接引く方式とする. 二分探索は行わない.

```csharp
// 実データ. 添字 = 主キー - start.
Record[] data;

// スロットの使用状況. 削除済みスロットを All / FindRange で読み飛ばすために使う.
bool[] alive;

// 次に払い出す未使用の Key. この Key 以降は一度も使われていない.
Key nextFreshKey;

// 削除によって空いた主キー. 再利用はこちらを優先する.
Stack<Key> freeKeys;
```

### 主キー

Runtime の DB であるため、主キーは DB 側で生成、割り当てるものとする.

Insert 時は、まず `freeKeys` から取り出す. 空であれば `nextFreshKey` を払い出し、`nextFreshKey` を進める.
削除等で無効化されたデータの Key は `freeKeys` に積まれる.
未使用の Key を事前に全て積む必要が無いため、構築コストは `capacity` に依存しない.

こちらから Key を割り当てるため、 Insert 時に Sort をし直したりする必要が無い. 使用した Key に対応するスロットに ただ入れるだけで良い.

#### 対応する型

- 主キーの型は `short` / `int` / `long` とする.
- Key は開始値 `start` から連番で払い出す. 添字は `index = key - start` で求める.

### 容量と配列の拡張

- 初期 `capacity` は構築時に指定する.
- Insert / BulkInsert で `freeKeys` が空かつ `nextFreshKey` の添字（`nextFreshKey - start`）が `capacity` に達している場合、`data` と `alive` を拡張する.
- 拡張後の長さの上限は、`start + 長さ - 1` が Key 型の最大値以下となる長さとする. 上限に達している場合は Insert を失敗させる.
- 拡張時は `data` と `alive` を再確保するため、`data` への参照（ref return / Span）は拡張後に無効となる.
- BulkInsert は、必要な件数分を事前に拡張してから挿入する. 上限を超える場合は何も挿入せずに失敗する.

## インデックス構造

PrimaryKey は配列の添字で直接引くため索引を持たない. SecondaryKey の索引には B+Tree を使用する.

- SecondaryKey ごとに B+Tree を 1 本持つ.
- 葉ノードは SecondaryKey の昇順で連結し、`FindRangeByXxx` は葉を辿って範囲を取得する.
- 索引が保持する値は Record の PrimaryKey とする. Record 本体は `data` から引く.
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
| All            | 全 Record の取得                     |
| Remove         | PrimaryKey に対応する Record の削除      |
