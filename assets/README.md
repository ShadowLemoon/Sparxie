# 品牌资源

- `sparxie-mark.svg`：应用图标源文件（矢量）。
- `sparxie.ico`：多尺寸图标。**≤32px（16/20/24/32，托盘与任务栏用）使用最简版**：保留帽子、兔耳和头发，去掉五官与红黑色细节，只用 4 种颜色（深紫轮廓 / 白发 / 深紫灰发丝阴影 / 浅肤色脸），不留边距；48/64/128/256 使用完整图（留约 3% 边距）。用于 `Sparxie.Desktop.exe`、壳启动器 `Sparxie.exe`、窗口与托盘。
- `sparxie-64.png`：标题栏左侧用的小图标；`sparxie-256.png`：应用设置“关于”里的大图标（均为完整图）。

重新生成（SVG 改动后），需要 Edge 与 Pillow、numpy，在一个临时目录里操作：

```powershell
# 1) 派生两份简化 SVG（脚本读取当前目录的 sparxie-mark.svg）：smallC.svg=去掉五官，smallH.svg=只留头发
python build/make_icon_small_svg.py C
python build/make_icon_small_svg.py H
# 2) 用 Edge 无头把 sparxie-mark.svg / smallC.svg / smallH.svg 各渲染成透明底 1024 位图
#    （html 里放 <img src="….svg" style="width:1024px;height:1024px;object-fit:contain">）
msedge --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 --window-size=1024,1024 --screenshot=full.png file:///…/view-full.html
#    smallC → small-c.png，smallH → small-h.png，命令同上
# 3) 合成 ICO（同时输出 sparxie-preview.png，用来缩出 sparxie-64.png）
python build/make_icon.py full.png small-c.png small-h.png assets/sparxie.ico
```
