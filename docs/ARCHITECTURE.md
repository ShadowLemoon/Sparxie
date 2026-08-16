# Sparxie 架构

无 GUI 的控制台启动器核心 + 管理员 Broker + 按游戏独立的 SessionHost。三款游戏（绝区零、原神、崩坏：星穹铁道）均以“触屏初始化成功”为启动成功硬条件。

## 进程拓扑

```text
普通权限 Sparxie.Launcher
        │
        │ Sparxie.LauncherCore：配置选择、Broker 控制流、事件路由
        ▼
管理员 Sparxie.Broker（一次 UAC 启动）
        │
        ├─ 绝区零 SessionHost ──▶ ZZZTouchCore.dll / ZZZTouchFilterHook.dll（私有 Runtime）
        ├─ 原神   SessionHost ──▶ HoyoTouchCore.dll（上游 subtree + adapter 构建）
        └─ 星铁   SessionHost ──▶ HoyoTouchCore.dll
```

- Launcher 不提升权限、不直接创建游戏进程、不加载 native Runtime；
- Broker 只暴露固定 RPC，为获准请求启动 SessionHost；
- 每个 Launcher 私有 Broker 只接受一个控制事件流，控制端断开后按活动 Host 状态收尾；
- SessionHost 持有不可变 Profile 快照，是游戏级命名互斥锁的权威所有者；
- Launcher 退出不结束已启动游戏；Broker 退出/崩溃不结束既有 Host 与游戏。

## 会话生命周期

1. LauncherCore 生成不可变 Profile 快照 → gRPC 请求 Broker；
2. Broker 重新验证路径/Variant/设置范围，检查同款游戏互斥与已运行游戏；
3. 启动对应 SessionHost（管理员）并转发快照；
4. SessionHost 创建游戏进程 → 调用 native Runtime 完成触屏初始化；
5. 全部成功 → 宣布 Running；失败 → 整次失败并处置本次进程；
6. Running 前 Host 异常：临时 Job/监督机制终止本次游戏进程；ZZZ 走共享恢复例程恢复 PC 配置；
7. Running 后 Host 异常：三款游戏均保留游戏进程，热调丢失但不重连；
8. 控制端断开后，Broker 继续监控活动 Host，全部会话结束后退出。

## 代码布局

```text
src/
├─ Sparxie.Launcher/      # 控制台参考宿主（list、launch、fps、quit）
├─ Sparxie.LauncherCore/  # 无界面启动器核心（选择、快照、RPC、事件、会话句柄）
├─ Sparxie.Broker/        # 管理员 Broker（RPC 入口、Host 启动、异常监控）
├─ Sparxie.SessionHost/   # 按游戏运行会话（互斥、Job、Runtime 调用、清理）
├─ Sparxie.Contracts/     # Profile / RPC / 错误契约
└─ Sparxie.Infrastructure/ # 配置存储、日志、诊断、ZZZ 恢复、进程检测
native/
└─ HoyoTouchCore/
   ├─ upstream/            # Hoyo 上游 subtree（MIT，保留原结构与编码，最小补丁）
   ├─ adapter/             # C ABI、配置转换、bootstrap 对接（subtree 外）
   └─ UPSTREAM.md          # 上游补丁记录
runtime/zzz/               # ZZZ Runtime 构建 staging（不提交私有源码）
build/zzz-runtime.json     # 固定 Runtime 版本、资产名与 SHA-256
docs/                      # 架构、第三方许可证
```

## 关键机制

- **配置**：`config.json` 与 Launcher 同目录，原子保存；损坏时按原始字节备份为
  `config.invalid-<UTC>-<随机>.json` 并生成空白 v1 配置，CLI 输出提醒；
- **Profile 选择**：优先按 ID，再按显示名精确匹配；重名、空配置和不存在项明确失败；
- **互斥**：同款游戏（含所有 Variant/Profile）共享游戏级命名互斥域，由 SessionHost 持有；
- **Running 前失效保护**：Host 独占临时 Job（KILL_ON_JOB_CLOSE），Running 前完成
  可验证的撤销/转换，不覆盖整个 Running 生命周期；
- **ZZZ 恢复**：修改 `GENERAL_DATA.bin` 前原子写入 `recovery/zzz/` 会话恢复记录与
  原始备份；正常清理与异常接管共用同一恢复例程；
- **Hoyo 接入**：上游入口补丁复用扫描/inject/ResumeThread 流程，adapter 以 C ABI
  封装为会话式接口；`FpsValue` 为 Host 内稳定变量，热调经 C ABI 原子写更新；
- **日志与诊断**：`logs/` 结构化滚动日志保留 7 天；诊断包只含脱敏日志与非敏感
  版本/契约信息，不含 config、备份、凭据与未脱敏完整用户路径。

## 当前状态与阻塞点

- 已完成：Contracts/配置、LauncherCore、Launcher CLI、Broker/SessionHost 闭环、ZZZ 配置恢复与
  Running 前失效保护、Hoyo subtree 与 C ABI、Broker 单控制流和断连收尾、发布许可证交付、无 GUI 发布链；
  105 项离线测试目标覆盖核心、配置、Broker、ZZZ 和 native ABI。
- 阻塞项：
  1. `build/zzz-runtime.json` 的 runtimeVersion/releaseAsset/sha256 待用户提供私有 Release 信息；
  2. 生产 UAC 确认以及原神/星铁/绝区零真实游戏环境的注入、Running 前后强杀 Host 等实机验证。

## 发布

- 自包含 `win-x64` 便携 ZIP；`config.json` 跟随 Launcher；
- CI 先构建 `HoyoTouchCore.dll`，再构建和发布 Launcher、Broker、SessionHost；
- 发布 staging 从干净目录产生，不含旧 App、调试符号、CI 凭据或私库痕迹；
- 许可证：根 LICENSE 覆盖自有代码（闭源专有）；THIRD-PARTY-NOTICES.md 与
  `adapter/licenses/` 覆盖 Hoyo(MIT)、inih(BSD-3-Clause)、gRPC(Apache-2.0)、.NET(MIT)；
  ZZZ Runtime 独立 RUNTIME-NOTICE。
