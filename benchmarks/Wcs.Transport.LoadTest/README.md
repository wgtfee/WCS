# Wcs.Transport.LoadTest

临时 GitHub Actions 压测工程，用于测量第十二阶段 EMS/RGV 离线仿真内核。

覆盖：

- 100、500、1000、2500、5000 任务单次仿真；
- 五策略 A/B；
- 8 路并发请求排队；
- 20 次 × 1000 任务历史结果保留内存增长；
- 3×3 容量网格、2 次重复。

该分支不应合入生产 `develop`。结果仅代表 GitHub Windows Runner 上的纯内存仿真，不包含 SQL Server、HTTP、SignalR、IIS 或真实 PLC。
