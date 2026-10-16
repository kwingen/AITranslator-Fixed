# AITranslator-Fixed 维护说明

这是基于 [jxq1997216/AITranslator](https://github.com/jxq1997216/AITranslator) 的独立修复版本，基线为 `741b0dda0d62c21c592457c0d65a983a43fc3163`（上游 v2.2.3）。保留原作者 FireFrog 的署名、历史和 GPL-3.0 许可。不是上游官方发行版。

## 兼容与使用

- 保留 WPF、现有任务和 JSON / Translator++ CSV / SRT / TXT 格式。
- 优先使用独立的 Sakura / llama-server 或其他 OpenAI Chat Completions 兼容服务；不要直接把新 llama.cpp DLL 覆盖进旧 LLamaSharp。
- 任务内部数据继续保留原路径，避免强制迁移旧任务。**新合并结果放在任务目录的 `output/` 子目录**，避免与配置、原文或断点同名。
- 导入文件是只读的：不会用导入目录里的 `_bak` 文件反向覆盖原文。
- 内部检查点采用同目录临时文件、刷新磁盘和原子替换；保留上一版 `.bak`。内部读取优先当前文件，当前缺失/损坏时才回退备份，并兼容旧 `_bak` 恢复文件。不会自动移动或删除恢复文件。
- 手工编辑失败译文后，请保存到原来的主文件；有效主文件优先于备份。
- 保留整文件检查点机制；没有启用未经隔离验证的并发工作线程。

## 外部接口参数

服务器地址仍填写到 `/v1`（例如 `http://127.0.0.1:8080/v1`），程序调用 `/chat/completions`。

“扩展参数”接受 JSON 对象（也兼容原来的对象成员片段），在结构化合并后发送，而非拼接请求字符串。具体保留字段与思考处理选项见测试和本文件后续接口说明。

- JSON `null` 表示移除该默认字段，适合不支持采样或 stop 的接口。
- 设置 `max_completion_tokens` 会替换 `max_tokens`，不会同时发送两种上限。
- 参数按**实际服务器和模型能力**配置，不按模型名猜测。不强迫本地服务改用 Responses API。
- 认证/权限/普通无效请求直接报错，不对相同 400/401/403 请求盲目重发。
- 暂时性错误退避重试，尊重 Retry-After，可通过暂停取消等待。
- 输出截断、拒绝、过滤、空回复不是翻译成功。批次遇到上下文超限或截断时，回到调用端**缩批为逐条翻译**，每条先校验并保存断点，再请求下一条，避免后续失败丢弃前面已成功的结果；上下文超限时先裁剪最老的历史对话，**不删原文、不自动提高付费输出上限**。单条仍超限/截断时明确暂停，不会静默保存半句。
- 扩展参数不能替换/删除 `messages` 或 `model`；重复响应控制字段、未配对思考标记均明确拒绝。默认客户端不跟随重定向，避免把原文和历史转发到新地址。

## 构建和测试

Windows + .NET 8 SDK；SDK 版本记录在 `global.json`。无需 GPU 即可执行回归测试。

```powershell
dotnet restore AITranslator.sln --locked-mode
dotnet build AITranslator.sln -c Release --no-restore
dotnet test AITranslator.Tests/AITranslator.Tests.csproj -c Release --no-build --no-restore
dotnet publish AITranslator/AITranslator.csproj -c Release --no-restore --self-contained false -o dist/AITranslator-Fixed
```

框架依赖发布需要 .NET 8 Windows Desktop Runtime（x64）。源码包和外部服务版本**不捆绑 GGUF 模型、CUDA、Vulkan 或 llama 原生运行库**。

Windows CI 模板保留在 `ci/windows.yml`。当前 GitHub OAuth 授权没有 `workflow` 权限，因此本次没有把它放入 `.github/workflows/`，**远端 CI 尚未启用/运行**。授予 `workflow` 权限后将该文件复制到 `.github/workflows/windows.yml` 并推送即可启用。模板按锁定依赖构建完整 WPF 解决方案、执行测试并上传外部服务版产物；同等的本机构建/测试已执行，远端状态仍应以实际 GitHub 运行结果为准。

## 凭据保存与迁移

- API Key 不再写入加载器 JSON。JSON 只保存随机 `CredentialId`，密钥以 Windows 当前用户 DPAPI 保护后存入 `%LOCALAPPDATA%/AITranslator/Credentials/`，并绑定原服务器地址（包括协议、端口、路径和查询）。更改服务地址后应重新输入对应密钥，不会把原密钥自动发送到新地址。
- SMTP 授权码使用带版本的 `ProtectedEmailPassword` DPAPI 信封，绑定邮箱、服务器、端口和 TLS 设置，不再保存明文 `EmailPassword`。
- 旧明文配置仍可读取；下一次显式保存时，先验证并迁移主文件以及 `.bak` / `_bak` 候选文件，再执行检查点轮换，避免把明文留在新版备份里。无法读取或存在歧义时停止保存，不静默清空凭据。
- DPAPI 凭据与 Windows 用户绑定。复制加载器到另一台电脑/另一账号后需要重新配置凭据；`CredentialId` 不是可跨机器使用的 API Key。备份迁移时应保留原 Windows 用户配置和本地凭据目录，或在新机器明确重新输入。失去原凭据访问权时，先另存完整旧配置，再在副本中删除失效的 `CredentialId` / `ProtectedEmailPassword` 并重新配置，不能把删除凭据当作自动恢复策略。
- 已存在于其他目录、手工备份或旧 Git 历史中的明文不在自动迁移范围；若曾公开泄漏应在服务商处撤销并轮换。

## 信任与安全边界

- `.csx` 清理、校验、对话模板是当前用户权限的可执行 C#，不是沙箱配置。只运行可信来源脚本；备份不能防止恶意脚本删除文件。
- 测试数据是无敏感内容的临时文件；不会把用户 API Key、邮箱授权码、模型或翻译任务提交到 Git。
- Windows 凭据保护仅用于静态保存，不保护当前用户已经被控制的进程、调试器或管理员环境。
- 升级旧安装前，先离线备份整个软件目录和 `翻译数据`，不要在仅有一份成果的生产目录直接测试。

## 明确保留到后续的工程更新

1. 服务端 tokenizer / 精确 token 预算、RPM/TPM 调度、超长单条安全拆分和游戏占位符的完整验证。
2. 真正隔离历史和校验输入后的有界并发、日志式或数据库增量检查点。
3. .NET 10 迁移，以及 LLamaSharp / llama.cpp / 原生插件整套升级与 GPU 矩阵测试。
4. UI 重做、所有第三方兼容服务与所有历史文件边界格式的全面兼容测试。

本版不声称完成了这些后续重构，也不把模拟服务器测试当作实际 Sakura/GPU 推理验证。
