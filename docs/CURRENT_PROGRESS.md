# WinAgentController 当前进度

- 更新时间：2026-08-12
- 基线提交：`main` @ `5dde471`（功能、测试与此前文档已提交并推送；本轮仅增量更新验收文档）
- 事实来源优先级：当前实现与 CLI 用法 > 部署脚本 > `README.md` > 历史记录。

## 当前结论

WinAgentController 已具备局域网发现、TLS 指纹固定、单控制端配对、成功连接后自动登记目标档案、普通/提权命令、持久任务、受限文件传输、桌面 UI 自动化、Chromium 浏览器控制和完整包更新能力。当前 copy/update 强制使用认证 TLS 二进制能力，不再回退旧 Agent JSON 路径；单块断线会重新认证并重发，完成统计区分逻辑、线上和重传字节。跨机器 1 GiB 已在 256 MiB 检查点后真实终止目标 Agent，并以同一 session 自动续传完成且三方 SHA-256 一致。detached 更新持久化 stdout/stderr 且具备结果超时。SDK 8.0.422 下 Debug/Release 完整测试均为 306/306。

## 能力状态

| 范围 | 当前状态 |
| --- | --- |
| 发现、探测与配对 | 已实现。发现仅用于定位；信任来自独立核对的 TLS SHA-256 指纹、一次性代码和已配对控制端身份。 |
| 命令与持久任务 | 已实现。支持 PowerShell/cmd、普通/显式提权、stdin、日志续读、等待、取消和 PTY。 |
| 文件操作与续传 | 已实现并完成流式性能优化。远端路径受 `RC_AGENT_FILE_ROOT` 限制；使用 64 MiB 二进制 TLS 流、流式逐块哈希、单块断线重连、每 256 MiB 断点持久化及逻辑/线上/重传速度统计。 |
| Agent/Broker 服务 | 已实现。Agent 使用 LocalService，Privileged Broker 使用 LocalSystem；提权请求需经过已认证控制链路。 |
| UI Agent | 已实现。仅控制配置用户的活动、解锁登录会话，不覆盖锁屏、Winlogon 或 UAC 安全桌面。 |
| UI 自动化 | 已实现主要功能。支持窗口、截图、UI Automation、鼠标、键盘和剪贴板。鼠标 move、button、wheel 是三个独立操作。 |
| 浏览器控制 | 已实现 Chromium 主要链路。支持 Edge/Chrome 启动、导航和受控 CDP DOM 读取。 |
| 目标档案 | 已实现。probe、pair 和认证连接成功后自动登记；同设备/同指纹刷新端点，同设备/新指纹在成功固定验证后替换旧档案并清除其余旧指纹记录。 |
| 更新与部署 | 已实现。完整包更新支持清单、断线重发、状态查询、detached stdout/stderr、结果超时和脚本回滚；更多真实双节点失败场景仍待覆盖。 |
| 二进制更新上传 | 已实现并部署。`binary-update-v1` 使用签名元数据、64 MiB TLS 原始帧、在线 SHA-256 和 DataRoot staging；缺少能力时要求先升级 Agent。 |

## 当前验证矩阵

| 类别 | 结果 | 当前证据与边界 |
| --- | --- | --- |
| Release Build / Publish | Pass | Windows x64 自包含完整包已成功生成；所需可执行文件与部署脚本齐全。 |
| README / 操作 Skill | Pass | README 与仓库 Skill 已同步新版能力要求；本机/对端安装副本均 5/5 哈希一致，旧副本已备份，对端 staging 已清理。 |
| Debug 完整测试 | Pass | SDK 8.0.422；Contracts 118/118、TaskHost 15/15、PrivilegedBroker 3/3、Agent 170/170，共 306/306。 |
| Release 完整测试 | Pass | SDK 8.0.422；Build 0 warnings/0 errors；完整测试同为 306/306。 |
| 断线与检查点 | Cross-machine Pass | 自动化中 copy 上传/下载和 update 上传均在真实 TLS 达到 512 KiB 后中断重连；跨机器会话 `transfer-52ecf7e57e0f453c935daffbda93999d` 在 DataRoot DB 写入 256 MiB 检查点后强制终止 Agent PID，15 秒后以同一 session 自动续传 1 GiB 完成。`bytes=1,073,741,824`、`wireBytes=1,132,462,080`、`retransmittedBytes=58,720,256`，三方 SHA-256 一致；远端路径/staging/触发器/计划任务和本机临时根均已清零。 |
| detached 日志/超时 | Pass / Local | stdout/stderr 持久化、成功退出码边界和 result 缺失超时均有真实 PowerShell/文件系统测试；尚未做对端故障注入。 |
| Lint | 未配置 | 项目没有独立 lint 命令；不把编译成功表述为 Lint 0 errors。 |
| 测试覆盖率 | 未配置 | 当前没有覆盖率配置或报告。 |
| 本机可见 UI 验收 | Pass | 单 UiAgent、最新发布目录链路为 25/25；空剪贴板恢复立即和延迟读回均为 0 字节。 |
| 输入操作分离 | Pass | UI 验收先单独 move，再分别验证 button 和 wheel；滚轮上/下均通过。 |
| 历史 Hyper-V 双机验收 | Pass | 2026-07-16 已验证 UI Automation 25 项、浏览器 DOM、服务恢复、提权、持久任务与文件续传。 |
| 当前跨机器认证与列表命令 | Pass / 当前在线 | 用户确认恢复后的新指纹可信；外部安装后 probe 仍显示同一设备 ID、固定指纹和 `paired=True`，认证命令、job 与 UI 链路可用。 |
| `target` 四子命令实测 | Pass | 在隔离档案中，`add`、`list`、`use` 均退出码 0 且返回/持久状态符合预期；`refresh` 首次因普通终端无权启用 UDP 43000 防火墙规则失败，随后由 Windows `RunAs` 提权 PowerShell 重测退出码 0，并返回同一设备 ID、固定指纹和端点。 |
| 目标档案自动登记 | Cross-machine Pass | 成功连接后同设备旧指纹档案会被替换；正式档案实查 2 项、重复设备组 0，省略端点 `job list` 返回 `ok/0`。 |
| Setup 身份默认值 | Changed | `RemoteController.Agent.config.json` 与 Setup 缺省回退均改为 `RegenerateIdentity=false`；需显式设为 `true` 才轮换身份。 |
| CLI 命令级自动化覆盖 | Improved / Partial | 真实 CLI 进程验证不完整 `job/fs/copy/ui/update` 在当前目标存在时均返回 exit 2 / `invalid_request`；能力缺失拒绝和主要实链均有回归。仍未配置覆盖率报告。 |
| 干净发布包 | Pass | 根目录 19 个文件、无子目录、689,688,218 字节；正式 CLI 已包含本轮档案、缺参和能力强制修改。 |
| 对端 job 命令 | Pass | 新增 `echo RC_JOB_RECOVERY_OK` 任务真实终态为 `Exited`、远端退出码 0、日志标记匹配；历史列表和失败状态过滤也返回预期。 |
| 对端 UI 验收 | Pass | 标准升级后以普通完整性交互任务启动测试程序，400 ms 默认间隔 25/25；真实控件反馈、三键拖动、滚轮、键盘和剪贴板均匹配，测试进程与任务已清理。 |
| 对端升级 | Pass | 最终标准 `update apply --wait` 完整接收 689,531,546 字节，78.3 秒返回 `Succeeded/0`。独立 SYSTEM runner 的 ready/started/result 均存在且 result 为成功，升级任务自清理；随后 probe、认证、关键哈希、job 与 UI 25/25 全部通过。 |
| 自动登记版对端刷新 | Pass / UI status only | 会话 `74fd6f11-8f54-439b-b50b-64df3d2e98c2` 持久 result 为 `Succeeded/0`，接收 689,625,754/689,625,754 字节，更新计划任务已自清理。升级后设备 ID、固定身份、配对保持；认证 stdout/退出码、Agent/Broker Running、UI task Ready/活动会话/1 个显示器、CLI/Agent/Broker/runner 四项 SHA-256 以及省略端点 `job list` 均通过。本轮未重复 25 项可见 UI 验收。 |
| `copy` 实时速度与统计 | Cross-machine Pass | 1 GiB 上传 min/max/avg=47.95/113.39/100.85 MiB/s，下载=108.70/112.47/111.30 MiB/s；三方 SHA-256 一致。默认结构化模式已保持 stdout JSON、stderr 实时进度分离。 |
| 实时速度版对端升级 | Pass / UI status only | 会话 `4542718d-0080-4b33-945e-b07621a11c8b` 接收 689,629,850/689,629,850 字节并返回 `Succeeded/0`；全流程 118.954 秒（约 5.53 MiB/s，不能视作纯上传）。升级后固定身份/配对、认证命令、Agent/Broker、UI task/session/display、四项安装哈希和更新任务自清理全部通过。 |
| `binary-update-v1` 对端验收 | Pass / UI status only | legacy 引导会话 `9783923d-c96c-4c0f-80ff-1e554e48540f`：纯上传 102.916 秒/6.39 MiB/s，全流程 124.234 秒；binary 会话 `066c4c6b-6109-407f-9802-8673368eb599`：64 MiB、纯上传 13.719 秒/47.94 MiB/s，全流程 36.088 秒。二进制分别提升约 7.50 倍/3.44 倍；最终身份、认证、服务、UI、哈希和任务清理通过。 |
| 最终正式包对端升级 | Pass / UI status only | 会话 `13c0a35e-4e9f-414f-94e4-cefa938329a8`：708,981,117/708,981,117，纯上传 8.873 秒、76.20 MiB/s，`wireBytes=bytes`、重传 0，全流程 30.5 秒，`Succeeded/0`。stdout/stderr durable 文件存在；身份/配对、认证、Agent/Broker、UI Ready/session 1/1920×1080、任务清理和四项哈希通过。未重复 25 项可见 UI。 |
| 测试专用恢复 EXE | Cross-machine Pass | 对端运行 EXE 的 SHA-256 与本地产物一致，管理员启动的监听进程通过 status/exec。实测先将 Agent/Broker 停止，再由内置 recover 恢复为 Running，随后 probe、认证命令、job 与 UI 25/25 全部通过；从非管理员进程触发自动 UAC 的分支未单独重演。 |
| `copy` 性能与完整性 | Cross-machine Pass | 二进制流式 v2、64 MiB 分块连续两轮：100 个 1 KiB–5 MiB 文件共 175,713,393 字节，上传 52.57–52.71、下载 69.67–74.71 MiB/s；1 GiB 上传 97.72–99.52、下载 106.06–107.65 MiB/s。逐文件与大文件 SHA-256 全部匹配。 |
| SCP/SFTP 对照 | Cross-machine Pass | 默认 `scp.exe`（SFTP 后端）小文件上传/下载 54.26/75.43 MiB/s，1 GiB 102.13/107.27 MiB/s；传统 `scp -O` 为 50.75/83.74、99.09/108.12 MiB/s。两种模式 SHA-256 全部匹配。 |
| OpenSSH Server | Installed / persistent | 对端 capability 已安装，`sshd` Running/Automatic，防火墙规则启用且 TCP/22 可连接。专用测试凭据已移除，服务与规则按用户要求保留。 |

## 当前缺口

当前没有执行中的目标；跨机器 1 GiB 检查点后断线续传已完成。

1. **排队：**隔离目标验证 detached 失败、result 缺失超时和安装回滚。
2. **排队：**建立 Windows CI、覆盖率和发布制品哈希/许可清单。
3. **排队：**覆盖卸载数据保留、服务账户切换和连续多次断线压力回归。

其余覆盖率、Windows CI、兼容性、压力、服务账户和卸载边界统一归入 `docs/审计.md` 的技术债，不再列为当前目标。

## 历史里程碑

| 日期 | 里程碑 | 当前解释 |
| --- | --- | --- |
| 2026-07-16 | 234 项测试中 1 项 ConPTY 取消时序失败；完成 Hyper-V 双机核心/UI 验收。 | 2026-08-11 确认为测试启动竞态；增加子进程就绪握手和单调计时后 Release 隔离连续 6 次及完整套件均通过。 |
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
