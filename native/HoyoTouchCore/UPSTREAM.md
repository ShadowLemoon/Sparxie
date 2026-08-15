# HoyoTouchCore UPSTREAM 基线

## 上游来源

- 仓库：`winTEuser/Genshin_StarRail_fps_unlocker`
- 许可证：MIT（见 `upstream/LICENSE`）
- 派生自：`34736384/genshin-fps-unlock`（MIT）
- 内嵌组件：`inih/INIReader`（BSD-3-Clause，`upstream/src/inireader.h` 引用 LICENSE.txt，上游未随包提供；官方原文副本放于 `adapter/licenses/inih-LICENSE.txt`，分发时保留 BSD 声明）

## 导入方式

Git subtree --squash 导入，prefix：`native/HoyoTouchCore/upstream`。

```bash
git subtree add --squash --prefix=native/HoyoTouchCore/upstream \
    <上游仓库> HEAD
```

## 当前基线

- 上游提交：`4ba09224c68b8a82ec041517b144ee3287884a8c`
- 导入日期：2026-08-15
- subtree 提交：`git log --oneline --grep="Add.*upstream" -1` 或 `git log --oneline -- native/HoyoTouchCore/upstream | tail -n 1`

## 保持原样约束

- 保留 `.gitattributes` 语义（`*.h`/`*.cpp` 为 UTF-16 + CRLF）；
- 禁止自动格式化、编码转换和无关重排；
- subtree 更新后校验 `git diff` 无整文件编码差异；
- 每次更新后重放并审核本地补丁。

## 本地补丁（首版）

### sparxie_hoyo_bootstrap（追加于 upstream/src/main.cpp 末尾）

- 位置：`upstream/src/main.cpp` 文件末尾追加（保持 UTF-16 BE + CRLF）。
- 内容：`extern "C" __declspec(dllexport) int __stdcall sparxie_hoyo_bootstrap(...)`，
  复用 main() 的注入流程（创建挂起进程 → UnityPlayer/il2cpp 扫描 → inject_patch →
  ResumeThread），不做控制台交互、不读 INI、不处理插件 DLL。
- 全局配置（isGenshin/FpsValue/Target_set_30/60/Custom_DPI_Scale/PowerSave_target/
  GamePriorityClass 等）由参数直接设置，绕开 INI。
- 纯触屏条件：`fps_unlock_enabled=0` 时跳过 FPS Patch 扫描/安装（pfps 扫描、
  星铁 Patch0、原神 il2cpp Pfps 同步地址均跳过），`inject_patch` 以 pfps=0
  仅注入触屏载荷（FPS 槽位写 0，游戏内 FPS 同步不启用）。
  `inject_patch` 入口校验同步放宽为 `!_ptr_fps && !arg->PfuncList` 才失败，
  保证纯触屏（无 FPS 指针、有触屏 Hook 列表）仍能完成注入。
- Sync failed 弹窗与 AutoExit 解耦：bootstrap 内 `AutoExit=1` 仅跳过控制台热键循环，
  不改变上游错误弹窗语义。
- 错误弹窗静默：bootstrap 内 `ErrorMsg_EN=0`（默认 1）。上游 RemoteDll_Inject 等
  失败路径会调 Show_Error_Msg 弹系统窗并阻塞等待点击（WaitForSingleObject 60s），
  Sparxie 错误经 C ABI 返回码上报，禁止弹窗阻塞 SessionHost。
- Job 失效保护：新增 `job_handle` 参数（可空）；CreateProcess 成功后立即
  AssignProcessToJobObject，Job 分配失败时终止进程并整次失败（返回 6）。
  进入 Running 前由 SessionHost 撤销 KILL_ON_JOB_CLOSE，保证 Running 前 Host
  异常退出时游戏进程被终止、Running 后保留。
- 返回码：0=成功，1=参数错误，2=API 初始化失败，3=已运行拒绝，4=创建失败，
  5=扫描失败，6=注入失败（含 Job 分配失败），7=SEH 异常。
- 同步风险：上游 main() 扫描特征或注入时序变化时需同步更新本函数对应段落；
  上游主流程重构时需重新评估。

### CET 兼容性约束（SessionHost 构建配置，非上游补丁）

- 现象：HoyoTouchCore.dll 在 SessionHost（ASP.NET Core/gRPC 进程）加载后调用
  bootstrap 即崩 C0000409（STATUS_STACK_BUFFER_OVERRUN）；在 testhost 探针进程
  正常。
- 根因：.NET 9+ 的 apphost 默认标记 CET（硬件强制 shadow stack）兼容；上游
  `syscall.asm` 的 `asm_syscall` 手写汇编用 `jmp rcx` 跳转 syscall stub 后
  `ret` 返回，CET 下 shadow stack 校验失败触发 #CP → C0000409。
- 修复：`Sparxie.SessionHost.csproj` 设 `<CETCompat>false</CETCompat>` 禁用 CET，
  加载与 bootstrap 恢复正常（假游戏路径返回扫描失败而非崩溃）。上游代码未改动。
- 验证：HoyoAbi 测试（testhost 无 CET）与 HostPipeline 集成测试（真实 SessionHost
  禁用 CET 后走 HoyoGameController → bootstrap → Failed）均通过。
- 同步风险：上游若重写 syscall 汇编使其兼容 CET，可移除该禁用；届时需回归
  SessionHost 加载与 bootstrap 全流程。

### TLS 回调条件编译（NTSYSAPI.h，Sparxie 构建启用）

- 位置：`upstream/src/NTSYSAPI.h` 的 `TLS_CALLBACK`。
- 内容：`SPARXIE_DISABLE_TLS_INIT` 宏下跳过 DLL_PROCESS_ATTACH 时的 `init_API()`
  调用（默认行为不改，仅 Sparxie 构建由 CMake 定义该宏）。
- 原因：TLS 回调在 loader lock 内调 init_API，失败时在 loader lock 内
  ExitProcess，导致 DLL 在 .NET 进程（SessionHost）加载即崩 C0000409。
  init_API 由 `sparxie_hoyo_bootstrap` 显式调用，加载期初始化对 Sparxie 冗余。
- 同步风险：若上游改动 TLS 回调逻辑需重新评估；宏默认关闭不影响上游行为。

以下差异全部放在 subtree 外的 adapter/：

- C ABI 导出入口（`adapter/include/hoyo_touch_core_abi.h`、`adapter/src/hoyo_adapter.cpp`）；
- `EnableFps` 条件与纯触屏分支；
- 触屏失败硬检查（移除上游静默降级）；
- 主目标 FPS 热调（对齐 32 位原子写）；
- 隔离控制台、INI、热键与插件入口（adapter/SessionHost 绕开，不删除上游代码）。

## 后续同步风险

- 上游 `main.cpp` 的入口流程、扫描特征与载荷字段随游戏更新变化；
- 需要时先评估能否在 adapter 内实现，再考虑最小 upstream 补丁；
- 每次上游更新必须跑：构建、离线测试、实机回归（原神国服/国际服、星铁国服/国际服）。
