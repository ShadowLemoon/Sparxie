# config.json 有问题时 Sparxie 怎么处理

外部工具也会写 `config.json`。以前任何一个字段不合法，整份文件都会被移走并换成空白配置，界面上的配置全部消失。

## 规则

1. **逐个读取 Profile**，一个坏 Profile 不影响其他的。
   - 超出范围的值改回默认值：帧率（10–1000）、触控 UI 缩放（100–500）、`processPriority`。
   - 类型不对的字段只丢这个字段（`hoyo`、`launch` 内逐个判断）；插件的 `settings` 不是对象时丢掉 `settings`，插件设置的具体取值不检查。
   - 不合法的分辨率改为“不修改”，不合法的附加启动参数清空。
   - 内置游戏的 EXE 不在白名单里时只清空路径，其他设置保留。
   - 缺名称时用 id；绝区零上的 `hoyo` 移除。
   - 修不了的 Profile（不是对象、没有 id、游戏 id 不合法、id 重复、对象里有重复的键）单独移出。旧版第三方构建写的 `"game": "endfield"` 属于这一类；终末地现在由插件提供。
2. **先备份再改。** 有修复或移出时，原始字节先写到 `config.repaired-<utc>-<id>.json`，成功后才写回；备份或写回失败就保留原文件，加载失败。
3. **整份读不了**（不是 JSON、根不是对象、`schemaVersion` 不对、没有 `profiles` 列表）时，原始字节写到 `config.invalid-<utc>-<id>.json`，
   然后在 `config.backup-*`、`config.repaired-*`、`config.invalid-*` 里找最近一份至少有一个可用 Profile 的文件恢复；没有才生成空白配置。
   备份可能比损坏前的内容旧。
4. **合法的空配置不会自动从备份填回**（可能是用户自己删光的）。
5. 做过任何处理都会写进 `logs\desktop.log`，并在主页提示。

逐个读取与备份恢复的做法来自 PR #8（skyhua0224）。

测试：`tests/Config.Tests/ConfigSurvivalTests.cs`。
