# Crane2 脚本驱动逻辑分析

> 模型：`QLP TOOL DEV.demo3d`（Emulate3D Ultimate 2026 / 19.2.0.74）
> 分析时间：2026-10-01｜方法：MCP 实时读取（脚本源码、视觉属性、端口、事件清单）+ 对导出脚本做全量文本检索
> 结论均有实测出处，见 §7 证据清单

---

## 0. 结论摘要

1. **Crane2 的吊车本体脚本是 `CraneToolkitQS`**（实测 `Crane2.Script = CraneToolkitQS`）；其负载处理器
   `Crane2.CraneLift.ALH` 的脚本是 **`PalletLoadHandlerQS2`**（`ALH.Script`，且 `OnPickup`/`OnDropoff`
   是显式 ScriptReference → `ALH_OnPickup`/`ALH_OnDropoff`）。
2. 本引擎的事件绑定规则是 **`<宿主视觉名>_On<事件>`**（§4 有反证）。`CraneToolkitQS` 里的处理器前缀是
   `Crane_`（协议主体）、`Crane1_`（初始化/复位）、`CraneLift_`（升降台）——
   **视觉名是 `Crane2`，因此 `Crane_*` 与 `Crane1_*` 全部不绑定**，只有 `CraneLift_OnReset` 能命中
   （升降台视觉名恰好就是 `CraneLift`）。
   → **Crane2 的搬运协议（取货/送货/手动调度/状态联动）目前不会响应；`ALH` 一侧是活的。**
3. Crane2 的 4 个端口**全部已连接**（`StoreIn→RollerLiftTable1`、`RetrieveIn/StoreOut→SRM4`、
   `RetrieveOut→RollerLiftTable2`），即它已被接入存取系统；对端 `SRM4` 上的检索管理脚本
   （处理器前缀 `SRM4` **能**命中）是活的 —— 所以现状是**"下游会创建转移，吊车侧不执行"**。
4. 全模型 18 个脚本里**没有任何一处提到 `Crane2`**（脚本无需按名引用它，绑定全靠前缀），
   且 `GetCraneLift` 这个被调用的函数**全模型未定义**（它所在的 `ReturnShuttle` 路径本身是死代码）。

---

## 1. 驱动链全景（实测）

### 1.1 Crane2 子系统

| 节点 | 类型 | 脚本（`Script` 属性） | 关键自定义属性 | 端口 |
|---|---|---|---|---|
| `RackConfigurator.Rail.Crane2` | Crane SingleLift | **CraneToolkitQS** | `LastWasStorage`、`LockingLift`、`SpeedX=3.20`、`SpeedY=0.80`、`SpeedZ=5.00`、`VehicleStatus=Idle` | 4/4 已连（见下） |
| `…Crane2.CraneLift` | Box | （无）`privateScript=n` | `Loads`、`LockingHandler` | — |
| `…CraneLift.LiftVisual` | Twister Base | — | — | — |
| `…CraneLift.ALH` | AccalonLoadHandler | **PalletLoadHandlerQS2** | `TransferStateEnabled=True`、`Load=<None>`、`TransferState(0 in/0 out)`、`ArmSpeed=2`、`ArmAcc=2`、`OnPickup→ALH_OnPickup(Count=1)`、`OnDropoff→ALH_OnDropoff(Count=1)` | — |
| `…Crane2.AwaitingRepairSignal` | Pling | — | —（停机指示灯） | — |

**端口（`em3d_node` 实测 `ports=4 connected=4`）**

```
StoreIn      -> RackConfigurator.Rail.InfeedGroup.RollerLiftTable1:Right
RetrieveIn   -> RackConfigurator.Rail.SRM4:Retrieve
StoreOut     -> RackConfigurator.Rail.SRM4:Store
RetrieveOut  -> RackConfigurator.Rail.OutfeedGroup.RollerLiftTable2:Right
```

这四个端口名正是 `CraneToolkitQS → CreateConnectors()` 的产物（`StoreIn/RetrieveIn/StoreOut/RetrieveOut`），
说明这套几何与接线来自"吊车逻辑曾按其原名运行过"的模型（或从原型拷贝而来）。

### 1.2 系统内的调用关系

```
RollerLiftTable1(入库) ──StoreIn──▶ ┌──────────────┐ ──StoreOut──▶ SRM4:Store      （SRM4 上挂 RetrievalManagerQS2010）
                                    │    Crane2    │
RollerLiftTable2(出库) ◀─RetrieveOut┤  (CraneToolkitQS) ├─RetrieveIn◀─ SRM4:Retrieve
                                    └──────┬───────┘
                                           │ CraneLift
                                        ALH（PalletLoadHandlerQS2）
```

- **转移（Transfer）由谁创建**：`RetrievalManagerQS2010`（`SRM4_OnTransferCreated`，前缀 `SRM4` ✓ 命中）
  与 `StorageManagerQS2010`（`SRM2_OnRxTransfer`，前缀 `SRM2` ✓ 命中）调用 `Transfer.Create(...)` /
  `Transfer.Dispatch(...)`；`CraneToolkitQS → Crane_OnDispatchOut` 也会创建出库转移（当前不触发）。
- **谁执行搬运**：`CraneToolkitQS` 的 `Crane_OnRx*` / `Crane_OnTx*` 系列 → 直接 `MoveTo` 吊车与升降台，
  并驱动 `ALH.TransferState` 的 Rx/Tx 回调；`ALH` 侧由 `PalletLoadHandlerQS2` 完成"板伸缩 + 载荷换父"。

### 1.3 两种绑定机制（本模型都在用）

| 机制 | 形态 | 例子 |
|---|---|---|
| **名字自动绑定** | 引擎事件 `<宿主视觉名>_On<事件>`（事件清单见 `get_visual_script_event_snippets`，Crane2 返回 100+ 个可用事件，含 `OnReset/OnDispatchIn/OnRxTransfer/OnTxTransfer/OnVehicleStatusUpdated` 等） | `Cylinder3_OnClick`（宿主视觉 `Cylinder3`）；`ALH_OnReset`（宿主 `ALH`）；`CraneLift_OnReset`（宿主 `CraneLift`） |
| **ScriptReference 显式绑定** | 自定义属性里指向脚本函数名，由组件回调 | `ALH.OnPickup → ALH_OnPickup`、`ALH.OnDropoff → ALH_OnDropoff`；`RetrievalsEnabled.UpdatedScript.Reference.Name = "RM_OnRetrievalsEnabledUpdated"` |

**关键**：`Crane2` 需要的所有协议事件都属第一类 → 前缀必须等于 `Crane2`。

---

## 2. `CraneToolkitQS` 逻辑详解

### 2.1 数据契约（脚本假定的属性/子件）

| 名称 | 出处 | 用途 |
|---|---|---|
| `crane.CraneLift` / 可选 `crane.CraneLift2` | 子件 | 升降台（双升降可选）；`GetLifts()` 按本地 X 排序 |
| `crane.SpeedX / SpeedY / SpeedZ` | 自定义属性 | 吊车横移 / 升降 / 伸缩的速度剖面（`MoveTo(...,speed)`） |
| `crane.VehicleStatus` | 自定义属性 | 状态机（见 2.2） |
| `crane.LockingLift` / `lift.LockingHandler` | 自定义属性 | 双侧互锁（吊车↔升降台各记一侧） |
| `crane.LastWasStorage` | 自定义属性 | 上次落点是否货架（`DropoffLoad` 使用，当前为死代码） |
| `crane.AwaitingRepairSignal` | 子件（Pling） | 停机指示；`Crane_OnVehicleStatusUpdated` 控制其 `SignalVisible` |
| `lift.Loads` | 自定义属性 | 升降台上的载荷集合 |
| 处理器（handler） | `TransferStateEnabled=True` 的后代视觉 | `ALH`（AccalonLoadHandler） |
| `handler.Load` / `handler.LoadPlace` | 自定义属性 | 当前载荷 / 落点视觉（`LoadPlace` 可选） |
| `crane.RetrievalWL` / `crane.StorageWL` | 自定义属性 | 工作列表——**Crane2 上没有**，且只被本脚本的死代码引用（§2.9） |

### 2.2 状态机（`VehicleStatus`）

```
Idle ──OnRxBeforeTransfer──▶ Fetching ──OnRxAfterTransfer──▶ Loaded
  ▲                                                             │
  │                                                  OnTxBeforeTransfer
  └──────────── OnTxAfterTransfer ────────── Delivering ◀──────┘
                                                              
AwaitingRepair（外部置位）：所有协议处理器开头 `IsOutOfService(...)` 直接 return；
恢复时 `Crane_OnVehicleStatusUpdated` 执行 `Transfer.Dispatch(sender)` 重新派单
```

### 2.3 事件协议表（含"当前前缀能否命中"）

| 引擎事件 | `CraneToolkitQS` 中的实现 | 行为要点 | Crane2 是否命中 |
|---|---|---|---|
| `OnInitialize` | `Crane1_OnInitialize` | 吊车 + 各升降台的 `Tracking.SpeedX/Y/Z.CurrentSpeed = 0` | ✗（需 `Crane2_`） |
| `OnReset` | `Crane1_OnReset` | `PhysicsEnabled=false`；清 `lift.Loads`/`LockingHandler`；`LockingLift=null`；`VehicleStatus="Idle"`；**创建 4 个端口**并移除 `Start/End` | ✗ |
| `OnReset`（升降台） | `CraneLift_OnReset` | 确保升降台有 `LoadHandler` 端口（`Start/End=+Y 半高`，`AutoConnect=true`） | **✓ 命中**（视觉名恰为 `CraneLift`） |
| `OnDispatchIn` | `Crane_OnDispatchIn` | `ReadyForIncoming` 为真时 `FirstIncomingWaitingToStart.Run()`，并置 `ReadyForIncoming=false` | ✗ |
| `OnDispatchOut` | `Crane_OnDispatchOut` | 从 `TransferState.Processed` 取第一个 `DispatchOutEnabled` 载荷 → `Transfer.Create(crane, 入/出方向端口, load)`，并移除临时属性 `Rx` | ✗ |
| `OnRxBeforeTransfer` | `Crane_OnRxBeforeTransfer` | `VehicleStatus="Fetching"`；给载荷打临时属性 `Rx=<对端端口名>`；`MoveToPickup()`：按**载荷世界包围盒底部**位置移动吊车与升降台；转交 `handler.TransferState.OnRxBeforeTransfer` | ✗ |
| `OnRxTransfer` | `Crane_OnRxTransfer` | `PickupLoad()`：`handler.TransferState.OnRxTransfer(transfer)`；清除双侧锁 | ✗ |
| `OnRxAfterTransfer` | `Crane_OnRxAfterTransfer` | 转交 `handler.TransferState.OnRxAfterTransfer`；`VehicleStatus="Loaded"` | ✗ |
| `OnTxBeforeTransfer` | `Crane_OnTxBeforeTransfer` | `VehicleStatus="Delivering"`；`MoveToDropoff()`：按 `transfer.RxLocation` 移动；转交 `handler.TransferState.OnTxBeforeTransfer` | ✗ |
| `OnTxTransfer` | `Crane_OnTxTransfer` | 仅转交 `handler.TransferState.OnTxTransfer`（真正的交付由 ALH 组件/脚本完成） | ✗ |
| `OnTxAfterTransfer` | `Crane_OnTxAfterTransfer` | 转交 handler；`ReleaseLocks()`；`ReadyForIncoming=true`；`VehicleStatus="Idle"` | ✗ |
| `OnVehicleStatusUpdated` | `Crane_OnVehicleStatusUpdated` | 控制 `AwaitingRepairSignal.SignalVisible`；从停机恢复时 `Transfer.Dispatch(sender)` | ✗ |

### 2.4 取货（Rx）链路

```
下游发起 Transfer（SRM4/进料线）
→ Crane_OnRxBeforeTransfer：状态=Fetching，标记 Rx，MoveToPickup（吊车 X + 升降台 Y 到载荷正下方/正前方）
→ Crane_OnRxTransfer：handler.TransferState.OnRxTransfer(transfer)   ← ALH 组件在其 TransferState 回调里调用 OnPickup
→ PalletLoadHandlerQS2.ALH_OnPickup：DisablePhysicsDeep → Plate1/Plate2 按 topPlate↔load 的 Z 偏移伸出
   → load.Reparent(topPlate) → sender.Load = load → 板收回
→ Crane_OnRxAfterTransfer：状态=Loaded
```

### 2.5 送货（Tx）链路

```
Crane_OnDispatchOut（或下游请求）创建 StoreOut/RetrieveOut 转移
→ Crane_OnTxBeforeTransfer：状态=Delivering，MoveToDropoff（吊车/升降台到 transfer.RxLocation）
→ Crane_OnTxTransfer：handler.TransferState.OnTxTransfer(transfer)
→ PalletLoadHandlerQS2.ALH_OnDropoff：板伸出 → 若落点是输送线：load.Reparent(Scene)+EnablePhysicsDeep；
   否则 load.Reparent(dropoff.Parent) → 板收回 → sender.Load=null
→ Crane_OnTxAfterTransfer：清锁、ReadyForIncoming=true、状态=Idle
```

### 2.6 运动学要点（`MoveLiftTo`）

- 目标位置先转到**初始坐标系**：`Matrix.Multiply(sender.InitialMatrix, sender.Parent.ObjectToWorldMatrix())` 后取逆
  （避免吊车/升降台已被移动导致的目标漂移），再减去处理器/落点相对位移。
- 轴约束：**吊车只走本地 X**（`baseTarget.Y=Z=0`），**升降台只走本地 Y**（`liftTarget.X=Z=0`）。
- 互锁等待：若 `lift.LockingHandler != null` 且未到位，则 `wait(0.1)` 轮询；就绪后自行加锁
  （`lift.LockingHandler = loadHandler`、`sender.LockingLift = lift`），到位容差 0.05 / 0.01。
- 升降台/吊车两侧 `MoveTo` 分别用 `sender.SpeedY` 与 `sender.SpeedX`。
- `wait(0.1)` + `while` 属于阻塞式协程写法，必须由引擎事件线程调用（这也是它靠事件驱动的原因）。

### 2.7 跨脚本辅助函数（单程序集全局作用域，实测 18 脚本共用一个程序集）

`GetLifts` / `GetHandlers` / `FindHandlerAt` / `GetHandlerForLoad` / `GetLiftFromHandler` /
`CountLoads(OnLift)` / `GetLoads` / `InsertItemSortedX` / `ForEachHandler` / `ForEachLift` /
`ForEachDescendant` / `CloneVisualList` / `FindLoadOnLift` / `ReleaseLocks` / `FindWorkListFor{Pickup,Dropoff}` /
`FindPickupForLoad` / `CraneVehicleSelector` / `DropoffLoad` / `MoveLiftTo` / `MoveToPickup` / `MoveToDropoff` /
`PickupLoad` / `TransformToInitialCoords` / `ResetTrackingSpeed` / `ZSideOf` / `IsOutOfService`。

> 实测：这些函数**没有任何模型内其它脚本调用**（全量检索仅在本脚本内命中）→ 吊车与仓储管理脚本之间
> **没有函数级协作**，耦合只通过 `Transfer`/`TransferState`/端口与自定义属性发生。

### 2.8 死代码与缺陷（实测）

| 项 | 说明 |
|---|---|
| `MoveShuttleTo` / `ReturnShuttle` | 仅定义、无调用点；且二者调用的 **`GetCraneLift` 全模型未定义** → 一旦被调用必运行期报错 |
| `DropoffLoad` | 仅定义、无调用点（Tx 交付实际走 `MoveToDropoff` + `handler.TransferState.OnTxTransfer`） |
| `FindWorkListFor{Pickup,Dropoff}` / `FindPickupForLoad` / `CraneVehicleSelector` | 依赖 `crane.RetrievalWL/StorageWL`，而 **Crane2 无这两个属性**；且在模型内无调用点 |
| 双升降分支 `CraneLift2` | Crane2 无 `CraneLift2`（单升降），分支按设计跳过 |

---

## 3. Crane2 契约核对（实有 vs 脚本需要）

| 脚本需要 | Crane2 实有 | 影响 |
|---|---|---|
| `Script = CraneToolkitQS` | ✓ | 吊车侧协议处理器**在脚本里但不绑定**（§4） |
| `SpeedX/SpeedY/SpeedZ` | ✓ 3.20 / 0.80 / 5.00 | 运动可用 |
| `VehicleStatus` | ✓ Idle | 状态机可用 |
| `LockingLift` / `lift.LockingHandler` | ✓ / ✓ | 互锁可用 |
| `AwaitingRepairSignal` | ✓（Pling） | 停机指示可用 |
| `CraneLift`（子件） | ✓ | `CraneLift_OnReset` 会确保 `LoadHandler` 端口 |
| `ALH`（`TransferStateEnabled=True`） | ✓ + 脚本 `PalletLoadHandlerQS2` | 板伸缩/换父逻辑**活着** |
| 4 个转移端口 | ✓ 4/4 已连 | 物理接线完整 |
| `CraneLift2` | ✗（单升降） | 按设计可缺省 |
| `RetrievalWL` / `StorageWL` | ✗ | 仅死代码引用，当前无影响 |

---

## 4. 为什么"脚本在、逻辑却不生" —— 前缀绑定规则

**规则（实测反证）**：引擎事件按 `<宿主视觉名>_On<事件>` 绑定，**与脚本容器名无关**。

- 反证 A：脚本容器名 `HelpButton`，宿主视觉 `Group1.HelpButton1.Cylinder3`，其处理器写作 `Cylinder3_OnClick` ✓
  → 前缀 = **视觉名**，不是脚本名。
- 反证 B：`Group1.RackConfigurator.Rail1.MotorController`（类型 `CraneMotorController`）挂 `CraneMotorController`
  脚本，但脚本里的处理器是 `C_OnInitialize`/`C_OnReset` → 宿主名是 `MotorController`，**同样不匹配**（同一个坑）。
- 旁证：`Group1.RackConfigurator.Rail1.Crane1.C` 挂 `TwinMastCraneVisual`，其处理器为 `C_*` → 恰好命中 ✓
  （所以 `C` 这个几何配置脚本是活的）。

**Crane2 的处理器缺口**（全部需要写成 `Crane2_On*`）：

```
OnInitialize  OnReset  OnDispatchIn  OnDispatchOut
OnRxBeforeTransfer  OnRxTransfer  OnRxAfterTransfer
OnTxBeforeTransfer  OnTxTransfer  OnTxAfterTransfer
OnVehicleStatusUpdated
```

**模型整体规模**：`js_test action=list` 实测 —— 19 个脚本 / 110 个事件函数中，
**前缀能命中场景视觉的仅 28 个，82 个不命中**；`Crane2`、`MotorController`、`Crane`、`CLH`、`VM`、`RM`、
`RackConfigurator`(部分) 等均属"改名后未同步处理器名"的同一模式。

---

## 5. 与 Crane1 的对比：模型里并存两代吊车实现

| 维度 | **Crane2（B 型：Transfer 协议 + ALH）** | **Crane1（A 型：MotorController + TelescopicLoadHandler）** |
|---|---|---|
| 本体脚本 | `CraneToolkitQS`（前缀 `Crane_*` → 不命中） | `Crane1.Script = (null)`（未挂脚本） |
| 控制器 | 无独立控制器；吊车自己 `MoveTo` | `Rail1.MotorController`（类型/脚本均 `CraneMotorController`）：`Carriage→Crane1`、`Lift→CraneLift`、`CarriageSpeed=3.50`、`LiftSpeed=4.00`、`TargetTolerance=0.05`；公开成员 `DriveTo/DriveRelease`（队列 + 到位容差） |
| 几何脚本 | 无（`CraneLift_OnReset` 只管端口） | `Crane1.C` 挂 `TwinMastCraneVisual`（`C_*` ✓ 命中）：按 `Wheelbase/MastHeight/ShuttleSpacing/ShuttleWidth` 摆放双立柱/顶梁/车轮/升降台/双 ALH |
| 负载处理器 | `ALH`（AccalonLoadHandler）挂 `PalletLoadHandlerQS2`（`ALH_*` ✓ 命中，`OnPickup/OnDropoff` 显式绑定） | `ALH1`/`ALH2`（TelescopicLoadHandler）挂 `TelescopicLoadHandlerForCrane`（处理器前缀 `ALH`/`CLH` → 视觉名 `ALH1/ALH2` **不命中**；而 `TelescopicLoadHandlerForCrane` 的 `DriveTo` 会转发到 `sender.MotorController.DriveTo(...)`） |
| 驱动入口 | 引擎事件（当前断链） | 控制器队列（`DriveTo` 由负载处理器跨视觉调用——这条调用不经事件前缀，故**不依赖改名**） |

**含义**：Crane1 的搬运靠"控制器函数被显式调用"（跨视觉函数调用不看名字前缀），Crane2 的搬运靠"引擎事件"，
后者对视觉改名极其敏感 —— 这正是 Crane2 当前不动的原因。

---

## 6. 修复方案

### 方案 A（推荐）：给 `CraneToolkitQS` 补 `Crane2_*` 处理器（薄包装）

在脚本末尾追加一组同名转发函数（不改动原逻辑，`Crane1_*`/`Crane_*` 保留给其它吊车）：

```javascript
// ===== 视觉 Crane2 的事件绑定（引擎按 <宿主视觉名>_On<事件> 绑定）=====
function Crane2_OnInitialize( sender : Demo3D.Visuals.BoxVisual ) { Crane1_OnInitialize(sender); }
function Crane2_OnReset( sender : Demo3D.Visuals.BoxVisual )      { Crane1_OnReset(sender); }
function Crane2_OnDispatchIn( sender : Demo3D.Visuals.BoxVisual )  { Crane_OnDispatchIn(sender); }
function Crane2_OnDispatchOut( sender : Demo3D.Visuals.BoxVisual ) { Crane_OnDispatchOut(sender); }
function Crane2_OnRxBeforeTransfer( sender : Demo3D.Visuals.BoxVisual, transfer : Demo3D.Visuals.Transfer ) { Crane_OnRxBeforeTransfer(sender, transfer); }
function Crane2_OnRxTransfer( sender : Demo3D.Visuals.BoxVisual, transfer : Demo3D.Visuals.Transfer )       { Crane_OnRxTransfer(sender, transfer); }
function Crane2_OnRxAfterTransfer( sender : Demo3D.Visuals.BoxVisual, transfer : Demo3D.Visuals.Transfer )  { Crane_OnRxAfterTransfer(sender, transfer); }
function Crane2_OnTxBeforeTransfer( sender : Demo3D.Visuals.BoxVisual, transfer : Demo3D.Visuals.Transfer ) { Crane_OnTxBeforeTransfer(sender, transfer); }
function Crane2_OnTxTransfer( sender : Demo3D.Visuals.BoxVisual, transfer : Demo3D.Visuals.Transfer )       { Crane_OnTxTransfer(sender, transfer); }
function Crane2_OnTxAfterTransfer( sender : Demo3D.Visuals.BoxVisual, transfer : Demo3D.Visuals.Transfer )  { Crane_OnTxAfterTransfer(sender, transfer); }
function Crane2_OnVehicleStatusUpdated( sender : Demo3D.Visuals.PhotoEye, value : System.Boolean, oldValue ) { Crane_OnVehicleStatusUpdated(sender, value, oldValue); }
```

注意：`Crane_OnVehicleStatusUpdated` 的形参声明是 `PhotoEye`，而 Crane2 上挂的是 Pling 子件作为指示灯；
实际触发它的视觉应为 `AwaitingRepairSignal`（Pling）—— 若要在 `Crane2` 上生效，需把该处理器改挂到
`AwaitingRepairSignal`（前缀变为 `AwaitingRepairSignal_On*`）或按实际宿主类型调整形参。**这一条需在 UI 中确认。**

落地方式（本工具箱）：
1. `js_snapshot()` 先建快照 →
2. `js_write(target="CraneToolkitQS", source=<原文 + 上述包装>, verify=true)`（`verify=true` 会 Reset 并扫描引擎日志，
   确认整库编译通过；`js_lint` 会先做写前拦截）→
3. Reset 后在日志/Console 观察转移是否开始执行（`Verbose` 打开 `RetrievalManagerQS2010` 的打印可看派单）。

### 方案 B：把视觉改名为 `Crane` 或 `Crane1`

可让现有 `Crane_*`/`Crane1_*` 直接命中，但**改名是 UI 操作**，且会影响端口对端引用、`Crane2` 这个名字在
其它对象里的引用（本模型实测无脚本按名引用 `Crane2`，风险较低）。**不推荐**：一个模型里已有 `Crane1`，
再出现同名会引发歧义。

### 方案 C：改用 ScriptReference 显式绑定

把 `OnDispatch*`/`OnRx*`/`OnTx*` 这类回调改成组件级 ScriptReference 绑定（像 `ALH.OnPickup` 那样）。
可行但改造面大，且这些是**引擎级事件**（`get_visual_script_event_snippets` 列出的），通常只能按名字绑定。

### 验证清单（修复后）

1. `js_test action=list target="CraneToolkitQS"` → 应出现 `OK Crane2_On* (handler of visual 'Crane2')`。
2. `js_write(..., verify=true)` → `Compile verify: Reset clean`。
3. Reset 后：`Crane2` 的 `VehicleStatus` 应从 `Idle` 变为 `Fetching/Loaded/Delivering`（用
   `em3d_props(target="RackConfigurator.Rail.Crane2", keys="VehicleStatus,LockingLift")` 观察；
   注意 ⚠️ 运行态下全模型遍历类工具会被门禁 REFUSED，需在停止态读取）。
4. 转移是否完成：看载荷是否从货架换父到 `ALH.Plate1.Plate2`（`em3d_props(target="…CraneLift.ALH", keys="Load")`
   与 `Loads`）。

---

## 7. 证据清单（可复现）

| # | 命令 | 关键结果 |
|---|---|---|
| 1 | `em3d_props(target="RackConfigurator.Rail.Crane2", keys="Script,…")` | `Script = CraneToolkitQS`；`TransferStateEnabled=True`；`SpeedX=3.20/SpeedY=0.80/SpeedZ=5.00` |
| 2 | `em3d_props(target="…Crane2.CraneLift.ALH", keys="Script,OnPickup,OnDropoff,…")` | `Script = PalletLoadHandlerQS2`；`OnPickup→ALH_OnPickup(Count=1)`、`OnDropoff→ALH_OnDropoff(Count=1)`；`TransferStateEnabled=True` |
| 3 | `em3d_node(target="RackConfigurator.Rail.Crane2", sections="connectors")` | `ports=4 connected=4`：`StoreIn→RollerLiftTable1:Right`、`RetrieveIn/StoreOut→SRM4`、`RetrieveOut→RollerLiftTable2:Right` |
| 4 | `em3d_tree(root="RackConfigurator.Rail.Crane2", maxDepth=4)` | `CraneLift(→LiftVisual, ALH(→Plate1.Plate2))`、`AwaitingRepairSignal(Pling)`、`BlueCraneOnWheelsVisual` |
| 5 | `get_js_script_source("CraneToolkitQS")` | 事件处理器前缀 `Crane1_*`/`Crane_*`/`CraneLift_*`；辅助函数与死代码见 §2.7/2.8 |
| 6 | `get_js_script_source("PalletLoadHandlerQS2")` | `ALH_OnReset/ALH_OnPickup/ALH_OnDropoff` + `CLH_*`；`MovePlate` 取 `sender.Parent.Parent.SpeedZ`（= Crane2.SpeedZ） |
| 7 | `get_visual_script_event_snippets(visual=Crane2)` | 100+ 个可用事件，含 `OnDispatchIn/OnRx*/OnTx*/OnVehicleStatusUpdated/OnReset` 等 |
| 8 | `js_test action=list`（全模型） | 19 脚本 / 110 事件；前缀命中 28、不命中 82；`Crane2` 无任何脚本声明其处理器 |
| 9 | 导出脚本全量检索（`E:\文档\Emulate3D-JS-Export\export-20261001-000133`） | `Crane2` 零命中；`function GetCraneLift` 零命中（但被 `ReturnShuttle` 调用）；`DriveTo/DriveRelease` 仅 `CraneMotorController`/`Vehicle`/`TelescopicLoadHandlerForCrane` 使用 |
| 10 | `em3d_props(target="Group1.RackConfigurator.Rail1.MotorController")` | `Script=CraneMotorController`、`Carriage→Crane1`、`Lift→CraneLift`（A 型对照） |
| 11 | `em3d_props(target="Group1.RackConfigurator.Rail1.Crane1.C")` | `Script=TwinMastCraneVisual`（`C_*` 命中 ✓ 反证规则） |
| 12 | `get_js_script_source("HelpButton")` + `em3d_find("^Cylinder3$")` | 脚本名 `HelpButton` / 视觉名 `Cylinder3` / 处理器 `Cylinder3_OnClick` → **前缀 = 视觉名** |

> 备注：`get_visual_script_event_snippets` 返回的是**可用事件清单**（不是已绑定的处理器），因此它不能直接
> 证明绑定结果；绑定结论来自 §4 的两条反证 + §7 第 8 条的模型级统计。
