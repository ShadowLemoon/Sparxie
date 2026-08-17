# Sparxie

无 GUI 的 Windows 控制台启动器：通过 `Sparxie.LauncherCore` 管理绝区零、原神和崩坏：星穹铁道的多个安装 Profile，由管理员 Broker 与按游戏独立的 SessionHost 承担运行会话。

## 特性

- 三款游戏均以“触屏初始化成功”为启动成功硬条件；
- 原神、星铁可选 FPS 解锁，默认开启 120，支持运行中主目标 FPS 热调；
- 绝区零消费固定版本的私有闭源 Runtime，客户端不访问私库；
- 不识别游戏版本，每次启动按实际结构、特征与原始内容严格校验；
- Running 前 SessionHost 异常终止本次游戏；Running 后三款游戏均保留游戏进程；
- ZZZ 配置损坏或 Host 异常时，走共享恢复例程恢复 PC 文件配置；
- 每次 Launcher 运行持有一个私有 Broker 控制流，控制端退出不主动结束已启动的游戏。

## 控制台用法

从空白配置开始先创建 Profile；`--exe` 必须是完整路径，且 EXE 文件名必须属于对应游戏白名单。创建时不强制该文件已经存在，实际启动仍会在 SessionHost 边界验证。

```text
Sparxie.Launcher.exe profile add --id genshin-cn --name "原神 国服" --game genshin --variant cn --exe "D:\Games\Genshin Impact Game\YuanShen.exe"
Sparxie.Launcher.exe profile list
Sparxie.Launcher.exe profile show genshin-cn
Sparxie.Launcher.exe profile set genshin-cn --target-fps 144 --priority high
Sparxie.Launcher.exe profile select genshin-cn
Sparxie.Launcher.exe profile remove genshin-cn
Sparxie.Launcher.exe launch [profile-id-or-name]
```

`list` 是 `profile list` 的兼容别名。`profile set` 可修改名称、Variant、EXE 路径和适用设置；Hoyo 通用设置为 `--fps`、`--target-fps`、`--background-fps-limit`、`--background-fps`、`--priority`，原神还支持档位和触控 UI 缩放设置。运行 `Sparxie.Launcher.exe help` 可查看完整参数与范围。

`launch` 进入会话后可在同一进程输入 `fps <10-1000>` 热调目标帧率，输入 `quit` 关闭控制端。配置文件继续放在 Launcher 同目录的 `config.json`。

## 仓库结构

```text
Sparxie/
├─ Sparxie.slnx
├─ src/
│  ├─ Sparxie.Launcher/      # 控制台参考宿主
│  ├─ Sparxie.LauncherCore/  # 无界面启动器核心
│  ├─ Sparxie.Broker/        # 管理员 Broker
│  ├─ Sparxie.SessionHost/   # 按参数运行具体游戏会话
│  ├─ Sparxie.Contracts/     # Profile / RPC / 错误契约
│  └─ Sparxie.Infrastructure/
├─ native/
│  └─ HoyoTouchCore/         # Hoyo 上游 subtree + 适配区
├─ runtime/
│  └─ zzz/                   # 构建 staging，不提交私有源码
├─ tests/
├─ build/
│  └─ zzz-runtime.json       # 固定 Runtime 版本、资产名与 SHA-256
├─ docs/
└─ .github/workflows/
```

## 许可

自有组件采用闭源专有许可，详见根目录 `LICENSE`；第三方组件许可与版权声明见 `THIRD-PARTY-NOTICES.md`。

## 状态

实施中（2026-08-17 快照）：

- 已完成：Contracts/配置、LauncherCore、CLI Profile 创建/查看/修改/选择/删除、Broker/SessionHost 闭环、Profile 快照映射、Broker 受控管道握手、私有 Broker 单控制流与收尾、ZZZ 配置恢复与共享恢复例程、滚动文件日志、脱敏诊断包、ZZZ Runtime 清单与 CI、Hoyo subtree 与 C ABI DLL、发布许可证交付和无 GUI 发布审计。
- 待验证：生产 UAC `runas` 人工确认；Hoyo（原神/星铁）真实游戏实机；ZZZ Runtime 的 `build/zzz-runtime.json` 版本/SHA-256；六个正式服实机验收。
- 测试：122 个（LauncherCore 29、Config 26、Zzz 14、Broker 43、HoyoAbi 10）Release 回归；发布包要求包含 Launcher、Broker、SessionHost 与 HoyoTouchCore，且不含旧图形入口、PDB 或凭据痕迹。
