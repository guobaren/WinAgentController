# 测试远程命令工具

`Rc.TestRemoteControl.exe` 是独立测试工具，不属于 RemoteController 正式发布包。控制端目标固定为 `192.168.3.50:43002`，对端监听器只接受来自 `192.168.3.47` 的连接。

它没有正式控制链路的 TLS、配对和审计保护，只能在已授权、隔离且可信的测试局域网中临时使用。若要在升级失败后恢复服务，必须在开始升级测试前就在对端管理员终端保持监听器运行。

## 构建

```powershell
.\scripts\Build-TestRemoteControl.ps1
```

输出：`artifacts\test-tools\Rc.TestRemoteControl.exe`。

## 对端启动

把同一个 EXE 复制到 `192.168.3.50` 并执行：

```powershell
.\Rc.TestRemoteControl.exe listen
```

如果当前终端不是管理员权限，工具会自动请求 UAC 提权并用管理员令牌重新启动监听器；必须确认该提示，否则 `recover` 无权启动 Windows 服务。

若 Windows 防火墙阻止连接，在对端管理员 PowerShell 中添加仅允许测试控制机的规则：

```powershell
New-NetFirewallRule -DisplayName 'Rc Test Remote Control' -Direction Inbound -Action Allow `
  -Protocol TCP -LocalPort 43002 -RemoteAddress 192.168.3.47 -Profile Private,Domain
```

## 控制端命令

```powershell
# 读取 Agent/Broker/UI 任务状态
.\Rc.TestRemoteControl.exe status

# 启动 Broker、Agent 和 UI 计划任务
.\Rc.TestRemoteControl.exe recover

# 执行任意测试命令并取得实际退出码、stdout 和 stderr
.\Rc.TestRemoteControl.exe exec --shell cmd --command 'whoami'
.\Rc.TestRemoteControl.exe exec --shell powershell --command 'Get-Service RemoteController*' --timeout-seconds 30

# 本机 TCP 分帧、JSON 和命令执行自检
.\Rc.TestRemoteControl.exe self-test
```

关闭对端 `listen` 窗口即可停用此测试通道；它不会注册 Windows 服务，也不会被复制进 `artifacts\publish`。
