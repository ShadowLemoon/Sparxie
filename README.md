# Sparxie

统一触屏启动器：在一个普通权限 WPF 界面中管理绝区零、原神和崩坏：星穹铁道的多个安装 Profile，由管理员 Broker 与按游戏独立的 SessionHost 承担运行会话。

## 特性

- 三款游戏均以“触屏初始化成功”为启动成功硬条件；
- 原神、星铁可选 FPS 解锁，默认开启 120，支持运行中主目标 FPS 热调；
- 绝区零消费固定版本的私有闭源 Runtime，客户端不访问私库；
- 不识别游戏版本，每次启动按实际结构、特征与原始内容严格校验；
- Running 前 SessionHost 异常终止本次游戏；Running 后三款游戏均保留游戏进程；
- ZZZ 配置损坏或 Host 异常时，走共享恢复例程恢复 PC 文件配置。

## 仓库结构

```text
Sparxie/
├─ Sparxie.sln
├─ src/
│  ├─ Sparxie.App/          # 普通权限 WPF UI
│  ├─ Sparxie.Broker/       # 管理员 Broker
│  ├─ Sparxie.SessionHost/  # 按参数运行具体游戏会话
│  ├─ Sparxie.Contracts/    # Profile / RPC / 错误契约
│  └─ Sparxie.Infrastructure/
├─ native/
│  └─ HoyoTouchCore/        # Hoyo 上游 subtree + 适配区
├─ runtime/
│  └─ zzz/                  # 构建 staging，不提交私有源码
├─ tests/
├─ build/
│  └─ zzz-runtime.json      # 固定 Runtime 版本、资产名与 SHA-256
├─ docs/
└─ .github/workflows/
```

## 许可

自有组件采用闭源专有许可，详见根目录 `LICENSE`；第三方组件许可与版权声明见 `THIRD-PARTY-NOTICES.md`。

## 状态

实施中：当前完成仓库初始化与解决方案骨架。
