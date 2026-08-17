# WinAgentController 当前进度

- 更新时间：2026-08-14
- 基线提交：`main` @ `e1fbc32`（与远端一致）；工作区含未提交改动（目录索引、SDK 兼容策略、`TCP_NODELAY`、性能证据与本文档），待提交。
- 事实来源优先级：当前实现与 CLI 用法 > 部署脚本 > `README.md` > 历史记录。

## 当前结论

WinAgentController 已具备局域网发现、TLS 指纹固定、单控制端配对、目标档案自动登记、普通/提权命令、持久任务、受限文件传输（64 MiB TLS 流、256 MiB 检查点、断线重连、逻辑/线上/重传统计）、桌面 UI 自动化、Chromium 浏览器控制和完整包更新能力。跨机器 1 GiB 已在 256 MiB 检查点后真实终止 Agent 并以同 session 续传完成，三方 SHA-256 一致。

大量小文件目录慢路径已修复：Agent/CLI 端 O(N²) 扫描改为会话级索引，控制端出站与 Agent 入站 TCP 启用 `TCP_NODELAY`，消除逐文件小控制帧的 Nagle/延迟 ACK 等待。13,667 文件真实目录下载由 323.634 秒降至 40.443 秒，达同数据集 SCP 吞吐的 87.5%（进程口径）。

SDK 策略：8.0.422 为原始下限基线，`global.json` `rollForward=latestMajor` 允许更高 SDK；本机 10.0.301 下 Debug/Release 全方案 307/307。

## 能力状态

| 范围 | 当前状态 |
| --- | --- |
| 发现/探测/配对 | 已实现。发现仅用于定位；信任来自独立核对的 TLS SHA-256 指纹、一次性代码和已配对身份。 |
| 命令与持久任务 | 已实现。PowerShell/cmd、普通/显式提权、stdin、日志续读、等待、取消、PTY。 |
| 文件操作与续传 | 已实现并优化。64 MiB 二进制 TLS 流、流式哈希、单块断线重连、256 MiB 检查点；会话索引消除 O(N²) 扫描，逐分片控制握手仍存在。 |
| Agent/Broker 服务 | 已实现。Agent=LocalService，Privileged Broker=LocalSystem；提权需经认证控制链路。 |
| UI Agent / 自动化 | 已实现。仅控制活动、解锁的配置用户会话；窗口、截图、UIA、鼠标（move/button/wheel 分离）、键盘、剪贴板。 |
| 浏览器控制 | 已实现 Chromium 主要链路（Edge/Chrome 启动、导航、CDP DOM）。 |
| 目标档案 | 已实现。probe/pair/认证成功后自动登记；同设备新指纹成功固定验证后替换旧档案。 |
| 更新与部署 | 已实现。完整包更新（清单、断线重发、状态查询、detached 日志/超时、回滚）；`binary-update-v1` 已部署，缺少能力时要求先升级 Agent。 |

## 当前验证矩阵（关键项）

| 类别 | 结果 | 证据 |
| --- | --- | --- |
| Build / Publish | Pass | Windows x64 自包含包；19 根文件、0 子目录、689,693,449 字节。 |
| Debug / Release 测试 | Pass | SDK 10.0.301，307/307（Contracts 118 / TaskHost 15 / Broker 3 / Agent 171）；Build 0 warnings/0 errors。 |
| SDK 兼容策略 | Pass / Policy | 8.0.422 下限 + `latestMajor`；无明确问题不预防性拒绝更高 SDK。 |
| 断线与检查点 | Cross-machine Pass | 1 GiB 会话 `transfer-52ecf7e57e0f453c935daffbda93999d` 在 256 MiB 检查点后强制终止 Agent，同 session 续传完成：bytes=1,073,741,824、重传 58,720,256，三方 SHA-256 一致，残留清零。 |
| 目录慢路径修复 | Pass / Local | `FileTransferService.cs` 会话级索引、`FileCommand.cs` 恢复 `HashSet`、流式按 256 MiB 检查点快照；2,048 文件回归通过。 |
| 修复后 13,667 文件目录吞吐 | Cross-machine Pass | 目录下载 40.443 秒/2.53 MiB/s（修复前 323.634 秒），达 SCP（35.324 秒/2.89 MiB/s）的 87.5%/92.0%；SHA-256 一致、重传 0。 |
| 100 小文件 / 1 GiB 吞吐 | Cross-machine Pass | WAC 100 文件上传/下载 59.62/75.46 MiB/s（SCP 65.61/74.74）；1 GiB 96.53/106.97 MiB/s；SHA-256 全部一致。 |
| 最终包对端升级 | Cross-machine Pass | 689,693,449 字节，`Succeeded`、纯上传 8.769 秒/75.00 MiB/s、重传 0；身份/配对/认证/服务/UI 保持。 |
| UI 验收 | Pass | 本机与对端标准升级后均为 25/25（400 ms 间隔）；binary 更新后仅复核注册/会话/显示器。 |
| SCP 对照 / 持久凭据 | Cross-machine Pass | OpenSSH 9.5 SFTP 后端同数据集双向基准；专用 Ed25519 密钥 + `winagentcontroller` alias + 已核验 `known_hosts`。 |
| Lint / 覆盖率 | 未配置 | 无独立 lint 命令、无覆盖率配置。 |

## 当前缺口

1. **可选协议演进：** 当前 13,667 文件目录下载达 SCP 87.5%；仅当性能目标明确更高时再设计批量 manifest/打包流或多文件复用协议。
2. **排队：** detached 目标侧失败/回滚注入；Windows CI、覆盖率、发布哈希/许可清单。

其余覆盖率、CI、兼容性、压力、服务账户和卸载边界统一归入 `docs/审计.md` 技术债。

## 历史里程碑

| 日期 | 里程碑 |
| --- | --- |
| 2026-07-16 | Hyper-V 双机核心/UI 验收；ConPTY 时序问题于 08-11 修复。 |
| 2026-08-03 ~ 08-07 | 初始化 StrictMode、目标档案、空剪贴板、输入分离修复；本机 UI 25/25。 |
| 2026-08-11 | detached 更新、流式 copy、SCP 对照、自动登记、实时速度、binary update；测试 285/285。 |
| 2026-08-12 | 旧指纹自动替换、断线重连与 256 MiB 检查点、detached 日志/超时；306/306；1 GiB 真实断线续传。 |
| 2026-08-14 | O(N²) + `TCP_NODELAY` 修复（13,667 文件目录下载达 SCP 87.5%）；WAC/SCP 双向基准；最终包标准升级通过。 |

## 文档约定

- `README.md`：用户入口，维护功能、部署、命令和安全边界。
- `docs/CURRENT_PROGRESS.md`：当前能力、验证证据和剩余缺口。
- `docs/交接.md`：当前目标、最近完成项和前三项执行路线。
- `docs/审计.md`：质量矩阵、已知风险、技术债和信心边界。
- `tools/Rc.TestRemoteControl`：可信测试局域网中的临时独立命令通道，不属于产品或发布包。
- 历史失败只有仍能解释当前风险时才保留；已被取代的过程日志压缩为里程碑。
