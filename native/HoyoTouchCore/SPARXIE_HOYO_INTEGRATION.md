# Hoyo（原神/星铁）上游流程接入方案

状态：**bootstrap 已实现并导出（sparxie_hoyo_bootstrap），adapter 已接入调用，DLL 构建验证通过**。剩余为 SessionHost 进程创建协调与实机验收。

## 目标

让 `HoyoTouchCore.dll` 的 `hoyo_launch(session, game_pid, result)` 真正执行上游扫描/Patch/注入，使原神/星铁会话进入 Running。失败路径由运行时能力校验捕获（特征缺失/地址非法/写入失败 → 整次失败），不提供绕过。

## 约束（来自计划）

- 尽量少改上游；只有无法在 subtree 外实现的入口条件、扫描条件和载荷字段才修改上游区；
- 禁止把旧 `main.cpp` 当作 DLL 导出函数直接包装（`hoyo_adapter.cpp` 已是独立 C ABI 层，满足）；
- 禁止在远程载荷中引用 SessionHost 内 `&FpsValue`（FpsValue 由 adapter 持有，见下文）；
- Sync failed 弹窗屏蔽必须与 `AutoExit` 解耦（见下）；
- 每次上游更新后重放并审核补丁，记录于 `UPSTREAM.md`。

## 上游现状（4ba0922）

`upstream/src/main.cpp` 是 2527 行单体：`main()` 内联执行 控制台初始化 → init_API → LoadConfig(INI) → 游戏检测 → CreateProcess(挂起) → 注入 UnityPlayer/UserAssembly → 特征扫描 → inject_patch → ResumeThread → 热键循环。

关键事实：
- `Show_Error_Msg` 在 `ErrorMsg_EN == 0` 时静默（不弹窗）——错误路径可复用而不打扰；
- `init_API()`、`RemoteDll_Inject`、`Get_Section_info`、`PatternScan_Region`、`PatternScanRegionEx`、`inject_patch`、`ReadProcessMemoryInternal`、`WriteProcessMemoryInternal` 均为文件内 static，可在 main.cpp 内新增函数中复用；
- 全部注入核心依赖 `pi->hProcess`（已创建挂起进程的句柄），与 CreateProcess 解耦点明确。

## 补丁方案（待实施，需实机验证）

在 `upstream/src/main.cpp` 末尾追加导出入口（UTF-16 BE + CRLF 保持原样）：

```cpp
// Sparxie adapter 入口：对已创建的游戏进程执行上游扫描/Patch/注入。
// 不启动控制台、不读 INI、不进入热键循环；错误由 ErrorMsg_EN=0 静默并返回结果码。
extern "C" __declspec(dllexport) int sparxie_hoyo_run(
    HANDLE game_process,      // 已挂起创建的游戏进程句柄
    const wchar_t* game_dir,  // 游戏安装目录（ProcessDir）
    int genshin,              // 1 = 原神，0 = 星铁
    int fps_value,            // 主目标 FPS（10-1000）
    int enable_fps            // 0 = 纯触屏（跳过 FPS Patch）
)
```

函数体从 main() 复制注入核心段（原 2014-2447 行：模块加载、特征扫描、inject_patch、ResumeThread），改动点：

1. 开头设置全局：`isGenshin = genshin; FpsValue = fps_value; ErrorMsg_EN = 0; AutoExit = 1; Use_mobile_UI = 1;`
2. 用 `OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid)` 或直接接收句柄替代 `CreateProcess` 段；
3. `pi->hProcess` → 传入句柄；`pi->hThread` → 由 `OpenThread` 获取或调用方传入；
4. `barg.Path_Lib` 插件段删除（Sparxie 不支持插件 DLL）；
5. 热键循环不复制（热调走 `hoyo_set_target_fps` → adapter 内 `g_fps_value`）；
6. 失败路径：`return` 稳定结果码（沿用 InjectResult 枚举语义），`Show_Error_Msg` 因 `ErrorMsg_EN=0` 自动静默；
7. 纯触屏（enable_fps=0）：跳过 FPS 特征扫描与 FPS Patch，只做触屏相关扫描/Patch（上游当前无独立触屏 Patch，此条件需实机确认上游触屏字段是否随 FPS 流程一并安装——见风险）。

## 纯触屏条件

计划要求"FPS 关闭时不扫描、不安装 FPS Patch"。上游当前 `isGenshin` 分支同时扫描触屏（UnityWndclass/UI）与 FPS 指针。需确认：
- 若触屏 Patch 与 FPS Patch 在同一 `inject_patch` 内（是，上游 `inject_patch` 同时处理），则 enable_fps=0 时仍调用 `inject_patch` 但跳过 FPS 目标写入（`_ptr_fps` 传空或跳过 `Base_fps` 段）；
- 具体跳过点需实机对照：`inject_patch` 中 `if (1)//basefps` 段是 FPS Patch 核心，纯触屏时跳过该段。

## FpsValue 同步与热调

- adapter 内 `g_fps_value`（`std::atomic<int32_t>`）即计划要求的"地址稳定、覆盖整个会话的主目标 FpsValue"；
- `hoyo_set_target_fps` 以原子写更新它；`sparxie_hoyo_run` 把 `&g_fps_value` 传入 `inject_patch` 的 `_ptr_fps` 参数；
- 游戏载荷沿用上游协议从 Host 进程读取该地址（属于 Runtime 内部实现，不进入公共 ABI）；
- SessionHost 死亡后，载荷读取失败走上游保底分支（保留最后 FPS），不触发回滚/终止。

## Sync failed 弹窗屏蔽

上游 `inject_patch` 生成的载荷在读取失败时调用 `MessageBoxA`（`_sc_buffer + 0x80` 写入 `&MessageBoxA`）。屏蔽方式（与 AutoExit 解耦）：

- 在 `sparxie_hoyo_run` 复制段中，把 `*(uint64_t*)(_sc_buffer + 0x80) = (uint64_t)(&MessageBoxA);` 替换为指向一个 no-op 桩函数（如 `sparxie_sync_failed_stub`）；
- 不设置上游 `AutoExit`（保持 0 语义：不因同步失败退出），只禁用弹窗；
- 保底分支（保持最后成功值）不动。

## 验证前提（阻塞）

1. 本机/CI 有真实原神或星铁安装（国服或国际服任一）；
2. 运行 `hoyo_launch` 冒烟：扫描成功 → Patch 成功 → 游戏内 FPS 生效/触屏生效；
3. 验证纯触屏条件（enable_fps=0）不安装 FPS Patch；
4. 验证 Host 崩溃后游戏保留最后 FPS 且无 Sync failed 弹窗；
5. 原神/星铁各国服/国际服完整实机验收（计划发布阻塞项）。

在具备上述环境前，保持 `hoyo_launch` 返回 `HOYO_ERR_NOT_SUPPORTED`，避免误报能力。
