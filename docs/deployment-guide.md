# 现场部署实践指南 — 信号批量导入与工位验证规则配置

> 回答两个现场落地最常遇到的问题：
> 1. **几千个 PLC 点位** — 逐条手写 JSON 要几天？→ 从博图 CSV 批量导入
> 2. **几十个工位的复杂验证** — 每个工位写一个 C# 类？→ 配置化验证规则（AND/OR 多层）

---

## 一、PLC 信号点位：批量导入

### 方案一：CSV 批量导入（推荐）

从西门子 TIA Portal（博图）导出的标签表 CSV，几分钟导入几千个点位。

```csharp
// 代码中批量导入（一次性的工具函数）
var csvContent = File.ReadAllText("tia_tags.csv");
var importer = new SignalCsvImporter();
var result = importer.ImportFromCsv(csvContent, CsvColumnMap.TiaPortal());

// result.Imported — 导入成功数
// result.Skipped  — 跳过数
// result.Errors   — 错误列表

// 注册到 SignalMapper
signalMapper.RegisterDefinitions(definitions);
```

### CSV 格式示例（TIA Portal 默认导出）

```csv
"Name","Address","DataType","Comment"
"CV01_DriveReady","DB1.DBX0.0","Bool","CV01 驱动就绪"
"CV01_PalletArrived","DB1.DBX0.1","Bool","托盘到位"
"CV01_Speed","DB1.DBW2","Int","当前速度"
"LIFT01_AtFloor","DB2.DBX0.0","Bool","到达楼层"
"LIFT01_GoingUp","DB2.DBX0.1","Bool","上升中"
"ASRS01_Fault","DB3.DBX0.0","Bool","堆垛机故障"
"EStop_ZoneA","DB10.DBX0.0","Bool","A区急停"
```

### 命名约定自动推断

导入器根据标签名自动推断目标事件类型和属性映射，**无需手动指定**：

| 标签名包含 | 自动推断为 |
|-----------|-----------|
| `Arrived` / `到位` | `PalletArrivedEvent` |
| `Ready` / `就绪` | `ConveyorReadyChangedEvent` |
| `Fault` / `Error` / `故障` | `DeviceFaultEvent` |
| `Speed` / `速度` | `ConveyorSpeedChangedEvent` |
| `EStop` / `Emergency` / `急停` | `EmergencyStopEvent` |
| `Mode` / `模式` | `ModeSwitchedEvent` |

### 列映射配置

如果 CSV 格式不同，配置列映射即可：

```csharp
// 西门子 TIA Portal 默认格式
CsvColumnMap.TiaPortal()

// 自定义格式（指定每列的索引）
new CsvColumnMap
{
    TagNameColumn = 0,     // 标签名在第 0 列
    AddressColumn = 2,     // 地址在第 2 列
    DataTypeColumn = 3,    // 类型在第 3 列
    CommentColumn = 5,     // 注释在第 5 列
    HasHeader = true,
    Delimiter = ","
};
```

### 方案二：JSON 逐条配置（少量点位/特殊信号）

少量特殊信号可以在 `appsettings.json` 中手动配置：

```json
{
  "Signals": [
    {
      "SignalId": "CV01_PalletArrived",
      "BlockNumber": 1,
      "ByteOffset": 0,
      "BitOffset": 1,
      "DataType": "bool",
      "TargetEventType": "Wcs.Core.EventBus.Events.PalletArrivedEvent",
      "PropertyMappings": { "DeviceId": "$CV01" },
      "Description": "CV01 托盘到位"
    }
  ]
}
```

### 可用事件类型

| 事件类 | 属性 |
|--------|------|
| `ConveyorReadyChangedEvent` | DeviceId, Ready, PlcName |
| `PalletArrivedEvent` | DeviceId, Barcode, PlcName |
| `DeviceFaultEvent` | DeviceId, FaultCode, Description |
| `EmergencyStopEvent` | DeviceId, PlcName |
| `ConveyorSpeedChangedEvent` | DeviceId, Speed, PlcName |
| `ModeSwitchedEvent` | DeviceId, Mode, PlcName |

---

## 二、工位验证规则：JSON 配置化

### 解决什么问题

> CV03 只有在提升机空闲且 CV03 自身也空闲时，才能接收托盘。否则拒绝。

### 配置验证规则

在 `appsettings.json` → `"ValidationRules"` 中定义：

```json
{
  "ValidationRules": [
    {
      "RuleId": "CV03_Arrive_Prerequisites",
      "TargetDeviceId": "CV03",
      "TargetSignalId": "CV01_PalletArrived",
      "Conditions": {
        "Operator": "AND",
        "Items": [
          { "CheckType": "DeviceState", "DeviceId": "LIFT01", "ExpectedStatus": "Idle" },
          { "CheckType": "DeviceState", "DeviceId": "CV03", "ExpectedStatus": "Idle" }
        ]
      },
      "OnRejectMessage": "LIFT01 忙或 CV03 非空闲，拒绝"
    }
  ]
}
```

**无需写任何 C# 代码。新增规则只需在 JSON 中加一条。**

### 规则匹配逻辑

| 字段 | 含义 | 示例 |
|------|------|------|
| `TargetDeviceId` | 目标设备（null=所有设备） | `"CV03"` |
| `TargetSignalId` | 目标信号（null=该设备所有信号） | `"CV01_PalletArrived"` |

### 条件类型

| CheckType | 说明 | 参数 |
|-----------|------|------|
| `DeviceState` | 检查某个设备的状态 | DeviceId, ExpectedStatus |
| `AlwaysPass` | 始终通过（用于调试） | 无 |
| `AlwaysReject` | 始终拒绝（用于临时屏蔽） | ExpectedValue=原因 |

### 多层 AND/OR 嵌套

支持多层嵌套，解决复杂工位的验证需求：

```json
{
  "RuleId": "ComplexStation_Validation",
  "Conditions": {
    "Operator": "AND",
    "Items": [
      { "CheckType": "DeviceState", "DeviceId": "LIFT01", "ExpectedStatus": "Idle" },
      { "CheckType": "DeviceState", "DeviceId": "CV03", "ExpectedStatus": "Idle" }
    ],
    "Groups": [
      {
        "Operator": "OR",
        "Items": [
          { "CheckType": "DeviceState", "DeviceId": "ASRS01", "ExpectedStatus": "Idle" },
          { "CheckType": "DeviceState", "DeviceId": "ASRS02", "ExpectedStatus": "Idle" }
        ]
      }
    ]
  }
}
```

逻辑为：`(LIFT01.Idle AND CV03.Idle) AND (ASRS01.Idle OR ASRS02.Idle)`

### 临时屏蔽信号

生产现场常见的需求——设备维修时暂时屏蔽某个报警信号：

```json
{
  "RuleId": "Temp_Maintenance_ASRS01",
  "TargetDeviceId": "ASRS01",
  "Conditions": {
    "Operator": "AND",
    "Items": [
      { "CheckType": "AlwaysReject", "ExpectedValue": "设备维修中，信号已屏蔽" }
    ]
  },
  "OnRejectMessage": "ASRS01 维修中，信号已屏蔽"
}
```

恢复时只需 `"Enabled": false` 或删除该规则。

---

## 三、现场部署要点

### 启动日志确认

启动时确认日志中有以下输出：

```
📋 已加载 X 个信号映射定义    ← 确认信号加载数量
🛡️ 已加载 Y 条配置化验证规则  ← 确认验证规则加载数量
```

### 验证规则故障排查

被拒绝的信号会在日志中显示：

```
[验证] ❌ CV03_Arrive_Prerequisites: CV03/CV01_PalletArrived LIFT01 忙 ...
```

### CSV 导入（一次性工具）

CSV 导入通常是一次性的迁移工具，不在每次启动时执行。导入后生成的 `SignalDefinition` 可以直接注册到 `SignalMapperEngine`。

---

## 四、一句话总结

```
PLC 点位：从博图导出 CSV → SignalCsvImporter 批量导入（几分钟几千个点位）
工位验证：在 appsettings.json 中配 ValidationRules（AND/OR 多层，不用写代码）
```