# 实用工具箱

一个纯原生 Windows 桌面工具箱，把「卸载软件」「管理右键菜单」「快捷键截屏」三件事收进同一个窗口，并给截图配了一套可用的放大预览（文字识别、按词拖选、涂鸦标注）。

- 技术栈：C# / .NET 10 / Windows Forms，全自绘控件，无 WebView、无第三方 UI 框架
- 目标框架：`net10.0-windows10.0.19041.0`（为调用系统内置 OCR）
- 运行平台：Windows 10 / 11，x64
- 版本：`1.0.0`，程序集名 `Toolbox`，产品名「实用工具箱」
- 数据：全部留在本地，不联网、不上传；配置写在 `%LOCALAPPDATA%\Toolbox\settings.ini`

---

## 一、快速开始

根目录有两个可直接双击运行的单文件 exe：

| 文件 | 体积 | 说明 |
| --- | --- | --- |
| `实用工具箱.exe` | 约 55 MB | **自带 .NET 10 运行时**（self-contained + 单文件压缩），拷到任何 Windows x64 机器都能跑，推荐日常使用 |
| `实用工具箱 轻量版.exe` | 约 26 MB | 不含运行时（framework-dependent 单文件），需要系统已安装 **.NET 10 桌面运行时** |

> 两个版本功能完全一致，只是运行时打包方式不同。体积比一般 WinForms 程序大，是因为系统 OCR 需要把 Windows SDK 投影程序集（`Microsoft.Windows.SDK.NET.dll`，约 20 MB）打进单文件。

- **双击**：打开主窗口（即使设置里勾了「开机自启时直接驻留托盘」，手动双击也一律显示窗口）
- **再次双击**：程序是单实例，会把已运行的窗口从托盘/后台唤回前台，不会开第二个
- **管理员权限**：只有安装/卸载系统级右键菜单（写 HKLM）时需要；设置里会自动弹 UAC 提权

---

## 二、功能

### 1. 软件卸载

- 扫描 4 个注册表卸载清单：`HKLM` / `HKCU` × `Uninstall` / `WOW6432Node`，自动排除系统组件、更新补丁、父子重复项
- 按名称 / 发布者 / 版本 / 安装路径实时搜索，支持排序、筛选、显示系统组件开关
- 多选（Ctrl 点选、Shift 连选、空格勾选、Ctrl+A 全选）、批量卸载
- 识别 `MSI` / `NSIS` / `InnoSetup` / 通用卸载器，自动推导静默参数；没有静默参数的会打开原卸载向导并给出提示
- 交互式卸载与静默卸载双入口，卸载后检查残留（注册表项 / 安装目录），清理统一送**回收站**
- 右键菜单集成：在文件、快捷方式、文件夹上右键 →「快速卸载软件」/「卸载此软件」，会把 `.lnk` 目标或 exe 路径匹配回软件记录

### 2. 右键菜单

- 浏览 9 个挂载点：所有文件、文件夹、桌面/空白处、磁盘驱动器、任意文件夹、文件与文件夹、快捷方式、可执行文件、此电脑
- 支持：启用 / 禁用、删除并备份、恢复备份、新增自定义菜单项、注册表定位、按状态筛选（全部 / 已启用 / 已禁用 / 本工具）
- 「安装本工具菜单」会把菜单写入 5 个位置：桌面与文件夹空白处、文件夹、此电脑、快捷方式（`.lnk`）、可执行文件（`.exe`）；卸载时一并移除
- Windows 11 默认收起传统菜单，右键后点「显示更多选项」即可看到

### 3. 快捷键截屏

- 全局热键，默认 `Ctrl + Alt + A`（使用 `MOD_NOREPEAT`，长按不会连拍），在截屏页的输入框里按下组合键即可改
- 两种模式：框选截图（可拖边角调整、拖内部移动）与全屏截图
- 结果保存为 PNG，默认目录 `图片\实用工具箱 截图`；旧版 `Toolbox 截图` 会自动迁移，不会丢历史
- 可选自动复制到剪贴板；历史列表支持勾选批量删除、清空、刷新，删除送回收站
- 历史保留上限默认 10 张（可配 `shot.keep`，范围 1–200）。**只在保存新截图之后**才按上限收敛，且清理送回收站可还原；启动程序不会动历史文件
- 截图覆盖层内可直接涂鸦：点选区下方的「画笔」按钮弹出颜色/粗细工具条，画完按 Enter 保存，涂鸦会烘焙进最终 PNG

### 4. 截图放大预览

从历史列表单击预览图、双击列表行、点「放大预览」或右键菜单进入。

- **查看**：滚轮以光标为锚点缩放（5%–1600%）、拖动平移、适应窗口 / 1:1、`←` `→` 翻页、最大化、上一张/下一张
- **文字识别**：用 Windows 系统内置 `Windows.Media.Ocr`，离线、无需密钥、无需联网。结果按行显示在右侧，中文不会拼成「活 动 日 志」
- **拖选文字**：在图片文字上按住拖动，按识别出的词显示蓝色高亮；**松手只选定，不碰剪贴板**，用 `Ctrl+C` 或右键菜单复制；右侧「复制文字」按钮始终复制全文
- **涂鸦**：按 `D` 或底部「涂鸦」按钮进入，画布顶部出现工具条——6 种颜色、3 档粗细、撤销、清空、保存副本、复制图片、保存、退出
  - **保存副本**：生成 `原名_涂鸦_时间.png`，立刻刷新并出现在主窗口历史记录里，不覆盖原图
  - **保存**：把涂鸦烘焙后**覆盖原文件**；保存后画布接管结果、清空笔画层，不会重复叠加
  - **复制图片**：把涂鸦后的整张图放进剪贴板
- **其它**：复制原图、用系统默认程序打开、打开所在文件夹（资源管理器会选中当前图片）、显示/隐藏文字面板

---

## 三、命令行参数

| 参数 | 作用 |
| --- | --- |
| （无参数） | 启动主窗口；已有实例时唤起它 |
| `--target=<路径>` / `--target <路径>` / 直接传一个存在的路径 | 右键菜单传入，定位到对应软件；带 target 时允许启动新实例 |
| `--install-menu [--silent]` | 安装本工具右键菜单（需管理员） |
| `--uninstall-menu [--silent]` | 卸载本工具右键菜单（需管理员） |
| `--silent` | 配合上面两条，不弹结果对话框，只写日志 |
| `--tray` | 开机自启标记；仅当 `app.startMinimized=1` 时才驻留托盘 |
| `--elevated-install` | 提权重启后自动装菜单，装完继续正常打开界面 |
| `--selftest` | 无界面自检：注册表扫描、卸载命令推导、菜单增删改链路、窗体布局；结果写 `%TEMP%\Toolbox-selftest.txt` |
| `--ocrtest [图片.png]` | 无界面 OCR 自检（引擎语言、行数、词数、识别文本）；结果写 `%TEMP%\Toolbox-ocrtest.txt` |
| `--viewer [图片.png]` | 直接打开大图预览窗口，不启动主界面 |

诊断与日志位置：

- `%LOCALAPPDATA%\Toolbox\crash.log` — 未处理异常（超过 20 KB 会截断重写）
- `%TEMP%\Toolbox-selftest.txt`、`Toolbox-ocrtest.txt`、`Toolbox-menu.log`、`Toolbox-error.log`、`Toolbox-crash.txt`

---

## 四、配置项

配置文件：`%LOCALAPPDATA%\Toolbox\settings.ini`，每行 `key=value`，UTF-8，改完下次启动生效。

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `theme` | `light` | `light` / `dark` / `auto`（跟随系统） |
| `nav.collapsed` | `0` | 左侧导航是否收起 |
| `shot.folder` | `图片\实用工具箱 截图` | 截图保存目录 |
| `shot.enabled` | `1` | 是否启用全局截屏热键 |
| `shot.fullscreen` | `0` | `1` = 全屏截图（跳过框选） |
| `shot.clipboard` | `1` | 截图后自动复制到剪贴板 |
| `shot.hotkey` | `Ctrl + Alt + A` | 全局热键，例如 `Alt + S` |
| `shot.keep` | `10` | 历史保留上限，1–200 |
| `app.trayOnMinimize` | `1` | 最小化时隐藏到托盘 |
| `app.trayOnClose` | `0` | 点关闭时缩到托盘而不退出 |
| `app.startMinimized` | `0` | 开机自启（`--tray`）时直接驻留托盘；**不影响手动双击** |
| `app.trayHint` | `0` | 首次缩到托盘的气泡提示是否已显示 |

开机自启写在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名 `Toolbox`，命令带 `--tray`。

---

## 五、快捷键

### 主窗口（仅「软件卸载」页生效）

| 键 | 作用 |
| --- | --- |
| `F5` | 刷新软件列表 |
| `Ctrl+F` / `/` | 聚焦搜索框 |
| `Ctrl+A` | 全选 |
| `Esc` | 清空搜索；搜索为空时关闭窗口 |
| `Delete` | 交互式卸载所选 |
| 双击行 | 卸载该软件 |
| Ctrl / Shift / 空格 / 上下 / Home / End | 列表多选与导航 |

### 截图框选覆盖层

| 键 | 作用 |
| --- | --- |
| 拖动 | 框选；松开后可拖边角调整、拖内部移动 |
| 点击选区内 / `Enter` / `Space` /「✓ 保存」 | 保存截图 |
| `Esc` / 右键 | 取消 |
| `R` | 重新框选 |
| 方向键（Shift 加速） | 微调选区位置 |
| `D` 或「画笔」按钮 | 切换涂鸦模式 |
| `Ctrl+Z` | 撤销上一笔 |

### 放大预览

| 键 | 作用 |
| --- | --- |
| 滚轮 | 以光标为锚点缩放 |
| 空白处拖动 / 中键拖动 | 平移图片 |
| `←` `→` | 上一张 / 下一张 |
| `+` `-` | 放大 / 缩小；`F` 适应窗口；`1` 原始尺寸 |
| `Ctrl+C` | 复制选中的文字；无选中时复制全文（焦点在右侧文本框时由文本框自己处理选中内容） |
| `D` | 进入 / 退出涂鸦 |
| `Ctrl+Z` | 涂鸦模式下撤销上一笔 |
| `Ctrl+S` | 涂鸦模式下保存副本（进历史记录） |
| `Ctrl+Shift+S` | 涂鸦模式下覆盖保存到原文件 |
| `Esc` | 涂鸦模式下先退出涂鸦；否则关闭窗口 |

---

## 六、从源码构建

环境要求：Windows 10/11 x64、**.NET 10 SDK**、`Microsoft.Windows.SDK.NET.Ref 10.0.19041.57`（SDK 会自动还原）。

### 一键构建

双击根目录的 `重新编译.bat`，会依次产出两个 exe 并覆盖根目录同名文件，临时 `publish` / `publish_full` 目录会自动清理。

> 该 .bat 是 **GBK（936）编码**，用编辑器修改时请保持编码，否则中文提示会乱码。

### 手动构建

```bat
dotnet publish "源码\Toolbox.csproj" -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:DebugType=None -p:NuGetAudit=false -o publish --nologo -v q
copy /Y publish\Toolbox.exe "实用工具箱 轻量版.exe"

dotnet publish "源码\Toolbox.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None -p:NuGetAudit=false -o publish_full --nologo -v q
copy /Y publish_full\Toolbox.exe "实用工具箱.exe"
```

说明：

- `EnableCompressionInSingleFile` 只对 self-contained 有效，所以完整版压缩、轻量版不压缩
- `-p:NuGetAudit=false` 避免离线环境拉不到 NuGet 漏洞库时刷 `NU1900` 警告
- 覆盖同名 exe 后，右键菜单注册的命令路径不变，无需重新安装
- 构建前先关闭正在运行的程序，否则 exe 被占用会复制失败

---

## 七、目录结构

```
├─ 实用工具箱.exe              # 完整版（自带运行时）
├─ 实用工具箱 轻量版.exe       # 轻量版（需 .NET 10 桌面运行时）
├─ 重新编译.bat                # 一键构建（GBK）
├─ README.md
├─ ShellExtNative/             # 保留资产：Win11 一级菜单 COM 扩展（当前未启用）
│  ├─ UninstallCommand.cpp
│  ├─ exports.def
│  ├─ build.ps1
│  └─ ToolboxShellNative.dll
└─ 源码/
   ├─ Toolbox.csproj
   ├─ app.ico
   ├─ Program.cs               # 入口、单实例、命令行、自检、崩溃日志、Win32 互操作
   ├─ Core/
   │  ├─ Models.cs             # 数据模型（软件记录、卸载计划/结果）
   │  ├─ RegistryScanner.cs    # 四个卸载清单扫描
   │  ├─ CommandLine.cs        # 卸载字符串解析
   │  ├─ UninstallEngine.cs    # 卸载命令推导、执行、残留检测
   │  ├─ ShortcutResolver.cs   # .lnk / exe / 目录 → 软件记录匹配
   │  ├─ ContextMenuRegistry.cs# 右键菜单扫描、增删改、备份恢复、本工具菜单
   │  ├─ StartupRegistry.cs    # HKCU Run 开机自启
   │  ├─ RecycleBin.cs         # SHFileOperation + AllowUndo 统一回收站
   │  └─ OcrService.cs         # Windows.Media.Ocr 封装、词坐标、中文空格清理
   └─ UI/
      ├─ Theme.cs              # 调色板、DPI 缩放、绘制原语、settings.ini 读写
      ├─ Controls.cs / Glyph.cs / ColumnLayout.cs / ResizeController.cs / IconLoader.cs
      ├─ MainForm.cs           # 工具箱外壳、导航、托盘、主题切换、快捷键
      ├─ SoftwareList.cs / ActionDialog.cs / SettingsDialog.cs / AddMenuDialog.cs
      ├─ ContextMenuView.cs / ContextMenuList.cs
      ├─ HotkeyBox.cs          # 全局热键录入
      ├─ ScreenshotService.cs  # 热键注册、抓屏、保存、剪贴板、历史与容量管理
      ├─ ScreenshotView.cs     # 截屏工具页、历史列表
      ├─ CaptureOverlay.cs     # 全屏框选覆盖层 + 涂鸦
      ├─ ImageLoader.cs        # 内存解码 + 异步加载版本号去重
      ├─ ImageViewerForm.cs    # 放大预览、文字拖选、涂鸦、保存
      ├─ Doodle.cs             # 共用涂鸦：笔画图层 + 浮动工具条
      └─ AppPrefs.cs           # 界面行为偏好键
```

---

## 八、设计与实现要点

- **界面**：`BufferedPanel` / `ModernButton` / `ModernToggle` / `GlyphPainter` 全部自绘；`Theme` 提供亮色/深色/自动三套调色板，所有尺寸经 `Theme.S()` 按系统 DPI 缩放，切换主题时全局重绘
- **单实例**：`Local\Toolbox.SingleInstance` 互斥体 + `WM_APP+21` 自定义消息，把托盘里的窗口唤回前台
- **全局热键**：`RegisterHotKey` 带 `MOD_NOREPEAT`；窗口句柄重建时自动 `Rebind`，避免热键失效
- **图片解码**：`ImageLoader` 先把文件读进内存再解码，解除文件句柄占用（否则删除历史会失败）；`AsyncImageLoader` 用版本号丢弃过期结果，快速翻图不会贴错图
- **OCR**：`Windows.Media.Ocr` + CsWinRT；用 `InMemoryRandomAccessStream` 绕开桌面程序的路径授权，`IAsyncOperation` 统一 `AsTask()`；按行取文本并清理 CJK 词间空格；`OcrWord.BoundingRect` 提供词坐标给拖选高亮
- **涂鸦复用**：`DoodleLayer` 把笔画存成宿主坐标系（查看器=原图、覆盖层=全屏），绘制时传坐标映射、保存时传偏移烘焙；`DoodleBar` 按 `DoodleAction` 组合按钮，两处共用一套实现
- **数据安全**：删除文件统一走 `RecycleBin.Send`（`AllowUndo`）；启动程序不清理历史；容量收敛只在保存新截图后执行且送回收站

---

## 九、常见问题与限制

**OCR 提示不可用 / 识别为空**
系统未安装 OCR 语言包。到「Windows 设置 → 时间和语言 → 语言和区域」添加中文语言包后重试；可用 `--ocrtest` 自检。识别质量取决于 Windows 自带引擎：印刷体中文/英文可用，手写体、艺术字、极小字会有错字。

**右键菜单看不到「快速卸载软件」**
Windows 11 把传统菜单收进「显示更多选项」；另外系统级挂载点需要管理员权限安装。设置面板的开关会显示当前已安装的位置数量。

**轻量版打不开**
需要安装 .NET 10 桌面运行时；不确定就用完整版 `实用工具箱.exe`。

**为什么 exe 有几十 MB**
系统 OCR 依赖的 Windows SDK 投影程序集会被整包打进单文件；完整版已启用单文件压缩（未压缩约 130 MB）。

**历史截图被自动清理了？**
默认上限 `shot.keep=10`，可在 `settings.ini` 调大（最大 200）。清理送回收站，可从回收站还原；启动程序本身不会清理。

**「保存」会覆盖原图**
涂鸦工具条里的「保存」是覆盖原文件，不可撤销；想保留原图请用「保存副本」。截图覆盖层里画的涂鸦会随截图一起保存，不会另存一份。

**Win11 一级菜单扩展**
`ShellExtNative/` 是早期尝试的 COM Shell Extension 源码与 DLL，因 Explorer 加载失败已回退到传统菜单方式，代码保留但 `UseModernMenuItem = false`，当前不启用。

---

## 十、自检

```bat
实用工具箱.exe --selftest
实用工具箱.exe --ocrtest "C:\path\to\shot.png"
实用工具箱.exe --viewer "C:\path\to\shot.png"
```

`--selftest` 会验证注册表扫描、卸载命令推导、菜单新增/禁用/删除备份/恢复全链路（自动清理自建测试项）以及窗体布局，过程中会**短暂构造并显示一次主窗体**做布局检查，随后退出；`--ocrtest` 输出 OCR 语言、行数、词数与识别文本。两者都不启动常驻界面，也不写截图历史。

---

## 十一、声明

本工具会读写注册表、调用第三方卸载程序、抓屏并保存图片。卸载软件、删除右键菜单、涂鸦「保存」覆盖原文件等操作均不可撤销（删除类操作会送回收站），使用前请自行确认目标。项目为本地工具，不含联网上传逻辑；编译期的 NuGet 还原除外。
