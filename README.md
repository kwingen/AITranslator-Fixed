# AITranslator-Fixed

> 非官方可靠性修复版，基于 [FireFrog / jxq1997216 的 AITranslator](https://github.com/jxq1997216/AITranslator) v2.2.3（`741b0dd`），保留 GPL-3.0、原作者署名和上游历史。修改日期：2026-10-16。
>
> 本版优先修复译文误判、原文/断点安全、SRT/TPP 匹配与外部接口兼容；不是 UI 重写或原生 GPU 后端升级。新译文位于任务目录的 **`output/`**，内部断点原路径不变。
>
> **[使用、构建、安全边界与后续工作](docs/MAINTENANCE.md)** · [HTTP 扩展参数说明](AITranslator/Translator/PostData/OpenAI-compatible-HTTP.md) · [检查点恢复说明](AITranslator/Translator/Persistent/README.md)

## 修复摘要

- 重翻校验与保存最新结果；Translator++ 降级单条翻译保留稳定键。
- 字幕保留非连续 cue ID 和时间戳，缺项或旧错位检查点明确报错，不生成貌似成功的残缺文件。
- 外部导入只读；内部断点原子保存与显式备份恢复；缺模板不再误删任务。
- 输出独立目录，拒绝路径穿越；重翻失败项不会被旧备份重新“复活”。
- 空回复、截断、过滤、拒绝、思考内容明确分类；兼容可省略参数与 `max_completion_tokens`。
- 遵守 Retry-After 的可取消退避；上下文超限裁剪历史，批次超限/截断自动缩批。
- Windows 当前用户凭据保护、回归测试、锁定依赖与 Windows CI 模板（`ci/windows.yml`；远端待授予 workflow 权限后启用）。

## 原项目说明

#### 介绍
使用大语言模型来翻译文件的图形化UI软件，目前支持以下类型文件的翻译
1. MTool导出的json格式的待翻译文件
2. Translator++导出的包含csv文件的文件夹
3. srt字幕文件
4. txt文本文件

#### 使用说明
1.  打开此软件
2.  使用内置加载器加载GGUF模型 Or 设置好OpenAI接口参数
3.  新建翻译任务
4.  配置翻译任务的翻译选项
5.  开始翻译，并等待任务完成
6.  如果存在翻译失败的文本，请手动翻译失败部分，并合并翻译结果

#### 参与贡献
1.  Fork 本仓库
2.  新建 Develop_xxx 分支
3.  提交代码
4.  新建 Pull Request

#### 感谢
感谢smzh提供的python翻译脚本用于学习

#### 开源声明
项目使用到了以下开源库
- [LLamaSharp](https://github.com/SciSharp/LLamaSharp)
- [CommunityToolkit](https://github.com/CommunityToolkit/dotnet)
- [Microsoft.Xaml.Behaviors.Wpf](https://github.com/Microsoft/XamlBehaviorsWpf)
- [roslyn](https://github.com/dotnet/roslyn)
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json)
- [CsvHelper](https://github.com/JoshClose/CsvHelper)
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)
- [CalcBinding](https://github.com/Alex141/CalcBinding)

## License
本项目基于[GPL-3.0 License](LICENSE)发布，您如果基于此项目进行修改和分发，必须保持开源
