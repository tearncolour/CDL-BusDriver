# 持续车辆检查

`review_session.ps1` 默认执行 `Run`：复用当前支持协议的 Cities2 进程，没有进程时启动一次。默认串行执行 `Verify → Glass → Vehicle → Mirrors`，完成后保持游戏打开，下一批继续复用。检查使用游戏主菜单中的真实渲染器和已加载资产；这是受控测试场景，不是独立 HDRP 模拟，也不替代道路驾驶验收。

在仓库根目录的 PowerShell 中运行：

```powershell
# 同一进程内完成全部检查，结束保持打开
& .\Tools\VehicleReview\review_session.ps1

# 只做指定阶段；可选 Verify、Glass、Vehicle、Mirrors、ReviewSample
& .\Tools\VehicleReview\review_session.ps1 -Stages Glass,Mirrors

# 只读连接状态；用户使用过存档后，回主菜单再显式恢复检查
& .\Tools\VehicleReview\review_session.ps1 -Command Status
& .\Tools\VehicleReview\review_session.ps1 -Resume -Stages Verify

# 只有这个显式动作才请求正常退出；绝不强杀
& .\Tools\VehicleReview\review_session.ps1 -Command Close
```

`-Model` 默认 `SWB6109EV12G`；`-SourcePath`、`-RuntimePath`、`-PackagePath` 可指定已审核文件。`-OutputDirectory` 必须是新目录，默认使用 `out/vehicle-review-session/<时间>-<随机ID>`。路径来自 `CSII_LOCALMODSPATH`、`CSII_INSTALLATIONPATH`、`CSII_USERDATAPATH`，也可用 `-DeployDirectory`、`-GamePath`、`-UserDataDirectory` 覆盖。

模组的检查协议（每秒心跳、主菜单校验全部车包）只在部署目录存在 `review-enabled` 标记时启动，玩家正常游玩不承担这部分开销。客户端取得锁后自动写入标记（模组每 2 秒查看一次，找到后本进程内保持开启），显式 `Close` 后删除。已退出进程遗留的 `review-request.json`（其 PID/启动时刻不再对应存活进程）会被移到本次输出目录的 `orphaned-review-request.json`，不再阻塞新的会话。

每批与旧资产工具共用锁，请求绑定唯一 ID、PID/启动时刻、程序集 SHA/MVID、嵌入资源及包哈希。客户端不构建、不部署，不热替换 DLL 或包；`-DeployLatest` 当前明确拒绝。离开主菜单会锁存取消，返回后需显式 `-Resume`；`needs_restart` 不能靠恢复清除。超时不会强杀游戏，也不会覆盖仍在执行的请求或旧证据。

每阶段保留请求、响应、截图及日志。模组日志 `CDLC.BusDriver.Mod.log` 的请求 BEGIN/END 标记与 `Player.log` 的 Unity/运行错误分开核对；阶段通过不等于日志之外的人工图审通过。`client-summary.json` 分别记录阶段与进程状态：保持打开时为 `RUNNING`、`exit_code=null`；只有显式 `Close` 后才能记录实际退出码并核对最终日志。日志无错误不能代替进程退出结果。

旧入口 `Tools/SWB6109EV12G/probe_vehicle.ps1` 默认也转到持久会话：普通调用执行 `Vehicle`，`-MirrorProbe` 执行 `Mirrors`，`-ReviewSample` 执行 `ReviewSample`。只有显式 `-ColdStart` 才走原构建/启动/退出流程；独立资产目录及专用材质隔离诊断仍要求该参数。更换 DLL/包、新包冷加载及两个部署目录的独立加载验证仍按关闭→离线部署→启动执行，普通连续阶段不冒称重复冷启动。

离线测试不操作游戏：

```powershell
& .\Tools\VehicleReview\test_review_client.ps1
& .\Tools\VehicleReview\test_review_client_pipeline.ps1
```

第二个测试用 PowerShell runspace 模拟服务端，实际执行客户端四个阶段与再次连接，验证唯一请求、同进程复用及保持打开；不启动 Cities2，也不访问实际部署目录。
