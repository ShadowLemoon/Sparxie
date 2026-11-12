# THIRD-PARTY NOTICES

本文件列出 Sparxie 发布包中包含的第三方组件及其许可证。各组件保留其原始
版权与许可证条款，不受根目录 `LICENSE` 专有许可约束。清单随依赖变动持续更新，
发布前必须复核本文件、`docs/RUNTIME-NOTICE.md` 与 `native/HoyoTouchCore/adapter/licenses/`
均与发布目录实际内容一致。

## .NET 10 Runtime

- 平台：.NET 10，自包含 `win-x64` Runtime
- 许可：MIT（.NET Foundation）
- 来源：https://dotnet.microsoft.com/
- 许可证原文：随 .NET Runtime 分发（dotnet 安装目录 LICENSE.txt）

## gRPC / HTTP2（本地命名管道承载）

- 组件：Grpc.AspNetCore / Grpc.Net.Client 等
- 许可：Apache-2.0
- 来源：https://grpc.io/
- 许可证原文：随 NuGet 包分发（LICENSE 文件），或见 https://www.apache.org/licenses/LICENSE-2.0

## Hoyo 上游（Genshin_StarRail_fps_unlocker subtree）

- 许可：MIT
- 版权：Copyright (c) 2024 NullName
- 来源：https://github.com/winTEuser/Genshin_StarRail_fps_unlocker
- 派生自：https://github.com/34736384/genshin-fps-unlock（MIT）
- 许可证原文：`native/HoyoTouchCore/upstream/LICENSE`（随 subtree 保留）

## inih（INIReader，内嵌于 Hoyo 上游）

- 许可：BSD-3-Clause（New BSD）
- 版权：Copyright (c) 2009, Ben Hoyt
- 来源：https://github.com/benhoyt/inih
- 许可证原文：`native/HoyoTouchCore/adapter/licenses/inih-LICENSE.txt`
  （官方 LICENSE.txt 原文副本，随发布包分发）

## 绝区零私有 Runtime

- 组件：ZZZTouchCore.dll、ZZZTouchFilterHook.dll 及必要配套文件
- 许可：闭源私有，已取得随 Sparxie 成品分发的授权
- 说明：仅分发固定版本成品，不包含源码；版本/哈希/分发边界见
  `docs/RUNTIME-NOTICE.md`，对应 RUNTIME-NOTICE 随发布包分发

## Endfield FPS / 启动行为参考

- 项目：EndField.Fps.UnlockerIsland
- 许可：MIT；原项目另有“for learning and research, no commercial use”说明。
- 来源：https://github.com/isxlan0/EndField.Fps.UnlockerIsland
- 说明：Sparxie 的 Endfield Runtime 使用公开行为重新实现直接启动、目标帧率热调和
  运行时注入流程，没有复制其源文件；若随发布包分发其 MIT 代码或资源，必须同时保留
  原项目的版权与许可证声明。该功能仅用于免费产品，并保留现有注入风险提示。

## Endfield 启动方式（Collapse Launcher Endfield 插件）

- 项目：Hi3Helper.Plugin.Endfield（Collapse Launcher 的 Endfield 插件，含 Hi3Helper.Hypergryph.Core）
- 许可：MIT
- 版权：Copyright (c) 2025 Collapse Launcher
- 来源：https://github.com/misaka10843/Hi3Helper.Plugin.Endfield（提交 1efd304855ed68535d50e11504d790cd7004a3b1，`Hi3Helper.Hypergryph.Core/Management/GameDecoyParentBridge.cs`、`ParentProcessBridge.cs`、`Hi3Helper.Plugin.Endfield/Exports.GameLaunch.cs`）
- 说明：`native/EndfieldCore/endfield_decoy.cpp` 将其启动方法由 C# 移植为 C++：把游戏目录下同签名者的
  `PlatformProcess.exe` 复制为 `Games.exe` 并以挂起状态启动、不恢复，再以它为
  `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` 调用 `CreateProcessW` 创建 `Endfield.exe`；创建前后用
  `WinVerifyTrust` 与签名者证书哈希比对两份映像。移植部分保留上述版权与许可声明，其 MIT 许可全文如下。

```
MIT License

Copyright (c) 2025 Collapse Launcher

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

发布门禁检查项：

- [ ] 根目录 `LICENSE`（专有许可）不覆盖任何第三方组件；
- [ ] `native/HoyoTouchCore/upstream/LICENSE`（Hoyo MIT）随 subtree 保留；
- [ ] `native/HoyoTouchCore/adapter/licenses/inih-LICENSE.txt` 与发布目录一致；
- [ ] `docs/RUNTIME-NOTICE.md` 版本/哈希与 `build/zzz-runtime.json` 一致；
- [ ] 发布目录不含 PDB、调试符号、CI 凭据或私有 Release URL 临时签名；
- [ ] gRPC / .NET 包内要求的许可证与 Notices 完整保留。
