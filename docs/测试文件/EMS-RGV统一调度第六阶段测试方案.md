# EMS / RGV 统一调度第六阶段测试方案

## 1. Core 自动化测试

测试文件：

```text
src/Wcs.Core.Tests/TransportAdministrationTests.cs
```

### 1.1 配置版本冲突

1. 使用 `ExpectedVersion=0` 保存初始配置；
2. 确认返回 Version=1；
3. 再次使用 `ExpectedVersion=0` 保存；
4. 确认返回 `VersionConflict=true`，当前版本仍为 1。

### 1.2 独立审批

1. 申请人创建故障换车操作；
2. 申请人尝试审批自己的操作；
3. 确认被拒绝；
4. 具有审批权限的另一账号审批；
5. 确认状态进入 Approved。

### 1.3 防重复执行

1. 完成强制释放操作审批；
2. 第一次调用 `BeginExecutionAsync`；
3. 确认进入 Executing；
4. 第二次调用；
5. 确认失败，不能重复执行。

### 1.4 目标绑定

审批目标为 `runtime` 配置时，尝试执行 `another-config`，必须拒绝。

### 1.5 运行日志幂等

同一个 `Category + RecordId` 连续写入两次，查询只能返回一条最新记录。

### 1.6 驱动幂等

设备状态已经确认 `CMD-01` 时，再次发送同一命令：

- 返回设备已有确认；
- 不再次调用通道写命令。

### 1.7 心跳超时

设备标记在线但心跳超过阈值时，统一驱动必须返回 Offline。

---

## 2. SQL Server 测试

启动 Host 后确认新增表：

```text
Wcs_TransportConfiguration
Wcs_TransportJournal
Wcs_TransportGovernedOperation
Wcs_TransportAudit
```

检查：

- 配置版本条件更新；
- 相同 JournalKey 执行更新而非重复插入；
- 审批记录可恢复；
- 审计记录只追加；
- Host 重启后配置仍存在。

---

## 3. Host API 测试

### 3.1 未认证访问

以下接口在未认证时必须返回 401：

```text
POST /api/transport/administration/operations
POST /api/transport/administration/operations/{id}/approve
PUT  /api/transport/administration/configuration/{id}
POST /api/transport/optimization/executions/{requestId}/reassign
```

### 3.2 权限不足

认证用户没有对应 permission Claim 时，申请或执行必须返回 403 或明确权限错误。

### 3.3 双人确认

- 账号 A 申请；
- 账号 A 自己审批失败；
- 账号 B 审批成功；
- 账号 A 或具备执行权限的账号执行成功；
- 相同 OperationId 第二次执行失败。

### 3.4 配置并发

两个客户端同时读取 Version=3：

- 客户端 A 保存成功为 Version=4；
- 客户端 B 保存返回 409；
- 数据库不能被 B 覆盖。

### 3.5 故障换车

直接调用换车但不带 OperationId，返回 400。

带错误目标或错误操作类型的审批号，返回 409。

带正确审批号后，执行原第五阶段安全换车流程。

---

## 4. 驱动通道联调

现场实现 `ITransportDriverChannel` 后测试：

1. 命令序号单调递增；
2. 相同 CommandId 不重复写 PLC；
3. PLC 拒绝命令可返回错误原因；
4. 确认超时触发上层重试；
5. 心跳中断后车辆离线；
6. 状态序号、位置和活动命令号映射正确；
7. 断线恢复后不会重复执行已确认命令。

---

## 5. Desktop 验收

检查菜单：

```text
配置与审计
```

检查内容：

- 配置版本和数量正确；
- 驱动协议和端点可查看；
- 待审批、失败操作数量正确；
- 审计记录按时间倒序；
- 运行日志可查看；
- 页面不提供危险操作快捷按钮。

---

## 6. Windows CI

必须通过：

```text
Build and test Wcs.Core
Build Wcs.Host
Build Wcs.Desktop
```

重点关注：

- `IReadOnlySet<string>` JSON 序列化；
- SqlSugar `nvarchar(max)` CodeFirst；
- optimistic update 的 Where 条件；
- Controller `ActionResult<T>` 返回类型；
- Avalonia DataGrid 绑定；
- HostedService DI 解析；
- `ReliableTransportVehicleDriver` 超时与取消逻辑。
