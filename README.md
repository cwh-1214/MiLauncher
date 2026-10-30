# 米哈游游戏中心 · MiLauncher

一个轻量的米哈游游戏启动器，用 WPF / .NET 8 重写了官方启动器那套界面：无边框窗口、官方背景图轮播、一键启动游戏。

> **这不是官方软件。** 本项目为个人学习作品，与米哈游（miHoYo / HoYoverse）无关。
> 背景图、游戏图标等素材版权归米哈游所有，本项目只是调用官方接口把它们展示出来。
> 请优先使用官方启动器；如本项目侵犯了你的权益，请提 Issue，我会立即处理。

![界面截图](docs/screenshot.png)

## 功能

- **自动发现游戏**：首次启动扫描常见安装位置和注册表，原神 / 崩坏：星穹铁道 / 绝区零 / 崩坏3 自动入列，也可以手动添加目录
- **官方背景图轮播**：从米哈游资源接口拉取当前版本的官方背景图，按设置间隔切换，带交叉淡入
- **秒开游戏**：启动前把解码结果落成 BMP 旁车缓存，第二次起直接读缓存，切换游戏首帧约 80ms
- **版本检查**：查线上版本号，有更新时把副按钮变成金色的「更新至 vX」，点击拉起官方启动器
- **绝区零 DX12 模式**：勾选后以 `-use-d3d12` 启动，状态按游戏分别记忆
- **设置面板**：轮播开关与间隔、背景清晰度、开机自启、素材缓存目录与清理
- **细节**：单实例、记住窗口位置、上次启动时间、运行中状态、无边框自定义标题栏

## 系统要求

- Windows 10 / 11 x64
- 不需要安装 .NET 运行时（发布包是自包含单文件）

## 使用

1. 从 [Releases](../../releases) 下载 `MiLauncher.exe`
2. 双击运行。首次启动会自动扫描已安装的游戏
3. 没扫描到就在设置里用「添加游戏目录」，选到游戏主程序（比如 `YuanShen.exe`）即可

### 关于「Windows 已保护你的电脑」

发布包没有购买代码签名证书，所以从浏览器下载后首次运行可能弹出这个提示。**它不是检测到病毒，只是 Windows 不认识这个文件。** 点「更多信息」→「仍要运行」即可。

如果介意，可以用发布页附带的 `SHA256SUMS.txt` 校验文件完整性：

```bat
certutil -hashfile MiLauncher.exe SHA256
```

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bat
publish.cmd
```

产物在 `publish\MiLauncher.exe`（自包含单文件，约 80MB，包含中文字体）。

也可以直接用 dotnet 命令：

```bat
dotnet publish MiLauncher.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o publish
```

开发时直接 `dotnet run` 或 F5 即可。

## 项目结构

```
MiLauncher/
├─ MainWindow.xaml(.cs)      主界面：背景层、详情区、设置弹窗
├─ App.xaml                   全局资源：配色、字体
├─ Models/GameInfo.cs         游戏数据模型
├─ Services/
│  ├─ GameCatalog.cs          四款游戏的静态定义（进程名、biz、主色…）
│  ├─ GameScanner.cs          扫描安装位置、读版本号、提取图标
│  ├─ GameStore.cs            配置读写（%APPDATA%\MiLauncher\games.json）
│  ├─ OfficialAssets.cs       官方素材下载、解码、BMP 旁车缓存
│  ├─ VersionChecker.cs       线上版本查询
│  └─ AutoStart.cs            开机自启（注册表 Run 项）
├─ Assets/Fonts/              内嵌字体（HarmonyOS Sans + 许可文件）
└─ tools/                     构建辅助脚本（签名、截图）
```

## 一些实现上的取舍

**为什么用 BMP 旁车缓存**：`BitmapImage` 解码 1920 宽的 WebP 要 90–250ms，切换游戏时肉眼可见。首次解码后顺手写一份 `.d1920.bmp` 到缓存目录，下次直接读——BMP 无需解码，28ms 就能上屏。另外踩过一个坑：`StreamSource` 喂流给 WIC 比直接给 `UriSource` 文件路径慢 20 倍。

**内存怎么压的**：只解码当前游戏的帧，且只保留「当前帧 + 下一帧」，切走立刻释放；切换后空闲时主动做一次 GC 修剪（否则原生位图要等 GC，每切一次涨一百多兆）。稳定态约 300–500MB，比不优化时的 600MB+ 好很多。

**为什么没有第三方依赖**：整个项目只用 BCL——`System.Text.Json`、`HttpClient`、`Microsoft.Win32.Registry`，加少量 `user32`/`dwmapi`/`shell32` 的 P/Invoke。没有 NuGet 包，`dotnet publish` 一条命令就能出包。

## 已知限制

- 只支持 Windows x64，没有跨平台计划
- 国际服版本走另一套接口，暂不支持版本检查
- 首次运行需要解压自包含单文件，约 2–3 秒；之后正常
- 背景图保留两帧 1920 宽的位图，内存占用比纯色启动器高，介意的话可在设置里调低「背景清晰度」

## 许可

代码以 MIT 许可发布，详见 [LICENSE](LICENSE)。

字体（HarmonyOS Sans、Melete）遵循各自的许可，许可原文在 `Assets/Fonts/` 下。
游戏名称、背景图、图标等素材版权归米哈游所有，**不在本项目许可范围内**。
