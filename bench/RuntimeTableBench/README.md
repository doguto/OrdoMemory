# RuntimeTable ベンチマーク

RuntimeTable のデータ構造の候補を、同じ条件で比較する.

## 実行

```sh
dotnet build -c Release

# 正しさの検証 (全実装を参照実装と突き合わせる). 計測の前に必ず通す.
dotnet bin/Release/net8.0/RuntimeTableBench.dll verify

# 主キー側 / 二次索引
dotnet bin/Release/net8.0/RuntimeTableBench.dll pk  --n 1000000 --runs 3
dotnet bin/Release/net8.0/RuntimeTableBench.dll sec --n 1000000 --runs 3

# Mono (Unity の Mono バックエンドの近似)
mono bin/Release/net472/RuntimeTableBench.exe pk --n 1000000 --runs 3
```

`--filter <名前の一部>` で実装を絞れる.

## 候補

### 主キー側 (`Pk/`)

| | 構造 |
|---|---|
| A  | PR #10 の現行案そのまま (class の Record, `keys[]` の二分探索, tombstone, 全体コンパクション, `yield` による列挙) |
| A2 | A の Record を struct にしたもの |
| A3 | A2 の探索範囲を「コンパクションで詰めた数」と先頭 / 末尾 Key からの距離で絞ったもの |
| B  | 連続した 1 本の配列 + tombstone + rank テーブル (Key → スロットが O(1)). 全体コンパクション |
| C  | Key 64 個ごとのブロックに生存 Record だけを詰める (設計案の初版). 削除はブロック内で詰める |
| F  | B を Key 2048 個ごとの区画に分け、コンパクションを区画単位にしたもの |
| D  | 世代付きハンドル + sparse set (ECS 方式). Key 順を持たない |
| E  | `Dictionary<int, Row>` |

### 二次索引 (`Sec/`)

| | 構造 |
|---|---|
| S1 | `SortedSet<(Key, PK)>` (赤黒木) |
| S2 | class ノードの B+Tree. 比較は `IComparer<TKey>` |
| S3 | 配列アリーナ上の B+Tree + ulong 正規化キー. B はノードあたりのエントリ数 |
| S4 | `Dictionary<Key, PK の整列済み配列>` (等価検索のみ) |
| S5 | 値ごとの PK ビットマップ (値の種類が少ないキー向け) |

## 指標

| 指標 | 内容 |
|---|---|
| insert ns | 1 件の Insert |
| mem B/row, mem B/entry | 構築後の保持メモリ (`GC.GetTotalMemory`) を件数で割ったもの. 文字列本体は共有しているため含まない |
| find ns | 乱順の Find |
| churn ns | 乱順の Remove + Insert (1 組). N × 4 回 |
| churn p99.9 ns / max us | 上記 1 組ごとの遅延の 99.9 パーセンタイル / 最大 (コンパクション等のスパイク) |
| find(churned) ns | churn の後の Find (削除の累計が件数を大きく超えた状態) |
| all ns/row | 全件の列挙 |
| range64 ns | Key 64 個分の範囲の列挙 |
| fifo ns / max us | 古い順の Remove + Insert |
| alloc B/query | 検索 1 回あたりのアロケーション (CoreCLR のみ) |

## 注意

- Unity の IL2CPP では計測していない. IL2CPP では境界チェックや popcount のコストが異なるため、最終判断の前に IL2CPP ビルドでも計測すること.
- popcount は netstandard2.1 に合わせ SWAR で実装している (CoreCLR の `BitOperations` は使っていない).
- Mono はディストリビューションの Mono 6.8 (SGen GC). Unity の Mono (Boehm GC) とは GC の特性が異なる.
