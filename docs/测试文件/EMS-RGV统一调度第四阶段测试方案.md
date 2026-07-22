# EMS / RGV 统一调度第四阶段测试方案

## 1. 单元测试

### TC-TF-001 交叉口互斥

- X-01 映射两个冲突 Edge；
- REQ-A 获取 E-NORTH；
- REQ-B 获取 E-EAST；
- 预期 REQ-B 被拒绝，并记录 REQ-B → REQ-A。

### TC-TF-002 同一任务续租

- 同一 Owner 重复获取同一交通资源；
- 预期不产生重复 Hold，只更新租约。

### TC-TF-003 真实死锁环

- REQ-A 持有 R1、等待 R2；
- REQ-B 持有 R2、等待 R1；
- 预期检测到一个包含 A、B 的环。

### TC-TF-004 受害者选择

- REQ-A 优先级 10；
- REQ-B 优先级 1；
- 预期选择 REQ-B，撤销其等待并释放未占用资源。

### TC-TF-005 物理占用保护

- REQ-B 持有 R2 且 OccupancyConfirmed=true；
- 执行死锁处置；
- 预期 R2 不被释放，状态为 CycleBrokenAwaitingClearance。

### TC-TF-006 交通感知滚动窗口

- REQ-A 已通过 TrafficAwareRouteReservationManager 获取冲突资源；
- REQ-B 尝试预留冲突 Edge；
- 预期路段预留失败并建立等待关系；
- REQ-A 释放后，REQ-B 可成功获取。

### TC-TF-007 快照恢复

- 保存资源定义、Hold、Wait；
- 恢复到新的 Coordinator；
- 预期索引和等待关系一致。

## 2. 集成测试

### TC-TI-001 两车交叉口

- 两辆车同时请求交叉口；
- 只允许一辆收到进入命令；
- 另一辆保持 WaitingForRoute；
- 第一辆退出后，第二辆重新尝试成功。

### TC-TI-002 单轨相向会车

- EMS-A、EMS-B 从单轨两端同时进入；
- 单轨区容量为 1；
- 验证只有优先级高或先到车辆进入。

### TC-TI-003 三车循环等待

- A 等待 B、B 等待 C、C 等待 A；
- 验证生成单一规范化环；
- 验证最低优先级任务被暂停。

### TC-TI-004 重启恢复

- 交通资源包含已确认物理占用；
- 保存系统快照并重启；
- 验证资源定义、占用和等待关系恢复；
- 验证重启后不自动强制释放占用资源。

## 3. Desktop 验收

- 页面可以显示资源、Hold、Wait、Deadlock、Incident；
- 统计卡片与列表数量一致；
- Host 不可达时显示读取失败，不导致 Desktop 崩溃；
- 页面不出现强制释放物理资源按钮。

## 4. CI 验收

Windows CI 必须全部通过：

```text
Build and test Wcs.Core
Build Wcs.Host
Build Wcs.Desktop
```
