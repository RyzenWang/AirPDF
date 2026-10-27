<p align="center">
  <img src="docs/welcome.png" alt="AirPDF 欢迎界面" width="860" />
</p>

<h1 align="center">AirPDF</h1>

<p align="center">一款轻量、跨平台的 PDF 阅读器，使用 .NET 与 Avalonia 构建。</p>

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square" />
  <img alt="Avalonia 11" src="https://img.shields.io/badge/Avalonia-11.3-8B44AC?style=flat-square" />
  <img alt="MIT" src="https://img.shields.io/badge/license-MIT-2E6F63?style=flat-square" />
</p>

AirPDF 打开得快、渲染得稳，然后安静地退到页面后面。没有账号，没有云端，没有遥测，
也没有内嵌浏览器。整个阅读器就是一个小的 .NET 应用，界面用 Avalonia 手写。

> **状态：** 0.1 预览版。阅读核心已经可用并有测试覆盖；批注、打印、文本选择尚未实现。

## 功能

- **PDFium 原生渲染**，按实际显示比例解码，支持高 DPI。
- **连续滚动 + 页面虚拟化**，只渲染视口附近的页面。
- **缩略图侧栏与文档目录**，长文档也能快速跳转。
- **全文搜索**，结果按页分组，并在页面上绘制匹配框。
- **缩放**：适合宽度、适合页面、实际大小、预设比例、`Ctrl` + 滚轮。
- **加密 PDF**：会弹出密码输入框。
- **浅色 / 深色主题**，首次启动跟随系统。
- **拖放打开**、命令行打开、最近文件列表。
- **键盘操作**，以及一条克制的状态栏。
- 阅读路径不产生任何网络请求。

## 技术栈

| 层 | 选择 | 原因 |
| --- | --- | --- |
| 运行时 | .NET 10 (LTS) | 一个现代运行时覆盖三大桌面平台。 |
| 界面 | Avalonia 11 | 一套 XAML 代码生成原生桌面窗口。 |
| 渲染 | PDFium（PDFtoImage） | 浏览器同款开源 PDF 引擎。 |
| 位图传输 | SkiaSharp | 快速、可控的像素处理。 |
| 文本层 | PdfPig | 纯托管文本提取、搜索与匹配坐标。 |
| MVVM | CommunityToolkit.Mvvm | 小而克制的源生成 MVVM。 |

## 从源码运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。Windows 10/11、
macOS 13+，或现代 Linux 桌面（X11 / Wayland）均可。

```bash
git clone https://github.com/RyzenWang/AirPDF.git
cd AirPDF
dotnet run --project src/AirPdf.App
```

也可以通过按钮、拖放，或命令行参数打开 PDF：

```bash
dotnet run --project src/AirPdf.App -- /path/to/document.pdf
```

## 构建与测试

```bash
dotnet restore AirPdf.sln
dotnet build AirPdf.sln -c Release
dotnet test tests/AirPdf.Core.Tests/AirPdf.Core.Tests.csproj -c Release
```

测试使用 `tests/Assets/sample.pdf`，一份八页的示例文档，覆盖英文、中文、
混合排版和搜索坐标。

## 发布

```bash
dotnet publish src/AirPdf.App -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

支持 `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、
`osx-arm64`。发布包目前未签名，Windows SmartScreen 和 macOS Gatekeeper
可能要求用户确认。

## 快捷键

| 快捷键 | 操作 |
| --- | --- |
| `Ctrl+O` / `Cmd+O` | 打开 PDF |
| `Ctrl+W` / `Cmd+W` | 关闭文档 |
| `Ctrl+F` / `Cmd+F` | 在文档中查找 |
| `Enter` | 跳转到输入的页码，或执行搜索 |
| `Esc` | 关闭搜索 / 忽略错误 / 退出全屏 |
| `Ctrl++` / `Ctrl+-` | 放大 / 缩小 |
| `Ctrl+0` | 适合页面 |
| `Ctrl+1` | 实际大小 |
| `Ctrl+2` | 适合宽度 |
| `Ctrl` + 滚轮 | 缩放 |
| `F11` | 全屏 |
| `Page Up` / `Page Down` | 滚动页面 |

## 项目结构

```
src/
  AirPdf.Core/          PDF 服务、模型、设置
  AirPdf.App/           Avalonia 桌面应用
tests/
  AirPdf.Core.Tests/    打开、渲染、搜索、错误处理测试
  Assets/               可复现的示例 PDF 与 HTML 源文件
docs/                   截图与架构说明
```

`AirPdf.Core` 不依赖 Avalonia，可以被 CLI、测试或其他前端复用。PDFium 不是
线程安全的，所以所有渲染调用在一个进程级信号量下串行执行。页面位图会按当前页
附近的小窗口淘汰，避免长文档占用过多内存。

## 已知限制

- 页面以位图呈现，暂不支持文本选择。
- 暂无批注、表单填写、打印或 PDF 编辑。
- 带 `/Rotate` 的页面暂不显示搜索高亮，但搜索结果仍可用。
- 单窗口单文档，没有标签页。
- 暂无安装器与代码签名。
- 移动端与浏览器端不在本项目范围内。

## 参与贡献

请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。项目遵循
[Contributor Covenant](CODE_OF_CONDUCT.md)。安全问题请按
[SECURITY.md](SECURITY.md) 私下报告。

## 许可证

AirPDF 使用 [MIT License](LICENSE)。第三方组件与许可证见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

---

<sub>English: [README.md](README.md)</sub>
