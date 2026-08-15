# RUNTIME NOTICE — 绝区零私有 Runtime

本通知说明随 Sparxie 发布包分发的绝区零（Zenless Zone Zero）私有 Runtime
组件的身份与分发边界。本组件为独立闭源组件，不受根目录 `LICENSE`（Sparxie
专有许可）约束，其版权归属与允许的使用/分发边界以本通知为准。

## 组件

| 项 | 值 |
|---|---|
| 组件名称 | ZZZTouchCore.dll / ZZZTouchFilterHook.dll |
| 组件类型 | 闭源私有 Runtime（触屏注入与配置） |
| 版权归属 | 私有版权持有人（非 Sparxie 仓库作者） |
| 许可证 | 私有；仅允许随 Sparxie 成品 ZIP 分发固定版本成品 |

## 固定版本

以下信息由 `build/zzz-runtime.json` 维护，发布前必须与本通知一致：

| 项 | 值 |
|---|---|
| Runtime 版本 | （待填：runtimeVersion） |
| Release 资产名 | （待填：releaseAsset） |
| SHA-256 | （待填：sha256） |
| 包含文件 | ZZZTouchCore.dll、ZZZTouchFilterHook.dll |

## 使用与分发边界

- 仅分发固定版本成品，不包含、不暗示提供源码；
- 客户端不在运行时访问私有仓库，不包含任何私库凭据；
- Runtime 版本与哈希固定于可审查的构建配置；升级需显式修改版本与哈希并执行回归；
- 哈希不符、缺少任一 DLL 或 ABI 冒烟失败时，CI 直接失败，不落入发布包；
- 根目录 Sparxie 专有许可不改变本组件的独立许可身份。

（本通知为占位草案，版本/哈希字段随用户提供私有 Release 信息后填写。）
