# JsScriptTools — Emulate3D MCP 的 JScript 全生命周期工具箱

为 Emulate3D MCP 补齐 **JScript（JS）脚本的读写与工程化能力**。官方 MCP 对 JScript 只有读
（`get_scripts` 列出脚本名与类型，写只覆盖 C# Project 的 `modify_source_within_project`），
本扩展按 `Demo3D.Core.dll` 内嵌的官方自定义工具规范实现，作为用户脚本程序集被 MCP 服务器扫描注册。

- 环境实测：**Emulate3D Ultimate Edition 2026 / 19.2.0.74**（模型 `QLP TOOL DEV.demo3d`，18 个 JScript）
- 配套文档（均在 `docs/`）：`docs/改造清单.md`（需求映射）、`docs/可行性分析.md`（探测数据）、
  `docs/开发计划.md`（阶段与验收记录）、`docs/Crane2-脚本驱动逻辑.md`（模型侧脚本驱动分析）
- 当前规模：**21 个 MCP 工具**（9 个原始 + 12 个新增），13 个源文件

---

## 1. 规范依据与注册

| 官方约定 | 本工程的实现 |
|---|---|
| 脚本类型必须是 **C# Project** | `JsScriptTools.csproj`（`Import ..\Demo3D.targets`） |
| `.csproj` 必须引用 `ModelContextProtocol` 1.1.0 | `<PackageReference Include="ModelContextProtocol" Version="1.1.0" />` |
| 类标记 **`[McpServerToolType]`**，方法标记 `[McpServerTool]` | `JsScriptTools` 类（partial，多文件） |
| `[Description]` 标注工具与参数 | 全部工具与参数均已标注 |
| 继承 `McpToolBase`，用 `ExecuteMcpCommand(权限, app => …)` | 全部工具方法 |
| 权限标志 `AllowScriptRead` / `AllowScriptModification` | 见 §3 工具表 |
| 需勾选 **Allow Custom Tools** | 见 §2 部署 |

> **注册陷阱（实测）**：类上的 `[McpServerToolType]` 一旦丢失，**编译零报错**、`push` 回显
> `New assembly created`，但 MCP 服务器不注册该类 —— 表现为 JS 系工具全部消失（`qlp_*`/`em3d_*` 仍正常）。
> partial 类只需其中一处标注。

已核实的真实 API（本版本 DLL 元数据）：

- `Document.Resources.Scripts` → `ScriptContainerLibrary`（`Find` / `Create` / `GetAll` / `RunNewScriptsIfNecessary`）
- `ScriptContainer.Source`（**只读**）、`GetNativeSourcesKeys()`、`GetNativeSources(key)`、
  `SetNativeSources(app, key, bytes)`、`LoadedFromDiskPath`、`Load(app, path, infoPath, library)`、`Errors`、`Key`
- `IBuilder.Running`（运行态判据）、`IBuilder.Invoke(Delegate, object[])`（UI 线程编组）、`Document.Visuals`（扁平视觉表）
- 本版本**没有** `McpResult.FromResult/FromError`，工具统一返回 `string`

## 2. 部署与迭代循环

### 2.1 首次部署

1. 把 `JsScriptTools/` 目录复制到 Emulate3D 用户脚本目录（与 `QLToolBox` 同级，保证 `..\Demo3D.targets` 可解析）
2. 在 Emulate3D 里编译该脚本工程（必须编译成功）
3. MCP 服务设置勾选 **Allow Custom Tools**；确认 `AllowScriptRead` / `AllowScriptModification` 为 Allowed

### 2.2 改代码后的加载循环（顺序不可颠倒）

```
改工作区 .cs → read_lints → push-sources.ps1 → 看完整 Result（必须无 Error）
→ Ctrl+S 保存模型 → 关闭并重新打开模型 → 刷新 IDE 侧工具表
→ 检查 0：工具表里有 JS 工具；日志出现 New assembly created for 'JsScriptTools' in cache
```

- **无需退出 Emulate3D**：工具类在**模型打开**时注册（README 早期版本写的"必须完全退出再启动"已过时）
- **顺序陷阱**：先重开、后保存时磁盘模型仍是旧源码，引擎判定 "No updates available" → 不重编译 →
  工具类继续用**旧程序集**（症状：push 成功但行为/文案是上一版）。兜底：完整重启 Emulate3D
- **推送编译失败**：模型内源码已是坏版本 → **立即修好重推，期间绝不保存模型**
- `push-sources.ps1` / `mcp-invoke.ps1` 为本工程自带的部署与调用脚本（`-Project JsScriptTools`）

## 3. 工具表（21 个）

### 3.1 读（原始 3 个）

| 工具 | 参数 | 说明 |
|---|---|---|
| `list_js_scripts` | `nameFilter`、`previewLength` | 列出脚本：名称/类型/长度/native key/预览 |
| `get_js_script_source` | `scriptName` | 读取完整源码 |
| `inspect_js_script` | `scriptName` | 类型、`SourceSetter`、native key、磁盘路径、编译错误* |

### 3.2 写（原始 4 个 + 新增 2 个）

| 工具 | 参数 | 说明 |
|---|---|---|
| `write_js_script` | `scriptName`、`source`、`createIfMissing`、`sourceKey`、`compile` | 创建/覆盖并重编译（无门禁，历史接口） |
| `replace_in_js_script` | `scriptName`、`oldText`、`newText`、`expectedCount` | 片段替换（多槽工程直接拒绝） |
| `append_to_js_script` | `scriptName`、`text` | 追加 |
| `compile_js_scripts` | — | 重跑编译步骤 |
| **`js_write`** | `target`、`source`、`createIfMissing`、`compile`、`dryRun`、`force`、`autoSnapshot`、`verify` | **写入闭环**（见 §4） |
| **`js_set_source`** | `target`、`source`、`createIfMissing`、`compile` | 轻量写入：无门禁/lint/快照，但会打 `lastWrite` 标记并声明"编译未校验" |

### 3.3 版本与诊断（新增 4 个）

| 工具 | 参数 | 说明 |
|---|---|---|
| **`js_snapshot`** | `targets`、`tag`、`listOnly` | 源码快照（进程内），`js_write` 写前自动快照 |
| **`js_diff`** | `from`、`to`、`format` | 行级 diff（快照↔当前 / 快照↔快照），`text`/`json` |
| **`js_rollback`** | `snapshot`、`targets` | 按快照恢复；**脚本已被 Purge 时自动重建** |
| **`js_diag`** | `since`、`kind`、`severityMin`、`limit` | 诊断缓冲（`lastWrite`/`lastCompile`/`all`） |

### 3.4 质量与全景（新增 2 个）

| 工具 | 参数 | 说明 |
|---|---|---|
| **`js_lint`** | `target`、`source`、`rules`、`severityMin` | 四规则静态检查（见 §6），`target` 空 = 全模型 |
| **`js_index`** | `nameRegex`、`limit`、`kind`、`events` | 全模型清单：类型/行数/长度/**rev**/磁盘化/事件前缀 |

### 3.5 搬运与测试（新增 4 个）

| 工具 | 参数 | 说明 |
|---|---|---|
| **`js_export`** | `dir`、`targets`、`dryRun` | 批量导出 `.js` + `js-export.json` + `js-export.md`，逐项字节/rev 对账（见 §7） |
| **`js_import`** | `from`、`targets`、`map`、`dryRun`、`createIfMissing`、`force`、`autoSnapshot`、`verify` | 按清单落位导入，一次 Reset 批量校验 |
| **`js_test`** | `action`(list/scaffold)、`target`、`host`、`event`、`write`、`verify` | 事件清单（含挂载判定）+ 自检脚手架（见 §8） |
| **`js_trace`** | `stack`、`since`、`source`、`limit` | 异常文本 → 脚本/行号映射（见 §8） |

> `rev` 无独立工具：FNV-1a 64 指纹贯穿于 `js_index` 行、快照条目、写入回报与导出清单。
> 定义 = **回读源码**的指纹（不是文件字节的指纹，但实测两者一致：JS 源码无规范化）。

## 4. 写入机制

`ScriptContainer.Source` 在本版本**只读**，因此写入必须绕道。实测结论：**JScript 容器与 CSharp 工程必须走完全不同的通道，混用会直接损坏脚本**。

### 4.1 两条通道（按容器类型分流，不是按序尝试）

| 容器 | 判据 | 通道 |
|---|---|---|
| **JScript** | `PrimaryScriptType == JScript` 且 `GetNativeSourcesKeys()` 为空 | **磁盘 `.js` + `Load()`**：写 `<用户脚本目录>/<name>.js` → `script.Load(...)` → 回读校验「内容一致 **且** 类型仍为 JScript」 |
| **CSharp 工程** | 存在 native 槽位 | §4.2 策略链 |

`via disk .js + Load [路径]` 表示走了 JScript 通道；`via SetNativeSources[...]` 等表示走了策略链。

### 4.2 CSharp 工程策略链（按序尝试）

`Source` 公开 setter → `SetSource/SetText/…` 方法 → `SetNativeSources(app, key, bytes)` →
反射可写 string 属性（排除 `NonSourceMembers`）→ 反射 string 字段 → 磁盘回环 `Load()`。

### 4.3 为什么 JScript 绝不能走策略链

`SetNativeSources` 是 **CSharp 工程专属 API**：对 JScript 容器调用会先把容器**转成 CSharp 工程**
（生成 `.cs` 槽位）**然后才抛异常**；异常被策略链吞掉后继续兜底"成功"写入 —— 类型已坏而工具报成功
（静默失败）。故 JScript 分支失败即返回，绝不 fallthrough。

### 4.4 写入闭环 `js_write`（推荐入口）

```
运行态门禁(REFUSED: the model is Running)
→ 静态 lint（error 阻断，force 可过）
→ 自动快照（S3：编译失败会在保存时被 Purge，快照不可省）
→ 写入（磁盘 .js + Load，统一入口 WriteScriptUnified）
→ 回读校验（内容 + 类型）
→ 编译 + 诊断捕获
→ verify=true 时：Reset + 扫描引擎日志 → 命中 ScriptCompileException 则同调用内回滚
→ 失败 → 回滚（新建则移除；覆盖则快照复原，含 Purge 后重建）
```

**关键边界**：JScript **写入时不编译**（详见 §9 坑 3），因此默认路径会明确回报
`Compile NOT verified`；需要真实编译结论时用 `verify=true`。

## 5. 快照、回滚与诊断

- 快照只存**源码文本**（磁盘 `.js` 每次写入都被覆盖，不能当备份；S1）
- **快照是进程内 static**：推送引起的重编译后仍存活，但**重开模型即清空** —— 重开后第一次写入前建议先 `js_snapshot`
- 持久化形态是 `js_export`（§7）：导出即"当前模型"的可归档形态
- 回滚两条路径：脚本还在 → 磁盘回环重灌；脚本已被 Purge → `lib.Create` + 灌入快照源码
- 诊断缓冲 500 条环形，`since=lastWrite` 可精准框出"某次写入 + 它引发的编译/回滚"

## 6. 静态检查 `js_lint`（四规则 + 0 误报基线）

| 规则 | 级别 | 判据 |
|---|---|---|
| `syntax-error` | **error** | 括号/引号/块注释/正则字面量配平（注释、字符串、正则内容先被置空，避免误判） |
| `unresolved-visual-ref` | **warn** | 语句起始的 `Name.`，既非本脚本声明、也非**全模型**声明、也非场景视觉/命名空间/引擎全局 |
| `event-prefix-without-visual` | warn | `X_OnEvent` 的 `X` 不是场景视觉 → 很可能未挂载（挂载是人工 UI 步骤） |
| `unavailable-api` | **error** | 小写 JS 风格 `Math.xxx`（本引擎只有 .NET PascalCase `Math.Sign/Math.Round`） |

**为什么 ② 只能是 warn**：裸 `Name.` 还可能是类型静态调用（`Transfer.Dispatch(...)`）或宿主自定义属性，
而脚本的宿主不可反射 —— 静态无法判定，工具不许瞎断言。`js_write` 的**写前阻断**由 ①④ 承担，
**运行期正确性**由 `verify=true` 的引擎真值承担。

**校准基线**：18 个真实脚本 = `errors=0`；17 条 warn 全部可解释（③ 的真阳性 + `FC` 跨模型残留全局）。

## 7. 搬运：`js_export` / `js_import`

- 导出目录缺省 `我的文档\Emulate3D-JS-Export\export-<时间戳>`（可 `dir=` 指定），产物：
  `<name>.js` × N + `js-export.json`（含 `key`/`rev`/`chars`/`bytes`/`lines`）+ `js-export.md`
- 每份文件**写后即对账**：读回字节、重新指纹，必须等于模型源码 rev（18/18 实测一致）
- **导出不触发 S2**（只写文件，不调 `Load`）
- 导入落位优先级：`map=` 显式覆盖 → 清单 `key`(GUID) → 唯一名字 → **SKIP + 原因与建议**（绝不静默猜测）
- 编辑检出：文件 rev ≠ 清单 rev → 报 `(file edited after export)`，导入**文件当前内容**
- `dryRun` 默认 **true**；实写前为每个目标建快照（同一个 tag），**一条 `js_rollback(snapshot="import-…")` 整体撤销**
- **导入的 S2 副作用不可逆**：目标会被"磁盘化"（`LoadedFromDisk=False→True`），回滚只复原内容、不回退该标志
- 批量编译校验：`verify=true` 时**一次 Reset** 覆盖整批（实测 `batch=18, failures=0`）

## 8. 测试与取证

- `js_test action=list`：每个事件函数 → 所属脚本 → 其名字前缀是否为场景视觉（能否被触发），并按前缀归组，
  直接产出**人工挂载清单**
- `js_test action=scaffold`：生成自检脚本（仅内置 `print`/纯算术、`[Optimize(false)]`、**无 `Math.*`**），
  默认只打印，`write=true` 才落库，`verify=true` 会用一次 Reset 确认编译
- **不提供 `action=run`**：本引擎无 NUnit 等价物，挂载与 Reset/Play 是人工步骤，工具只做能诚实做到的部分
- `js_trace`：**编译失败可精确定位**（引擎报 `Compiling library 'X': …` + `line L:C:` → 直接给出脚本与源码片段）；
  **运行期成员错误无行号**（`Member 'ceil' not found on object 'Demo3D.Script.TypeLiteral'`）→ 按成员名在
  全模型脚本中检索候选调用点，并声明"最佳努力、原文为准"

## 9. 坑表（全部为本机实测，非推测）

| # | 现象 | 根因 | 处理 |
|---|---|---|---|
| 1 | 工具表里 JS 工具全部消失，编译零报错 | 类上丢了 `[McpServerToolType]` | 保留类级标注（§1） |
| 2 | push 成功但行为/文案是上一版 | **先重开模型、后保存**，引擎判定无更新、不换装 | 严格按 §2.2 顺序；兜底完整重启 |
| 3 | 写入成功但"编译"没结论、坏脚本过一会儿被清掉 | **JScript 只在 Reset / 模型打开时编译**；`script.Errors` 恒空；失败在**保存时被 Purge** | 写前用 `js_lint`；写后用 `js_write(verify=true)`；快照必开 |
| 4 | `verify=true` 报"编译正常"但其实是坏的 | 工具线程直呼 `app.Reset()` → 撞 UI（错误列表网格）抛跨线程异常，Reset 根本没执行 | 已修：`InvokeRequired` 时经 `IBuilder.Invoke` 编组（日志出现 `A cross-thread operation is detected` 即此坑） |
| 5 | `js_diag(since="lastWrite")` 返回 0 条，误以为"很干净" | 标记未设置 + 标记语义排他、off-by-one | 已修：先打标记再记条目；未设置的标记会显式提示 |
| 6 | 新建但未挂载的脚本后来消失了 | **未被任何视觉引用的脚本会在保存/重编译时被自动 Purge** | 这是引擎行为；导入后必须提示人工挂载 |
| 7 | 重开模型后 `js_snapshot(listOnly=true)` 为空 | 快照库是**进程内 static**，随程序集重载清空 | 重开后先快照；需要持久化就 `js_export` |
| 8 | 导入后所有脚本都 `disk=yes`，回滚也退不回来 | 写入通道会"磁盘化"脚本，**S2 副作用不可逆** | 知悉即可；回滚复原内容，标志保留 |
| 9 | 脚本里 `Math.ceil` 报 `Member 'ceil' not found`，但 `Math.Sign` 正常 | 引擎暴露 .NET PascalCase 成员，不暴露小写 JS 风格 | 用 PascalCase 或纯算术；`js_lint` 规则④ 已拦 |
| 10 | 单脚本里 `FC.`/`RM.` 被报"未声明" | **一个模型的所有 JScript 编译进同一程序集**，跨脚本全局合法 | 规则② 已纳入"模型级符号集"；剩余告警明确写"不确定" |
| 11 | `replace_in_js_script` 拒绝多槽工程 | 片段编辑会重写单个槽位、损坏工程 | 用官方 `modify_source_within_project` |
| 12 | `ReadSource` 早期版本拼坏 C# 工程 | fallback 曾拼接所有 native 槽位（`.cs` + `.csproj`） | 已修：fallback 只读主槽位 |

## 10. 验收记录（摘要）

| 阶段 | 验收项 | 结果 |
|---|---|---|
| 一（P0） | A1–A6：旧 9 工具回归、diff 精度、lint 拦截、`verify` 真实校验+回滚、Purge 后重建、运行态 REFUSED、轻量写入 | 全绿（详见 `docs/开发计划.md` §2.5） |
| 二（P1） | B1–B5：rev 幂等、`js_index` 对账（24=18+6）、`js_lint` 0 误报、写前拦截、`js_diag` 语义 | 全绿（§3.5） |
| 三（P2） | C1–C4：导出字节对账 18/18、编辑检出、SKIP+建议、整体撤销 | 全绿（§4.4） |
| 四（P3） | D：事件清单与挂载判定、scaffold 生成与编译、trace 映射 | 见 §4.5 |
| — | 双工具集（QLToolBox）回归 | **按用户要求不做** |

## 11. 备份与版本

| 项 | 值 |
|---|---|
| 源文件 | 13 个：`JsScriptTools.cs`（旧 9 工具 + 类级属性）、`JsScriptHelper.cs`、`JsScriptWrite.cs`、`JsWrite.cs`、`JsSnapshot.cs`、`JsDiag.cs`、`JsLint.cs`、`JsVerify.cs`、`JsIndex.cs`、`JsExport.cs`、`JsImport.cs`、`JsTest.cs`、`JsTrace.cs` |
| 拆分前备份 | 已清理：原 `JsScriptTools.cs.bak-20260930-stage0`（单文件 45 KB，含全部旧逻辑）。旧逻辑已被上面 13 个源文件完全覆盖并通过全阶段验收，2026-10-01 按用户要求删除，Git 首提交亦未包含它 |
| 部署脚本 | `push-sources.ps1`（推送工程源码并触发编译）、`mcp-invoke.ps1`（命令行调用 MCP 工具） |
| 文档 | 根目录 `README.md`（本文件）；`docs/`：`改造清单.md`、`可行性分析.md`、`开发计划.md`、`Crane2-脚本驱动逻辑.md` |
| 对应模型 | `QLP TOOL DEV.demo3d`（18 个 JScript + 6 个 Project） |
