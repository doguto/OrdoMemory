# OrdoMemory ベンチマーク

OrdoTable (`docs/design/OrdoTable.md`) を、他の組み込み DB / インメモリ DB と同じ操作で比較する.
計測は BenchmarkDotNet で行う.

## 実行

```sh
cd bench/OrdoMemory.Benchmark
dotnet build -c Release

# 正しさの検証 (同じ操作列を全ストアで実行し、Dictionary 版と結果を突き合わせる). 計測の前に必ず通す.
dotnet bin/Release/net10.0/OrdoMemory.Benchmark.dll verify

# 計測 (BenchmarkDotNet の引数をそのまま渡す)
dotnet bin/Release/net10.0/OrdoMemory.Benchmark.dll --filter '*ReadBenchmarks*'
dotnet bin/Release/net10.0/OrdoMemory.Benchmark.dll --filter '*' --job short

# 一括挿入後の保持メモリ
dotnet bin/Release/net10.0/OrdoMemory.Benchmark.dll footprint
```

BenchmarkDotNet の `Params` はコマンドラインから変えられないため、件数と比較対象は環境変数で絞る.

| 環境変数 | 既定 | 例 |
|---|---|---|
| `ORDO_BENCH_N` | `1000,10000,100000` | `ORDO_BENCH_N=1000000` |
| `ORDO_BENCH_TARGETS` | 全て | `ORDO_BENCH_TARGETS=OrdoMemory,Dictionary,SqliteInMemory` |

結果は `BenchmarkDotNet.Artifacts/results/` に出力される.

## 比較対象

| Target | 内容 |
|---|---|
| `OrdoMemory` | 本ライブラリ (`[OrdoTable]` から生成したテーブル) |
| `Dictionary` | BCL だけで素直に書いた基準. `Dictionary` + 索引ごとの `Dictionary` / `HashSet` / `SortedSet` |
| `MasterMemory` | Cysharp/MasterMemory 3. 読み取り専用の不変 DB. 書き込みは `ImmutableBuilder` で全体を作り直す |
| `Sqlite` / `SqliteInMemory` | Microsoft.Data.Sqlite (ADO.NET). 準備済みのコマンドを使い回す |
| `SqliteNet` / `SqliteNetInMemory` | sqlite-net (Unity でよく使われる ORM). その API の普通の使い方で書く |
| `LiteDb` / `LiteDbInMemory` | LiteDB 5 (組み込みのドキュメント DB). インメモリは `MemoryStream` 上 |

- ファイル版の SQLite は WAL + `synchronous=NORMAL` (コミットごとの fsync をしない、一般的な設定).
- 索引はどのストアでも同じものを張る: `Code` (Unique, 範囲検索), `Category` (非 Unique), `Name` (Unique, string).
- 検索は Record 全体を取り出すところまでを含める (SQL の `SELECT *` 相当).
- 共通のインターフェース (`IStore`) 越しに呼ぶため、どのストアにも仮想呼び出し 1 回分のコストが乗る.

### MasterMemory の扱い

MasterMemory は 1 件の書き込みでも全体を作り直す (O(n log n)) ため、1 件ずつ書くベンチマーク (`WriteBenchmarks`, `InsertBenchmarks`, `YcsbBenchmarks`) からは除く.
書き込みは `BatchUpdateBenchmarks` (1,000 件をまとめて 1 回で作り直す) と `BulkLoadBenchmarks` で測る.
`BulkLoad` は公式の手順どおり、`DatabaseBuilder` でバイナリを作り `MemoryDatabase` で読み込むまでを含める.

### LiteDB の扱い

更新は型付きの API での普通の書き方として `FindById` → `Update` (読み出して文書全体を書き戻す) とする.

## ケース

LevelDB / RocksDB の `db_bench` と YCSB のコアワークロードに対応させている.

| クラス | ケース | 対応 | 内容 |
|---|---|---|---|
| `ReadBenchmarks` | `ReadRandom` | db_bench readrandom | PK で一様乱数に引く |
| | `ReadZipfian` | YCSB C | PK で Zipf 分布 (θ = 0.99) に引く |
| | `ReadByCode` / `ReadByName` | | Unique な二次キー (int / string) で引く |
| | `ReadByCategory` | | 非 Unique な二次キーで引く (1 キーあたり約 64 件) |
| | `ScanRange` | | 二次キーの範囲 (64 件) |
| | `ScanAll` | db_bench readseq | 全件の列挙 |
| `WriteBenchmarks` | `Overwrite` | db_bench overwrite | 索引の無い列の更新 |
| | `UpdateKey` | | 二次キーの更新 (索引の付け替え) |
| | `RemoveInsert` | deleterandom + fillrandom | 削除して挿入し直す (churn) |
| `BatchUpdateBenchmarks` | `BatchUpdate` | | 1,000 件の更新を 1 回のトランザクション / Build で行う |
| `BulkLoadBenchmarks` | `BulkLoad` | db_bench fillrandom (一括) | 空から N 件を一括で入れる (N 件全体の時間) |
| `InsertBenchmarks` | `InsertOneByOne` | db_bench fillrandom | 空から N 件を 1 件ずつ入れる (自動コミット, N 件全体の時間) |
| `YcsbBenchmarks` | `Run` (A, B, D, E, F) | YCSB A〜F | 下記 |

| Workload | 操作の比率 | キーの分布 |
|---|---|---|
| A | read 50 / update 50 | Zipf |
| B | read 95 / update 5 | Zipf |
| C | read 100 | Zipf (`ReadBenchmarks.ReadZipfian`) |
| D | read 95 / insert 5 | 最近挿入したものほど読む |
| E | scan 95 / insert 5 | 開始位置は Zipf, 長さは 1〜100 の一様分布 |
| F | read 50 / read-modify-write 50 | Zipf |

- `BulkLoad` / `InsertOneByOne` 以外は、1 回の呼び出しで 1,000 回の操作を行い、1 操作あたりの時間を示す.
- 繰り返し呼んでも状態が変わらないよう、件数を保つ操作だけにしている. YCSB の insert も、最も古い Record を消してから同じ Code で入れ直す (本家の YCSB は件数が増え続ける).
- 操作の引数は 65,536 件の乱数列から順に取る. 同じ数件だけがキャッシュに乗り続けることを避けるため.

## 指標

| 指標 | 内容 |
|---|---|
| Mean / Median / P95 | 1 操作あたりの時間 (反復ごとの平均の統計. 操作 1 回ごとの遅延の分布ではない) |
| Allocated | 1 操作あたりのアロケーション (`MemoryDiagnoser`) |
| footprint | 一括挿入後の保持メモリ (`footprint` コマンド). managed は GC ヒープ、private はネイティブ (SQLite) を含むプロセス全体の増分 |

## 注意

- 計測は .NET 10 (CoreCLR) のみ. Microsoft.Data.Sqlite 11 が net10.0 専用のため. Unity の Mono / IL2CPP では計測していない.
- 操作 1 回ごとの遅延のばらつき (p99.9 や最大の停止時間) は BenchmarkDotNet では測れない.
- ファイル版の結果は OS のページキャッシュに強く依存する. データがキャッシュに乗りきる大きさで測っている.
