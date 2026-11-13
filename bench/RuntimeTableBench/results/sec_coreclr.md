
### Secondary index: int key, ~250,000 distinct  N=1,000,000  runs=3 (median)  [CoreCLR 8.0.32]

| impl | insert ns | mem B/entry | equal ns | equal ns/hit | range ns | range ns/hit | churn ns | churn max us | bulk ns/entry | alloc B/query |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| S1 SortedSet<(K,PK)> | 916 | 47.9 | 2387 | 478 | 8503 | 133 | 3585 | 4152 | - | 440 |
| S2 Class B+Tree B=32 | 416 | 17.5 | 520 | 104 | 912 | 14.2 | 1318 | 875 | - | 0.00 |
| S3 Arena B+Tree B=16 | 349 | 21.6 | 403 | 80.6 | 720 | 11.3 | 1131 | 1759 | 140 | 0.00 |
| S3 Arena B+Tree B=32 | 279 | 19.2 | 355 | 71.1 | 572 | 8.94 | 973 | 2618 | 134 | 0.00 |
| S3 Arena B+Tree B=64 | 285 | 18.0 | 387 | 77.4 | 546 | 8.52 | 846 | 1701 | 151 | 0.00 |
| S3 Arena B+Tree B=128 | 269 | 17.4 | 316 | 63.3 | 447 | 6.98 | 897 | 2111 | 134 | 0.00 |
| S4 Hash -> sorted PK[] | 152 | 28.0 | 114 | 22.8 | - | - | 692 | 693 | - | 0.00 |

### Secondary index: int key, 16 distinct (low-card)  N=1,000,000  runs=3 (median)  [CoreCLR 8.0.32]

| impl | insert ns | mem B/entry | equal ns | equal ns/hit | range ns | range ns/hit | churn ns | churn max us | bulk ns/entry | alloc B/query |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| S1 SortedSet<(K,PK)> | 300 | 47.9 | 3786830 | 60.6 | 7488612 | 59.9 | 2394 | 2637 | - | 440 |
| S2 Class B+Tree B=32 | 178 | 24.0 | 489540 | 7.83 | 1020588 | 8.16 | 1163 | 494 | - | 0.31 |
| S3 Arena B+Tree B=16 | 188 | 21.6 | 266801 | 4.27 | 459192 | 3.67 | 981 | 11077 | 136 | 0.31 |
| S3 Arena B+Tree B=32 | 125 | 19.2 | 241281 | 3.86 | 355244 | 2.84 | 861 | 10684 | 133 | 0.31 |
| S3 Arena B+Tree B=64 | 124 | 18.0 | 219041 | 3.51 | 328822 | 2.63 | 780 | 10282 | 143 | 0.31 |
| S3 Arena B+Tree B=128 | 124 | 17.4 | 183555 | 2.94 | 262946 | 2.10 | 745 | 10247 | 136 | 0.31 |
| S4 Hash -> sorted PK[] | 18.8 | 4.20 | 44753 | 0.72 | - | - | 4506 | 2808 | - | 0.62 |
| S5 Bitmap (low-card) | 62.4 | 3.15 | 305508 | 4.89 | 635294 | 5.08 | 883 | 647 | - | 0.31 |

### Secondary index: string key random 8-16 chars, ~250,000 distinct  N=1,000,000  runs=3 (median)  [CoreCLR 8.0.32]

| impl | insert ns | mem B/entry | equal ns | equal ns/hit | range ns | range ns/hit | churn ns | churn max us | bulk ns/entry | alloc B/query |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| S1 SortedSet<(K,PK)> | 1440 | 55.8 | 4126 | 826 | 15285 | 239 | 5040 | 2692 | - | 456 |
| S2 Class B+Tree B=32 | 1091 | 23.4 | 1173 | 235 | 2655 | 41.5 | 2930 | 3254 | - | 0.00 |
| S3 Arena B+Tree B=16 | 731 | 49.9 | 805 | 161 | 1344 | 21.0 | 1799 | 3077 | 343 | 0.00 |
| S3 Arena B+Tree B=32 | 718 | 45.9 | 711 | 142 | 1195 | 18.7 | 1845 | 1966 | 323 | 0.00 |
| S3 Arena B+Tree B=64 | 621 | 43.9 | 642 | 129 | 1012 | 15.8 | 1681 | 3136 | 358 | 0.00 |
| S4 Hash -> sorted PK[] | 389 | 28.0 | 283 | 56.6 | - | - | 1524 | 803 | - | 0.00 |

### Secondary index: string key "Item_0000000" (common prefix), ~250,000 distinct  N=1,000,000  runs=3 (median)  [CoreCLR 8.0.32]

| impl | insert ns | mem B/entry | equal ns | equal ns/hit | range ns | range ns/hit | churn ns | churn max us | bulk ns/entry | alloc B/query |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| S1 SortedSet<(K,PK)> | 1734 | 55.8 | 4828 | 964 | 17748 | 277 | 5781 | 4100 | - | 456 |
| S2 Class B+Tree B=32 | 1491 | 23.4 | 1896 | 379 | 3216 | 50.3 | 3463 | 2468 | - | 0.00 |
| S3 Arena B+Tree B=16 | 1323 | 49.9 | 1500 | 300 | 4690 | 73.3 | 3502 | 2169 | 1086 | 0.00 |
| S3 Arena B+Tree B=32 | 1192 | 45.9 | 1481 | 296 | 3029 | 47.3 | 3439 | 2570 | 1003 | 0.00 |
| S3 Arena B+Tree B=64 | 1193 | 43.9 | 1450 | 290 | 2931 | 45.8 | 3119 | 4134 | 1034 | 0.00 |
| S4 Hash -> sorted PK[] | 416 | 28.0 | 361 | 72.2 | - | - | 1467 | 1025 | - | 0.00 |
