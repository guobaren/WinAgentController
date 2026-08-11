# WinAgentController 当前进度

- 更新时间：2026-08-11
- 基线提交：`main`（本轮新增测试与升级链路修复尚未提交）
- 事实来源优先级：当前实现与 CLI 用法 > 部署脚本 > `README.md` > 历史记录。

## 当前结论

WinAgentController 已具备局域网发现、TLS 指纹固定、单控制端配对、普通/提权命令、持久任务、受限文件传输、桌面 UI 自动化、Chromium 浏览器控制和完整包更新能力。当前 Release 构建、Agent 141/141、跨机器 job、400 ms 对端 UI 25/25、新版独立恢复通道、标准 `update apply --wait` 以及接近 SCP 性能的 100 小文件/1 GiB 双向 `copy` 均已验证；本轮完整测试 277/277 通过。

## 能力状态

| 范围 | 当前状态 |
| --- | --- |
| 发现、探测与配对 | 已实现。发现仅用于定位；信任来自独立核对的 TLS SHA-256 指纹、一次性代码和已配对控制端身份。 |
| 命令与持久任务 | 已实现。支持 PowerShell/cmd、普通/显式提权、stdin、日志续读、等待、取消和 PTY。 |
| 文件操作与续传 | 已实现并完成流式性能优化。远端路径受 `RC_AGENT_FILE_ROOT` 限制；新 Agent 协商 64 MiB 二进制 TLS 流，旧 Agent 回退 4 MiB JSON，支持流式逐块哈希、每 256 MiB 断点持久化和吞吐输出。 |
| Agent/Broker 服务 | 已实现。Agent 使用 LocalService，Privileged Broker 使用 LocalSystem；提权请求需经过已认证控制链路。 |
| UI Agent | 已实现。仅控制配置用户的活动、解锁登录会话，不覆盖锁屏、Winlogon 或 UAC 安全桌面。 |
| UI 自动化 | 已实现主要功能。支持窗口、截图、UI Automation、鼠标、键盘和剪贴板。鼠标 move、button、wheel 是三个独立操作。 |
| 浏览器控制 | 已实现 Chromium 主要链路。支持 Edge/Chrome 启动、导航和受控 CDP DOM 读取。 |
| 目标档案 | 已实现。档案保存名称、设备 ID、最近端点和固定指纹；刷新只能在设备 ID 与指纹同时匹配时更新端点。 |
| 更新与部署 | 已实现首版。完整包更新支持清单、分块传输、状态查询和脚本回滚；更多真实双节点失败场景仍待覆盖。 |

## 当前验证矩阵

| 类别 | 结果 | 当前证据与边界 |
| --- | --- | --- |
| Release Build / Publish | Pass | Windows x64 自包含完整包已成功生成；所需可执行文件与部署脚本齐全。 |
| README / 操作 Skill | Pass | README、主 SKILL、部署与排障引用已同步 detached 更新流程和 `RegenerateIdentity=false` 默认值；仓库 Skill 重装到本机和对端后均为 5/5 文件哈希一致。 |
| 单元测试 | Pass | Contracts 118/118、TaskHost 15/15、PrivilegedBroker 3/3、Agent 141/141，共 277/277。使用 SDK 10.0.301 workaround；固定的 SDK 8.0.422 仍未安装。 |
| Lint | 未配置 | 项目没有独立 lint 命令；不把编译成功表述为 Lint 0 errors。 |
| 测试覆盖率 | 未配置 | 当前没有覆盖率配置或报告。 |
| 本机可见 UI 验收 | Pass | 单 UiAgent、最新发布目录链路为 25/25；空剪贴板恢复立即和延迟读回均为 0 字节。 |
| 输入操作分离 | Pass | UI 验收先单独 move，再分别验证 button 和 wheel；滚轮上/下均通过。 |
| 历史 Hyper-V 双机验收 | Pass | 2026-07-16 已验证 UI Automation 25 项、浏览器 DOM、服务恢复、提权、持久任务与文件续传。 |
| 当前跨机器认证与列表命令 | Pass / 当前在线 | 用户确认恢复后的新指纹可信；外部安装后 probe 仍显示同一设备 ID、固定指纹和 `paired=True`，认证命令、job 与 UI 链路可用。 |
| `target` 四子命令实测 | Pass | 在隔离档案中，`add`、`list`、`use` 均退出码 0 且返回/持久状态符合预期；`refresh` 首次因普通终端无权启用 UDP 43000 防火墙规则失败，随后由 Windows `RunAs` 提权 PowerShell 重测退出码 0，并返回同一设备 ID、固定指纹和端点。 |
| 目标档案自动登记 | 未实现 | `target list` 只读取控制端 `targets.json`；pair、probe 和普通认证连接成功均不会自动 add。本次重新配对 `.50` 后实测正式列表仍只有既有 `.47` 档案。 |
| Setup 身份默认值 | Changed | `RemoteController.Agent.config.json` 与 Setup 缺省回退均改为 `RegenerateIdentity=false`；需显式设为 `true` 才轮换身份。 |
| CLI 命令级自动化覆盖 | Improved / Partial | 新增 `CliCommandEntryTests` 的 10 个命令族非法参数用例、`CliCommandLiveTests` 的 probe/target/job/fs/copy/exec/ui/pair 真实链路用例，以及版本归一化回归；底层与入口合计 270 个测试，仍未配置覆盖率报告。 |
| 干净发布包 | Pass | 根目录 19 个文件、无子目录、689,531,546 字节；新增 `Invoke-RemoteControllerDetachedUpdate.ps1`，不再把各项目 staging 目录重复打入更新清单。 |
| 对端 job 命令 | Pass | 新增 `echo RC_JOB_RECOVERY_OK` 任务真实终态为 `Exited`、远端退出码 0、日志标记匹配；历史列表和失败状态过滤也返回预期。 |
| 对端 UI 验收 | Pass | 标准升级后以普通完整性交互任务启动测试程序，400 ms 默认间隔 25/25；真实控件反馈、三键拖动、滚轮、键盘和剪贴板均匹配，测试进程与任务已清理。 |
| 对端升级 | Pass | 最终标准 `update apply --wait` 完整接收 689,531,546 字节，78.3 秒返回 `Succeeded/0`。独立 SYSTEM runner 的 ready/started/result 均存在且 result 为成功，升级任务自清理；随后 probe、认证、关键哈希、job 与 UI 25/25 全部通过。 |
| 测试专用恢复 EXE | Cross-machine Pass | 对端运行 EXE 的 SHA-256 与本地产物一致，管理员启动的监听进程通过 status/exec。实测先将 Agent/Broker 停止，再由内置 recover 恢复为 Running，随后 probe、认证命令、job 与 UI 25/25 全部通过；从非管理员进程触发自动 UAC 的分支未单独重演。 |
| `copy` 性能与完整性 | Cross-machine Pass | 二进制流式 v2、64 MiB 分块连续两轮：100 个 1 KiB–5 MiB 文件共 175,713,393 字节，上传 52.57–52.71、下载 69.67–74.71 MiB/s；1 GiB 上传 97.72–99.52、下载 106.06–107.65 MiB/s。逐文件与大文件 SHA-256 全部匹配。 |
| SCP/SFTP 对照 | Cross-machine Pass | 默认 `scp.exe`（SFTP 后端）小文件上传/下载 54.26/75.43 MiB/s，1 GiB 102.13/107.27 MiB/s；传统 `scp -O` 为 50.75/83.74、99.09/108.12 MiB/s。两种模式 SHA-256 全部匹配。 |
| OpenSSH Server | Installed / persistent | 对端 capability 已安装，`sshd` Running/Automatic，防火墙规则启用且 TCP/22 可连接。专用测试凭据已移除，服务与规则按用户要求保留。 |

## 当前缺口

1. 将已验证的二进制流式传输方案复用于仍为 256 KiB JSON/Base64 的完整包更新，减少约 2600 次串行事务。
2. 为流式 copy 增加真实断线后检查点恢复测试，确认最多重传最近 256 MiB，且旧 Agent 4 MiB JSON 回退可用。
3. 继续验证更新中断、断线续传、失败回滚、服务账户切换和卸载的数据保留边界。
4. 扩展不同 Windows、浏览器、输入法、锁屏/解锁切换和复杂 UI Automation 树的兼容性回归。
5. 增加磁盘/配额耗尽、恶意配对压力、审计异常与多节点压力测试。
6. 建立覆盖率报告、Windows CI、发布制品哈希与第三方许可清单。
7. 补齐 `global.json` 固定的 .NET SDK 8.0.422，并在官方工具链下复跑 Build/Test，消除 SDK 10 workaround。
8. 保持测试恢复监听器在升级期间独立运行，并区分内置 recover、显式 UAC 恢复和标准更新结果。
9. UI 测试程序必须以普通用户完整性启动；从管理员恢复监听器直接启动会继承高完整性并被普通 UI Agent 的 UIPI 边界阻止。
10. 若需要覆盖自动提权入口，还需从非管理员终端启动新版 `listen` 并人工确认 UAC；本轮用户直接以管理员权限启动，已完整覆盖管理员监听和 recover，不把它等同于自动 UAC 分支。

## 历史里程碑

| 日期 | 里程碑 | 当前解释 |
| --- | --- | --- |
| 2026-07-16 | 234 项测试中 1 项 ConPTY 取消时序失败；完成 Hyper-V 双机核心/UI 验收。 | 本轮 274 项套件中同类用例再次失败，现为当前 273/274 的唯一缺口。 |
| 2026-08-03 | 修复一键初始化在 StrictMode 下读取缺失 `paired` 属性导致的尾部退出码 1。 | 修复已进入当前分支；真实部署仍须区分脚本成功与服务/控制链路验收。 |
| 2026-08-04 | 完成目标档案和重复指纹参数拒绝。 | 功能已进入当前实现，不再标记为“仅规划”。 |
| 2026-08-07 | 完成初始化提升输出、空剪贴板恢复和鼠标输入操作分离；本机 UI 25/25。 | 当前源码基线；跨机器目标需人工恢复升级中断后的服务，再复验。 |

## 文档约定

- `README.md`：用户入口，维护功能、部署、命令和安全边界。
- `docs/CURRENT_PROGRESS.md`：当前能力、验证证据和剩余缺口。
- `docs/交接.md`：当前目标、最近完成项和前三项执行路线。
- `docs/审计.md`：当前质量矩阵、已知风险、技术债和信心边界。
- `tools/Rc.TestRemoteControl`：可信测试局域网中的临时独立命令通道，不属于产品或发布包。
- 历史失败只有仍能解释当前风险时才保留；已被后续证据取代的过程日志压缩为里程碑，不重复追加整段审计。
