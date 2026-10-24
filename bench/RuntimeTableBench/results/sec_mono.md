
### Secondary index: int key, ~250,000 distinct  N=1,000,000  runs=3 (median)  [Mono 4.0.30319.42000]

| impl | insert ns | mem B/entry | equal ns | equal ns/hit | range ns | range ns/hit | churn ns | churn max us | bulk ns/entry | alloc B/query |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| S1 SortedSet<(K,PK)> | 1436 | 48.0 | 4202 | 840 | 16696 | 261 | 5159 | 99073 | - | - |
| S2 Class B+Tree B=32 | 736 | 18.4 | 966 | 193 | 2044 | 31.9 | 2135 | 3053 | - | - |
| S3 Arena B+Tree B=16 | 475 | 21.6 | 622 | 124 | 1122 | 17.5 | 1422 | 3074 | 331 | - |
| S3 Arena B+Tree B=32 | 452 | 19.2 | 540 | 108 | 911 | 14.2 | 1381 | 1869 | 331 | - |
| S3 Arena B+Tree B=64 | 413 | 18.0 | 541 | 108 | 723 | 11.3 | 1217 | 1564 | 329 | - |
| S3 Arena B+Tree B=128 | 432 | 17.4 | 512 | 102 | 647 | 10.1 | 1132 | 1498 | 324 | - |
| S4 Hash -> sorted PK[] | 546 | 29.9 | 343 | 68.6 | - | - | 1435 | 12466 | - | - |

### Secondary index: int key, 16 distinct (low-card)  N=1,000,000  runs=3 (median)  [Mono 4.0.30319.42000]

| impl | insert ns | mem B/entry | equal ns | equal ns/hit | range ns | range ns/hit | churn ns | churn max us | bulk ns/entry | alloc B/query |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| S1 SortedSet<(K,PK)> | 834 | 48.0 | 10730082 | 172 | 20535327 | 164 | 3972 | 64897 | - | - |
| S2 Class B+Tree B=32 | 535 | 25.3 | 648941 | 10.4 | 1336210 | 10.7 | 2489 | 9084 | - | - |
| S3 Arena B+Tree B=16 | 342 | 21.6 | 657038 | 10.5 | 1024695 | 8.20 | 1533 | 15068 | 409 | - |
| S3 Arena B+Tree B=32 | 260 | 19.2 | 502213 | 8.04 | 807507 | 6.46 | 1327 | 11779 | 378 | - |
| S3 Arena B+Tree B=64 | 260 | 18.0 | 470166 | 7.52 | 937468 | 7.50 | 1172 | 12947 | 395 | - |
| S3 Arena B+Tree B=128 | 269 | 17.4 | 355392 | 5.69 | 684039 | 5.47 | 1235 | 12345 | 355 | - |
| S4 Hash -> sorted PK[] | 51.9 | 4.20 | 64501 | 1.03 | - | - | 4806 | 2352 | - | - |
| S5 Bitmap (low-card) | 129 | 3.15 | 472272 | 7.56 | 947757 | 7.58 | 1514 | 2436 | - | - |

### Secondary index: string key random 8-16 chars, ~250,000 distinct  N=1,000,000  runs=3 (median)  [Mono 4.0.30319.42000]

| impl | insert ns | mem B/entry | equal ns | equal ns/hit | range ns | range ns/hit | churn ns | churn max us | bulk ns/entry | alloc B/query |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| S1 SortedSet<(K,PK)> | 2037 | 56.0 | 5463 | 1092 | 25468 | 398 | 6644 | 202146 | - | - |
| S2 Class B+Tree B=32 | 1503 | 26.2 | 1963 | 393 | 4366 | 68.2 | 3787 | 19385 | - | - |
| S3 Arena B+Tree B=16 | 1084 | 49.9 | 1115 | 223 | 2174 | 34.0 | 2212 | 2853 | 568 | - |
| S3 Arena B+Tree B=32 | 1013 | 45.9 | 982 | 196 | 1629 | 25.5 | 2274 | 2303 | 576 | - |
| S3 Arena B+Tree B=64 | 1005 | 43.9 | 973 | 195 | 1465 | 22.9 | 2249 | 3354 | 562 | - |
| S4 Hash -> sorted PK[] | 791 | 29.9 | 662 | 133 | - | - | 2121 | 18935 | - | - |

### Secondary index: string key "Item_0000000" (common prefix), ~250,000 distinct  N=1,000,000  runs=3 (median)  [Mono 4.0.30319.42000]

| impl | insert ns | mem B/entry | equal ns | equal ns/hit | range ns | range ns/hit | churn ns | churn max us | bulk ns/entry | alloc B/query |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| S1 SortedSet<(K,PK)> | 2249 | 56.0 | 7040 | 1406 | 29417 | 460 | 6421 | 63794 | - | - |
| S2 Class B+Tree B=32 | 1828 | 26.2 | 1970 | 393 | 4281 | 66.9 | 4245 | 14403 | - | - |
| S3 Arena B+Tree B=16 | 1760 | 49.9 | 1969 | 393 | 5083 | 79.4 | 3877 | 3767 | 1448 | - |
| S3 Arena B+Tree B=32 | 1772 | 45.9 | 1649 | 329 | 4155 | 64.9 | 3798 | 2959 | 1319 | - |
| S3 Arena B+Tree B=64 | 1558 | 43.9 | 1684 | 337 | 3772 | 58.9 | 4028 | 3708 | 1472 | - |
| S4 Hash -> sorted PK[] | 681 | 29.9 | 572 | 114 | - | - | 1873 | 19386 | - | - |
