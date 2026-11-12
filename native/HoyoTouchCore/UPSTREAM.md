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

### PC 界面会话：触屏载荷开关 `touch_ui_enabled`（bootstrap 内）

上游 `Genshin_StarRail_fps_unlocker` 里 Mobile UI 本来就是可选的（`Use_mobile_UI` 默认 0，
`-EnableMobileUI` 参数才打开）；Sparxie 的 bootstrap 把它固定为 1，所以 FPS 解锁只能和触屏界面一起用。
现在 bootstrap 末尾多一个参数 `touch_ui_enabled`（0/1），adapter 由 C ABI 请求结构的追加字段 `touch_ui_disabled` 换算后传入：

- `touch_ui_enabled=1`（缺省）：与之前完全相同；
- `touch_ui_enabled=0`：`Use_mobile_UI=0`。星铁不取 Bootui；原神的 Mobile UI 钩子表保持全零
  （与上游 `main()` 在 `Use_mobile_UI=0` 时的行为一致），其余 FPS Patch 路径不变；
- `touch_ui_enabled=0` 且 `fps_unlock_enabled=0`：没有可安装的内容，bootstrap 返回 1（参数错误），
  adapter 在 `hoyo_create_session` 就以 `HOYO_ERR_CONFIG_INVALID` 拒绝（`touch_ui_disabled=1` 且 `fps_unlock_enabled=0`）；
- 新增导出 `sparxie_hoyo_last_stage()`（bootstrap 当前阶段：2 触屏扫描、3 FPS 扫描、4 Patch、7 引擎模块加载），
  adapter 用它把失败映射到准确的 `HOYO_STAGE_*`（此前一律报触屏扫描阶段）。

ABI：`HoyoLaunchRequest` 末尾（`game_arguments_chars` 之后）追加 `touch_ui_disabled` 与 `reserved0`（x64 由 88 增至 96 字节；字段**取反存放**，
全零 = 既有行为，所以没设置这个字段的新调用方也不会意外关掉触屏）。`hoyo_create_session` 接受 `size >= HOYO_LAUNCH_REQUEST_SIZE_V2`(88) 的请求，
旧调用方缺省 `touch_ui_disabled=0`；`HOYO_ABI_VERSION` 保持 2（只追加字段，没有不兼容修改）。头文件里的 static_assert 固定每个字段的偏移，托管一侧的镜像（`HoyoGameController`）与 `HoyoAbi.Tests` 逐字段断言同样的偏移。Profile/Broker 一侧仍是正向的 `touch_ui_enabled`（缺省 true）。同步风险：上游若重排 Mobile UI 的扫描段，
需同时检查这里的 `want_touch_ui` 分支。

### 触屏载荷补齐（2026-11-09，bootstrap 内）

首版 `sparxie_hoyo_bootstrap` 只复制了 FPS 与 `verfiy` 扫描，漏掉了 main() 里的“Mobile UI”部分，
结果会话能进入 Running 但游戏内没有触屏载荷。现补齐（均位于 bootstrap 内，不动 main() 其余部分）：

- `Use_mobile_UI` 对原神、星铁都置 1（触屏是 Sparxie 的必选能力）；
- 原神：在 il2cpp 扫描成功后，按 main() 的 `if (Use_mobile_UI)` 段扫描并填充
  `GI_Func`（Grph_class / Grph_UIcl_VA / Func_gui_set / Func_input_set / Grph_inputcl_VA /
  Func_getDPI / UI_unhook_time）；任一特征过期（`Use_mobile_UI` 被清零）即整次失败（返回 5）；
- 星铁：挂起进程阶段调用 `Hksr_ENmobile_get_Ptr` 取 `injectarg.Bootui`，取不到即整次失败（返回 5）；
- 去掉原段里的 `wprintf_s`（DLL 宿主没有控制台）。

### 纯触屏（fps_unlock_enabled = 0）不再返回 HOYO_ERR_INSTALL_CONFIRM_FAILED（本地补丁，2026-11-11）

- 现象：preview.9 及 031efe2 的 `sparxie_hoyo_bootstrap` 在关闭帧率解锁时返回 6，会话 Failed，游戏被终止。
- 原因（main.cpp，`inject_patch`）：没有 FPS 指针时，星铁在函数入口 `return 0`（触屏入口 Bootui 不算有效钩子）；原神在无条件的 basefps 块里对地址 0 调
  `ReadProcessMemory`（GetLastError=299）后 `return 0`。AssignProcessToJobObject 本身成功（GetLastError=0）。
- 修改：入口判断把星铁的 Bootui 计为有效；星铁的 FPS 槽位在无指针时指向目标进程内的私有页；原神保留私有页但跳过对游戏帧率读取指令的钩子。
- 验证：VM 上用带特征码的无害假游戏（假 YuanShen.exe 的 il2cpp 节，假 StarRail.exe + GameAssembly.dll + UnityPlayer.dll）、真实 SessionHost（preview.9）：
  未修补 → Failed 6；修补 → Running → Exited。真实游戏里触屏层的表现需要实机确认。
