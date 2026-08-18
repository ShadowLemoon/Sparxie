# Sparxie 测试覆盖缺口清单（2026-08-17 审计）

依据启动器核心提取计划逐项审计。
当前测试基线：124 通过（LauncherCore 29 / Config 26 / Zzz 16 / Broker 43 / HoyoAbi 10）。
旧图形入口及其专属测试项目已从解决方案移除，后续新界面另行建立测试边界。

## 已由离线测试覆盖

### LauncherCore 与控制台宿主

- CLI Profile 创建、查看、修改、选择、删除、原子保存与重载；
- Profile ID、显示名、默认选中、回退首项、重名歧义、空配置和不存在项；
- Profile 快照全字段与游戏/优先级枚举映射；
- Broker 受控 `--pipe-name` 参数生成、严格解析和拒绝未知参数；
- `runas` 启动参数构造、连接超时与不通过环境变量传递管道名；
- 会话事件按 SessionId 路由、终态关闭会话流、目标 FPS 范围和交互命令解析；
- 真实 Broker/SessionHost 进程链路中的 Ping、会话启动与确定性失败终态。

### Broker、配置、Runtime

- Broker 单控制流、控制端断连、空闲退出和活动 Host 收尾契约；
- 配置损坏备份恢复、原子保存、目录错误、路径往返和发布凭据审计；
- 固定 ZZZ Runtime 清单的版本、资产名、SHA-256、DLL 清单与 ZIP 发布审计；
- ZZZ 恢复记录、配置读写和 Runtime 清单；
- HoyoTouchCore C ABI 导出与 .NET P/Invoke 冒烟。

## 离线缺口

- 自包含目录直接运行 Launcher 并读取同目录 `config.json`；
- 完整诊断包敏感项审计（`config.invalid-*`、凭据、路径组合）；
- Broker 只处置本次 SessionHost 创建的进程；
- 句柄继承、外部/嵌套 Job、Running 前转换失败等极端进程边界；
- 新界面（如后续恢复）自身的交互、布局、DPI 与触屏覆盖。

## 只能由真实 Runtime 或实机证明

- Hoyo 真实特征扫描、目标唯一性、页属性与原始字节校验；
- Hoyo FPS Patch/触屏载荷/FpsValue 读取与游戏内实际效果；
- 原神档位、后台 FPS、虚拟 DPI 实际效果；
- Running 后 Host 消失：游戏保留、最后 FPS 保留、热调停止；
- ZZZ 真实 DLL ABI、注入、Hook、触点状态、ZZZTouchRelease；
- ZZZ 各配置切换阶段恢复回滚；
- 三款游戏 Running 前/后 Host 强杀语义；
- 临时 Job 对真实游戏启动链与反作弊的影响；
- 六个首版发布阻塞安装（原神/星铁/ZZZ × 国服/国际服正式服）；
- 国服/国际服组合并行验收。

## 已由架构保证（无需单测）

- Profile 快照不可变：快照经 JSON 序列化传给独立 Host 进程（天然副本）；
- Launcher 与私有 Broker 不跨进程重连或接管旧会话；
- 控制端退出不主动结束已进入 Running 的游戏。

## 跟踪方式

- 离线可补项：进入后续迭代待办，按价值排序；
- 实机项：阻塞首版发布，需真实游戏环境逐项验收。
