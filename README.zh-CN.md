# Factorio Mod Studio

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · **简体中文** · [Українська](README.uk.md) · [Polski](README.pl.md) · [Français](README.fr.md) · [Español](README.es.md)

**为 [Factorio](https://factorio.com) 模组作者打造的本地工作台。** 创建模组、在游戏之前发现它的错误、翻译它、打包干净的发行版，
并用隔离的模组组合试运行——既可以在命令行中操作，也可以在浏览器页面中操作。一切都在你自己的电脑上运行，与你常用的代码编辑器并行使用。

> **早期版本（0.1）。** 逻辑已有测试覆盖，但启动真实游戏（`run`、`smoke`）尚未在 Factorio 上验证。非常欢迎反馈和错误报告。
> 非官方社区项目，与 Wube Software 无关。“Factorio”是 Wube Software Ltd. 的商标。
>
> 这是简介部分的翻译。完整文档为[英文版](README.md#documentation)；如有出入，以英文 README 为准。

## 功能

- **几秒钟创建模组。** 适用于 Factorio 2.0 和 2.1 的模板（空模组、物品 + 配方、实体、运行时脚本；除实体外也支持 1.1），开箱即可通过所有检查。
- **在游戏之前发现错误。** `info.json`、更新日志、缩略图、损坏的 `__mod__/` 路径和 `require`、缺失或有误的翻译——都附带文件和行号。
- **查看任意模组的内部。** 浏览器中的工作室：文件树、带实时检查的 Lua 编辑器、模组定义的原型和配方、它的图形和声音，以及没有被任何地方引用的文件。
- **轻松翻译。** 原文与译文并排的编辑器，带每种语言的进度、占位符检查，以及来自你以往翻译的建议。
- **干净的发行版。** 目录结构正确的 `NAME_VERSION.zip`，只有在模组通过检查时才会生成。
- **测试时不动你的游戏。** 为每个配置使用隔离的模组组合启动 Factorio 或无界面冒烟测试；你平时的模组文件夹不会被改动。可在 CI 中使用。
- **更快地读日志。** 将 Factorio 日志浓缩为错误和警告，合并重复项，并附带文件、行号和提示。

## 路线图

项目现状与下一步计划。版本号是目标而不是日期；顺序取决于反馈，所以如果某一项对你很重要（或这里缺少了什么），
请[提交 issue](https://github.com/4a11/factorio-mod-studio/issues)。

**0.1：现在。** 命令行（`new`、`validate`、`pack`、`prepare` / `run`、`smoke`、`log`、`factorio`）和浏览器工作室
（概览、文件、原型、资源、本地化、问题、构建），界面支持 8 种语言。

**0.2：在真实游戏上验证**
- [ ] 在 Windows、Linux 和 macOS 上用 Factorio 1.1、2.0 和 2.1（包括无界面服务器）验证 `run` 和 `smoke`
- [ ] 运行或冒烟测试之后同时读取 `factorio-current.log`，并在“问题”和“输出”中显示其中的错误
- [ ] 模板生成的原型（实体、运行时脚本）在游戏中实际加载过，而不只是按文档编写
- [ ] 一行命令安装：工具发布到 NuGet（`dotnet tool install -g FactorioModStudio.Cli`），无需克隆仓库
- [ ] 由工作室安装 Factorio 版本：`fmstudio factorio install 2.0` 下载免费的无界面服务器（Linux、WSL、CI），`smoke` 会自动找到它，“构建”标签页中也可以安装；*已实现，尚未用真实下载验证*

**0.3：其余标签页**
- [ ] 新风格的“问题”页：按文件和规则分组，提供安全的快速修复（添加缺失的翻译键、修正路径的大小写）
- [ ] “构建”页：选择配置、额外模组和存档；可对比的运行历史
- [ ] 显示来自基础游戏或依赖模组的原料和产物图标
- [ ] 依赖一目了然：必需、可选、不兼容，以及哪些可在 `modsSource` 中找到

**0.4：发布流程**
- [ ] 一步完成版本号提升和更新日志条目（`fmstudio bump patch|minor|major`）
- [ ] 上传到 Factorio 模组门户（可选，使用你自己的 API 密钥）
- [ ] 现成的 GitHub Actions 工作流（`fmstudio init --ci`）：每次 push 时检查、打包并进行冒烟测试

**以后**
- 通过 `--dump-data` 查看最终原型：“哪个模组修改了这个物品，改成了什么？”
- 在工作室中运行为 [FactorioTest](https://mods.factorio.com/mod/factorio-test) 编写的测试
- 基于同一 API 的编辑器扩展（VS Code）

## 为什么

编辑 Lua、调试和单元测试已经有很好的工具（例如 VS Code 的 *Factorio Modding Tool Kit* 和 *FactorioTest*）。
仍然繁琐的是它们之间的“胶水”工作：正确的发行版 zip、可复现的启动模组组合、在大量日志中找出唯一真正的错误、彼此不同步的翻译。
Factorio Mod Studio 负责的正是这些胶水工作。

## 快速开始

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download)。

```sh
# 1. 安装 `fmstudio` 工具（只需一次）
git clone https://github.com/4a11/factorio-mod-studio
cd factorio-mod-studio
dotnet pack src/FactorioModStudio.Cli -c Release -o ./nupkg
dotnet tool install --global FactorioModStudio.Cli --add-source ./nupkg

# 2. 在任意文件夹中创建模组并打开工作室
cd ~/factorio-dev
fmstudio new my-mod --template item-recipe   # 在 ./mods/my-mod 中生成可直接运行的模组
fmstudio ui                                  # 在浏览器中打开 ./mods 中的所有模组
```

典型流程：使用浏览器中每个模组顶部的按钮，或在模组文件夹的终端中执行同样的操作：

```sh
cd mods/my-mod
fmstudio validate      # 哪里有问题，附带文件和行号
fmstudio pack          # dist/my-mod_0.1.0.zip
fmstudio smoke         # 以无界面方式加载 600 tick；出现任何错误即失败
fmstudio run           # 只带这个模组及其依赖启动 Factorio
```

Factorio 会被自动找到（常见安装目录、`FACTORIO_PATH`，或在界面中添加的安装）；会使用与模组 `factorio_version` 相匹配的版本。

## 文档

完整文档（命令、配置、工作室各标签页、检查规则、API）为[英文版](README.md#documentation)。

## 许可证

[MIT](LICENSE)
