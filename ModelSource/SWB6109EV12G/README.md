# 申沃 SWB6109EV12G / iEV10

独立的 10.5 米公交车型，采用白色车身、黑色窗带与上海青色双饰线。车身、门位、内饰和镜组均独立建模，并使用本车型自己的注册项与资源路径。

2026-10-15 侧窗与无帘配置已更新并部署：按用户要求移除全部窗帘、轨道、绑带和挂扣；两侧 12 块固定窗独立使用略深色透明玻璃，前挡、后窗、门玻璃及司机隔断保持原材质。此配置覆盖参考视频中的窗帘细节，生成脚本与构建检查已固定该要求。最终源模型、三档 FBX/SWB1 和新游戏包一致；33 张源模型预览、60 张离线运行时图、90 张实际游戏整车图、19 张正式后视镜图、两种目录冷启动和玻璃透视检查均完成。游戏整车/玻璃与最终镜面使用的实际 DLL 分别保留绑定；并行车型更新后的 6109 网格、玻璃与镜面代码已核对一致，没有覆盖并行工作。详见 `window-update-report.json`。这些是本次配置修改的验收，道路驾驶、乘客与性能仍未验收，不代表整车稳定发布。

2026-10-15 上一阶段记录：后视镜底座悬空已修正并部署。两侧底座落在固定前角/A柱，转轴和支臂同步接合，三片镜面位置保持。两种目录冷启动、玻璃检查、最终组合的完整回归、90 张游戏整车图和 19 张正式后视镜图均已验证。此前检查工具清理方向光时错误恢复 Lumen，旧轮执行状态保留失败；已修为创建时明确 Lux、补光只管理自身灯光，最终重跑同时通过清理及完整日志中的 Unity/Colossal 错误门槛。测试自动退出加入用户接管保护，加载存档后取消自动退出并锁存。另一次冷启动曾出现加载材质校验失败及退出期原生崩溃，已保留证据并增补只读日志，根因尚未确认；后续通过不能证明该间歇问题已修复。道路驾驶、动态交通和面数优化仍未完成，不作为整体发布通过。

## 参考与精度

[政府网站转载的 2022 年第 1 批推荐目录](https://shanghai.chinatax.gov.cn/zcfw/zcfgk/ccs/202306/P020230615595350340074.pdf)列有准确后缀 SWB6109EV12G，外形为 **10500 × 2550 × 3200 mm**。[第 352 批公告转载](https://zc.cn-truck.com/gonggao/288012_SWB6109EV12G)提供轴距 **5600 mm**、前后悬 **2400/2500 mm**、275/70R22.5 轮胎和前单胎、后双胎配置。所选轮距为公告允许的方盛低地板桥/铝合金轮辋组合：前 2117、后 1876 mm；不将其称为所有 EV12G 的唯一轮距。

0.47825 m 轮胎半径由轮胎规格计算，是名义未加载半径，不是实测滚动半径。[申沃官网 SWB6109EV 系列](https://www.sunwinbus.com/mobilecn/product/details/321)照片用于外形对照，官网页面没有注明 EV12G 后缀；另以准确车型公告图和上海运营实车照片交叉核对。完整来源、身份差异及图片许可记录见 `reference-notes.json`，研究图片保留在 `references/`，不烘焙进模型纹理。

用户明确选择：**保留 SWB6109EV12G 车型，内饰按指定视频 [BV1y7bv6UE2A](https://www.bilibili.com/video/BV1y7bv6UE2A/) 还原**。视频作者标注的车型为 SWB6109EV21G；这是用户选定的内饰参考配置，不声称两个后缀的原厂内饰完全相同。公开预览帧及时间索引保存在 `references/BV1y7bv6UE2A/`，视角始终为后/中排朝前，不能证明镜头后方的布局。

本项目没有厂家 CAD 或座椅工程图。门宽、前后围曲线、灯具、车顶设备、镜架、驾驶眼位及小附件为照片比例估算。前门位于前轴之前，中门位于两轴之间，两门均为双叶。门洞宽 1.30 m、中心 Unity Z 为 +4.30 / −0.30 m。

本轮采用 **27 个乘客座位的设计布局**，位于公告 20–38 座范围内，不是从视频确认的整车座位清单。移除原自拟编号 07–10 的四个无足空间高座，其他高排间距保持；低地板三排整体后移 0.20 m 以避开前轮罩。乘客座面距所属脚下平台为 0.481 m，支腿接到实际地板或末排设备台，驾驶座保持原位。这项可用性修正已在当前保存的源模型逐座检查，三档导出亦完成离线复验。司机眼位、方向盘、座椅、卡机及逐门铰链见 `cabin-layout.json`。

用户提供的 `references/user-reference-20261015-01.jpg` / `-02.jpg` 补充了内饰与门把手依据。座椅采用深灰座面/硬背和银灰圆角顶把手；约 44 cm 把手外宽、32 mm 管径与 55 mm 顶角半径属于照片估算。黄/浅色不规则印花已通过 `Tools/SWB6109EV12G/seat_print.py` 实现并接入修正流程：薄片沿实际座面/背板射线贴合，图案重复和颜色为简化估计，仅用于 LOD0，LOD1/2 省略印花。它不是原厂织物的精确描图；反光也不能作为背板透明的证据。末排靠后围，不设置不可使用的背侧把手与独立后盖。

扶手采用 `handrail-layout-evidence.json` 的逐件参考清单：当前建模对应 **9 个可见吊环实例、5 个可辨竖向构件**，并区分左右高、低纵杆。9 和 5 是已辨认实例数，**不是实车总数**；左侧重叠吊环、镜头后方及杆脚遮挡仍列为 `REFERENCE_INCOMPLETE`。旧版任意三杆/六环和通道横梯架已由该清单替代。保留灰色圆角三角环，80 mm 自由吊带、杆位及安装尺寸均为照片比例/模型拟合估计。

中门后首排的座垫前缘、脚台、轮罩与阶梯连接被前景座背遮挡，现有照片和预览帧不能确认踏步数量、高度或平台前伸深度。`seat-access-validation.json` 用每座 90 个通用乘客脚底采样、膝部与头部净空及 54 个支撑落点核对可坐性；`previews/rear-seat-access.png` 和低视角图用于检查平台与走道。遮挡区域的设计尺寸不能写成实拍量测值，有限坐姿采样也不能代表所有体型。

车头重点标识采用可编辑矢量轮廓：申沃标按高清实车重建 S 形核心与独立圆角窄框，久事标保留真实定制字形，老人/轮椅圆贴按照片估计定位。两圆贴至灯框的模型最近距离分别为 48.3 / 28.0 mm，属于照片重建值，不是厂家尺寸。薄贴花与金属凸标分别处理，避免把圆贴做成厚徽章；来源和定位见 `front-badge-paths.json`、`front-badge-placement.json` 及 `references/front-badges-original-crop.png`。

## 文件与坐标

完整构建的主要输出如下；构建中的中间文件不能代替最终产物验收。

- `SWB6109EV12G.blend`：米制可编辑内外饰和动作装配。0/80 帧为关门、40 帧为开门；四片门叶、四个车轮部件、方向盘、两根仪表指针及独立吊环保留动作。
- `sunwin-mesh.bin.gz`：SWB1 格式、三档 LOD；固定前 34 部件，独立吊环按实际参考清单追加为 `Strap0..N`，各自以吊杆顶端为枢轴，按 X/Z 两轴摆动。吊环数量、吊带长度和位置尚在逐项对照实拍，不以代码常量代替真实性确认。后轴左右部件各含一组双胎，实际共六条轮胎。修订产物须重新导出验证，不能拿旧版本报告代替。
- `model-spec.json`、`cabin-layout.json`、`mirror-surfaces.json`、`sign-surfaces.json`：尺寸、内饰、镜面和发光区坐标。必须按字段声明区分生成器准备坐标、最终 Blender 源坐标和 Unity 坐标，见 `coordinate-frames.json`。
- `kit-cabin.json`：由本项目 MeshKit 导出的驾驶硬件；只复用仪表台、方向盘和指针，未复用通用车身。
- `common-card-reader.json`：直接调用模组通用 `BusInterior.CardReader` 导出的三档网格和设备坐标。通用生成器为 UI 薄片建立真实侧壁与背面，原正面位置不变；Blender/FBX 保留同一模型，运行时按共享方法生成，源副本不重复写入 SWB1。导入器仍能封合旧版开放薄片，但新通用模型无需重复封口。
- `animation-report.json`、`wheel-clearance.json`、`mirror-arm-validation.json`：动作合同、轮胎包络与镜臂曲率检查。
- `runtime-validation.json`、`asset-validation.json`、`build-report.json`：最终运行时、导入资源及构建统计。
- `previews/`：保存后模型的前后、侧面、驾驶区、开门及必要近景。
- `../../Asset/SWB6109EV12G/`：三档车身 FBX、独立玻璃 FBX、配套纹理；不包含已发布游戏资产。

最终可编辑 `.blend` 使用 Blender 右手坐标：−Y 车头、+Z 向上，左舵驾驶区在 **+X**，右侧两门在 **−X**。CDLC/Unity 使用左手坐标：+Z 车头、+Y 向上，驾驶区在 **−X**，车门在 **+X**。最终源到游戏的点/方向映射为 **Unity = (−Blender.x, Blender.z, −Blender.y)**；FBX 导出还执行原生 reader 所需的单位与轴向转换。

旧生成器保留 `canonical_preparation` 准备坐标：+X 门侧、−Y 车头、+Z 向上。`model-spec.json` 中 `kit_offset_blender`、`doors[].centre_blender_y` 等历史字段属于该准备阶段，数值保留用于生成兼容，不能直接当成最终 Blender 世界坐标。准备坐标到最终源须先反射 X：**Blender = (−preparation.x, preparation.y, preparation.z)**。例如 Unity 驾驶眼位 `(−0.69, 1.95, 4.18)` 对应最终 Blender `(0.69, −4.18, 1.95)`，门中心对应源 X = −1.275。详细定义和字段范围见 `coordinate-frames.json`。

必须在最终保存的源文件中从正面、两侧、开门及驾驶眼位核对真实左右。此前把准备坐标直接当 Blender 车辆布置造成的右舵源是模型错误，不能归因于摄像机或查看器；`handedness-validation.json` 是修正后的必要验收项。Unity 运行时坐标和已有驾驶/门位合同保持不变。

## CDLC 使用与导入边界

**自 BusDriver 0.7.0 起，本车是单独的车包 mod `CDLC.Bus.SWB6109EV12G`**：车型资料在 `Tools/SWB6109EV12G/vehicle.json`（打包设置在其 `pack` 块，`strict: true`），`Tools/VehiclePack/make_pack.py SWB6109EV12G` 生成 `Packs/CDLC.Bus.SWB6109EV12G/`（`.cok`、`SWB6109EV12G.swb1.gz`、`SWB6109EV12G.cdlb.json`）。运行时按 prefab 所在 `.cok` 旁的 `.cdlb.json` 认车并注册，缓存键为 `pack-swb6109ev12g-<网格哈希前 8 位>`；测试与预览通过 `TestPacks.Spec("SWB6109EV12G")` 读取车包。以下 0.7.0 之前的记录中，"嵌入资源"指同一份 `sunwin-mesh.bin.gz`，`Mods/CDLC.Buses` 独立加载已由车包文件夹取代。

完整 CDLC 运行时使用车包里的运行时网格 `SWB6109EV12G.swb1.gz`（0.7.0 之前为嵌入资源 `CDLC.BusDriver.Models.SunwinSWB6109EV12G.bin.gz`），包含可动四门叶、车轮转向/滚动、方向盘、仪表、驾驶视点、乘客点、卡机、三面线路灯牌和三片后视镜反射面。镜组由司机侧小镜、门侧大镜及独立凸面补盲镜组成；实际相机反射已用本轮完整玻璃、固定驾驶眼位和三片正式镜面完成受控目标检查，见 `game-mirror-validation.json`；道路交通反射仍待验证。

前/侧/后线路牌使用 **112×16 / 80×16 / 112×16** 点阵，对应 **1.68×0.24 / 0.96×0.192 / 1.54×0.22 m** 发光区。车内下一站屏为 **128×20**、1.28×0.20 m。横纵点距相同，短线路号居中显示，不把字形拉满整块宽屏。

原生 FBX 的骨架为 `Root` 和 `WheelFL/FR/RL/RR`，只承载车身与四轮骨骼数据，**不包含完整 CDLC 四门叶/方向盘/指针动画合同**。Blender 动作源和 SWB1 运行时才保留上述完整独立部件。SWB1 网格与本次离线预览保留玻璃开口，原生 FBX 提供独立玻璃。镜座修正后本轮实际游戏 BusGlass 固定机位红盒透视检查和完整装配的开门玻璃随动已通过；后视镜成像由独立报告验收。

车身白色为线路色控制区域，青色双饰线保留自身颜色。C# 已接入原有左、右、前、后、车顶五面图片涂装接口，从本车 Livery 部件的真实白漆表面生成贴图网格；代码具有五面非空、UV 范围及朝向的回归检查；本轮修正后的结果须以重新生成的验收清单为准。可编辑原始对象保留 `CDLCWrapUV`，合并导出的 FBX 只有 `AtlasUV`，配套 ControlMask 标记线路色区域；`CDLC-wrap-guide.svg` 为源涂装指南。镜壳处于驾驶位可见的 Outside 部件，运行时不随主车身线路色一起换色。

CSL2/CDLC 材质通道已接入：不锈钢使用显式 `BrushedStainless` 色行与 `BrushedMetal` 金属表面；座面及其印花使用 `Textile`，硬质椅背及背面印花使用 `MouldedPlastic`，灰色吊环与金属杆保持不同材质。基础石墨内饰、灰色防滑地板也使用各自明确的 `CabinGraphite`、`CharcoalVinyl` 色行，避免近似选色变成棕色；灯光和线路色保留独立语义。Blender 材质、原生 FBX 材质与运行时调色板能力分别验收。镜座修正后本轮完整车辆的 90 张实际游戏图已逐张检查，涵盖原生 `RenderPrefabRenderer`、CDLC 动作和三面正式 LED；不锈钢、深色座面、灰色塑料及台阶可区分，旧玻璃点噪和车顶串色未再出现。图中使用车外 EV13、车内 EV12 和专用补光，不能据此认定道路光照下的细微拉丝、粗糙度或昼夜效果已通过。

实机对照还发现青色双饰线在 SWB1 中被近似选色误映射到灰色。现明确使用追加的 `ShanghaiAqua` 色行 83，沿用 `MouldedPlastic` 的金属度 0、粗糙度 .28 参数作为运行时有光泽非金属表面的近似；原生和 Blender 保持原材质（金属度 .15、粗糙度 .28），不宣称两条路径完全等价。`aqua-runtime-route-report.json` 记录该次保存重开后的结构比对：仅新增饰线材质的导出元数据，三档 SWB1 每档只改变 15,584 个指定色号字节（80→83），几何、法线、动作及其他颜色均未改变。调色板总行数改变所需的 Kit UV 重归一化单独检查；FBX、PNG 和当时已有游戏包逐字节不变。该报告是饰线修正的历史差异证据；后续灯牌内衬有独立的几何修正，当前源和运行时哈希以重跑后的 `validation-manifest.json` 为准。

本车型的打包设置（0.7.0 之前在 `BusVehicleAssetSystem.s_Models`，现在在 `vehicle.json` 的 `pack` 块）继承电动模板 `Bus02` 并配置总容量 80；该容量与本次 27 个座椅点不是同一概念。用户关闭游戏后，本轮已生成 `Asset/SWB6109EV12G.cok`，完成随模组目录和独立 `Mods/CDLC.Buses` 目录的两次冷启动加载验证，再恢复模组目录中的副本。独立目录测试仍启用了作为校验器的 CDLC 代码，不能表述为禁用全部代码模组后的测试。结果见 `game-package-validation.json`；加载通过不能替代材质画面、实际驾驶和后视镜成像验收。

首次实际打包发现游戏打包器为全非 VT 材质写入全空 `MidMips`，冷启动时报材质去重异常；`BusPackageExport` 现仅从候选包移除完整确认的全空缓存及其 `.cid`，逐项核对其余内容，保留有效缓存。玻璃参数现显式保存 `SurfaceAsset`，独立读取磁盘材质，并在原子发布前通过 `BusPackageGlass` 读取包内参数；失败时保留原包。单级色块贴图修正轮次的包为 `ea3b7571eca216064a458a74e7eab7f17305cab9b96b4d67e1f5a3a2f734eeca`（26,765,108 bytes），导入、随模组冷载、独立目录冷载三次均返回成功且进程正常退出。证据位于 `out/swb6109-game-review/20261015-065949-final-single-mip/`；该轮保留了前一包 `b585fc04…2380b` 供对照。此轮后续人工审图发现前后灯牌内衬接缝，因而不是最终外观通过报告。

上述 `b585`→`ea3b` 的严格差异检查通过：60 项包内容无增删，三份几何逐字节相同；18 张纹理只从 7 级 mip 改为 1 级，第 0 级 BC7 数据及解码像素不变，全部其他纹理头一致，玻璃 Normal/MaskMap 保留 Repeat。四份 Surface 和四份 Prefab 仅更新引用，玻璃三个参数、骨骼及九组灯光用途均保留。这项单车导入设置由 `BusPaletteImport` 自动执行；原包与候选在相同真实 `RenderPrefabRenderer` 下的往返对照，确认单级贴图消除了原生车顶侧面的黄褐串色。它不替代远景采样和道路测试，后续有图案的纹理需重新设计 mip 与边距。灯牌内衬重建后的包另行审计允许的几何差异，不沿用这次“几何不变”的结论。

离线回归、60 张运行时内饰图与射线、主程序集的实际嵌入资源分别记录在 `runtime-integration-review.json` 中，必须绑定当前源和二进制哈希。主项目与测试项目分别保留构建日志；测试项目有两条已有 CS0649 警告。源预览、离线诊断与实际游戏渲染各自保留独立报告，不混用通过结论。

`game-glass-validation.json` 记录其绑定包在固定机位的真实 `BH/GlsShader` 红盒测试与人工审图，包括透射、红盒移除响应和进程退出结果。此前 `ea3b…eeca` 包的红通道比值为 0.728，5280 个有效采样均响应红盒移除，进程正常退出；历史截图为 `out/swb6109-game-review/20261015-065949-final-single-mip/glass/glass-probe.png`。重新打包后须在新包上重跑，单独的透视检查不代替完整装配、车门随动和后视镜验收。

玻璃点噪经完整车辆的两种独立对照定位到透明深度预通道：关闭相机中的该通道，或单独关闭材质的该 pass，均能消除点噪。当前 HDRP 在透明 SSR 开启时会重新启用该 pass，因此本车型的专属 SurfaceAsset 将 `_ReceivesSSRTransparent` 与 `_TransparentDepthPrepassEnable` 同时保存为精确的 `0`，并保留 `_CoatMask=.65`。取舍是停用透明屏幕空间反射，保留天空/环境探针反射及原有折射；未修改全局模板。删除深度偏移关键字的早期对照因安装包变体剥离不能证明实际停用了该功能，未将其作为排除依据。历史对照保留在 `out/swb6109-game-review/` 各轮目录；当前完整装配结果见 `game-renderer-visual-review.json`。

玻璃几何审计覆盖 19 件闭合玻璃、18,255 条窗内射线及包内 171 条玻璃/车身射线，未确认额外重叠层；这些是有限采样。玻璃预通道点噪与车顶色块高 mip 串色是两项分别验证、分别修复的问题。旧 `b585` 包第一次独立加载曾在退出阶段发生 ExtraAssetsImporter 后台贴花导入调用栈的原生崩溃，完整证据仍保留；该历史阶段接受的三次加载及玻璃检查正常退出；本轮间歇故障另见本文当前状态。主菜单检查不能替代实际上车驾驶、道路光照、距离 LOD 和乘客行为验收。

运行时拆分玻璃还保留游戏原生 `SNorm16×2` 八面体法线与 `Float32×1` 位打包切线，避免把它们当普通向量归一化。`BusGlass.Piece` 通过 `MeshData` 逐字节复制顶点流，仅拆分索引，以局部逆静止变换带动门玻璃。`20261015-054832-raw-stream-inspection` 实测 15 块玻璃的 45 条顶点流字节、格式及步长一致，39 个姿态检查通过，最大位置误差 0.1221 mm。首次 63 图因点噪、曝光和工具问题未通过；失败图、无效对照及后续修复证据保存在 `game-renderer-visual-review.json`，不把旧图重新标成最终包验收。

## 重建和验证

在仓库根目录运行，依赖本机 Blender 5.2、.NET 10 及游戏托管程序集：

```powershell
./Tools/SWB6109EV12G/build.ps1 -Quick

$managed = 'C:/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed'
# the official build deploys to Mods\CDLC.BusDriver and deletes it first: LocalModsPath sends it to a folder of its own (BUILDING.md)
dotnet build CDLC.BusDriver.csproj -c Release "-p:LocalModsPath=$deployFolder"
dotnet run --project Tests/CDLC.BusDriver.Tests/CDLC.BusDriver.Tests.csproj -c Release "-p:ManagedPath=$managed"
dotnet Tests/CDLC.BusDriver.Tests/bin/Release/net10.0/CDLC.BusDriver.Tests.dll preview bus-interior spec=swb6109 pose=1 view=all out=out/swb6109-runtime
```

去掉 `-Quick` 使用常规渲染采样。模型构建脚本检查 Blender 异常并生成资产和运行时校验；主项目按官方工具链构建，会把输出部署到 Mods\CDLC.BusDriver 并先删除该目录，`-p:LocalModsPath=<目录>` 改为部署到该目录下，不动已安装的模组。

完整脚本先用 `verify_handedness.py` 检查保存后的源文件真实左右并写出 `handedness-validation.json`，再检查源模型与轮胎姿态，调用原生 reader 验证六个 FBX，并汇总到 `validation-manifest.json`。更换安装路径时可指定 `-NativeProbe`、`-NativePluginDirectory`；`-SkipNative` 仅跳过原生验证，该次清单标为 `INCOMPLETE`，不代表完整验收通过。左右修正后的源文件、FBX 和预览须重新验收；有效源版本以更新后的清单哈希为准。

灯牌装配边界另外执行 `verify_sign_enclosures.py`：检查中央灯牌边环、整片内衬外沿及其与前后围的实体接合，从内外两侧核对首个不透明命中、真实厚度、表面朝向和接合区间，并复查实际截图的白缝像素。中央发光口和真实透明车窗不被当作必须封死的区域。`sign-enclosure-validation.json` 是构建清单及规范状态的必需项；命中远处座椅、顶棚、穿到对面窗，或者只有投影遮住但两实体仍未接合，都不能通过。

本轮灯牌修正把前后黑色内衬做成有厚度的连续结构，内沿回折至原灯牌底座，外沿按实际车壳内表面接合。`sign-enclosure-repair-report.json` 记录保存后源与三档 SWB1 的差异：变化限定在这两片内衬和部件 17，其余 42 个部件、43 组静止变换、原材质与 27 张 PNG 保持一致。错误草稿的开缝、厚度面交叉及后窗底角间隙均有保留的失败回归；最终同版验证器的检查结果见 `out/swb6109-sign-enclosure/repair-v2/`。这些是模型装配检查；修复后包的独立加载与实际渲染结果另见 `game-package-validation.json` 和 `game-renderer-visual-review.json`。

前一轮灯牌内衬修正的历史源文件 SHA-256 为 `7904eddbccc3aa93adacfdbdb2e9cc5f56f9ef3e7fdf9aa47c36fd76d279068d`，运行时为 `49d0f2e6b24000c3ee562386f4dd741569469e93d0918577e83ff09e11fdfe8d`。保存重开后的 15 项模型检查、33 张源预览、60 张离线运行时图及 1,524 项回归均通过；灯牌内衬检查包含 23,706 项采样与 812 项真实接合验证。有限采样不能替代其他视角或后续游戏检查。

灯牌内衬修正的历史包 SHA-256 为 `0d000024006521b604265132cae4819e1ac6b9bb51078dd063267a701a36ca33`，大小 26,920,197 bytes。相对旧 `ea3b` 包的严格审计确认：18 张纹理和三档玻璃顶点流/索引逐字节不变，车身变化只对应源文件中两片内衬。两处非共面四边形的角点法线差异已用实际 Blender 导出复制、变换及翻面步骤重放解释，包内角点与重放一致，仍朝向实际三角形正面。证据保存在 `out/swb6109-game-review/20261015-074732-enclosure-final/`；首次失败审计与后续定位证据均保留。

当前镜座修正包 SHA-256 为 `8ae3167a3eecdbd521b4e79c74be78ac9adbfa78a621e0afd0f234a0c44afa63`，大小 26,940,586 bytes。相对上述历史包，每档仅替换 20 个原有支撑零件的 7,952 个三角形；其余原始三角形、18 张纹理、三档玻璃顶点流/索引、骨骼和灯光配置保持。证据保存在 `out/swb6109-game-review/20261015-115124-mirror-mount-final/`。本轮 90 张完整装配图已逐张复核，包括三档运行时内外饰、原生整车、座椅台阶、门姿态及正式 LED；补光和未验证范围见 `game-renderer-visual-review.json`。

三档原生车身分别为 **677,356 / 582,460 / 552,556** 三角形，SWB1 分别为 **669,524 / 574,628 / 544,724**。它们和包大小均明显超过规范建议预算，尚不能作为已完成性能优化的发布版本。

规范状态分别检查游戏包加载、玻璃、整车实际渲染、后视镜和道路使用报告。`game-renderer-visual-review.json` 与 `game-mirror-validation.json` 属于独立门槛：自动截图完成或进程正常退出，不能代替逐图检查。缺失报告和旧资源的报告不会被当作本轮通过。

### 内饰验收要求

每次完成建模、左右坐标修正、动作或 LOD 调整后，须重新检查最终保存的源文件和三档导出内饰。不能用外观正常、旧版内饰图或数值测试通过替代这项验收。

- 逐区检查驾驶区、前后乘客区、轮罩、地板及高低地板连接、前后内围、侧窗内衬、顶棚、座椅/支架、扶手/吊带、门框和踏步，确认没有非设计孔洞、错位、脱开、悬空或非预期交叉；真实门窗开口的内外封边须连续。
- 从最终源文件的实际驾驶眼位、前后走道互看和前后低视角检查完整装配，补看脚坑、座椅底部、轮罩内侧及顶棚连接近景。驾驶座、方向盘、踏板和仪表台须落在合理相对位置，仪表、前方路面与镜面不被错位内饰遮挡；剖切/隐藏只用于定位问题。
- 在门关闭、中间和全开姿态检查四门叶周围的门框、踏步、扶手、隔断、座椅与轮罩净空；同步核对方向盘和仪表动作，避免静止图通过但动作穿模。
- 三档 LOD 均检查驾驶区、前后走道和低视角的背面诊断及实际外观，特别复查减面后的座椅支架、扶手端口、轮罩、地板与门框连接。不能靠双面材质、强制法线或遮挡来隐藏开口。
- 人工内饰报告须绑定最终 `.blend` 与运行时资源哈希，记录视角、LOD、动作姿态及仍未解决的问题；可疑像素须按真实命中面复核。坐标修正后的新 master 尚未检查时，不沿用旧版 `interior-visual-review.json` 的通过结论。

单独复验三档运行时或纹理：

```powershell
& 'D:/SteamLibrary/steamapps/common/Blender/5.2/python/bin/python.exe' Tools/SWB6109EV12G/verify_runtime.py
& 'D:/SteamLibrary/steamapps/common/Blender/5.2/python/bin/python.exe' Tools/SWB6109EV12G/verify_assets.py
```

最终源文件与二进制保存完成后，可独立重跑主项目、回归和 60 张运行时内饰诊断图。为每轮使用新的输出目录，保留旧版证据；这些命令不部署或启动游戏。

```powershell
$reviewDir = 'out/swb6109-handedness/runtime-review/new-round'
$managedDir = 'C:/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed'
dotnet build CDLC.BusDriver.csproj -c Release --disable-build-servers "-p:ManagedPath=$managedDir" -p:DeployToGame=false
dotnet run --project Tests/CDLC.BusDriver.Tests/CDLC.BusDriver.Tests.csproj -c Release --disable-build-servers "-p:ManagedPath=$managedDir" -p:DeployToGame=false
& ./Tools/SWB6109EV12G/render_runtime_review.ps1 -OutputDirectory $reviewDir
& 'D:/SteamLibrary/steamapps/common/Blender/blender.exe' --background --python-exit-code 1 --python Tools/SWB6109EV12G/audit_runtime_images.py -- $reviewDir
```

`render-manifest.json` 绑定本轮程序集和嵌入资源哈希、三档 LOD 及实际门角；`pixel-attribution.json` 保留每个品红诊断像素及其真实命中面和邻近射线。脚本验证嵌入资源与磁盘二进制相同，并在文件变化时拒绝继续。材质路由可独立运行 `Blender --background --python-exit-code 1 --python Tools/SWB6109EV12G/verify_material_routes.py`；它只在临时目录构建小型夹具，验证显式不锈钢映射以及灯光/涂装优先级，不改正式模型。

以下是**历史诊断记录，针对旧 48 部件中间版本，不是本轮修正的验收结果**：21,008,628 bytes，三档 530936 / 459864 / 421064 个源三角形，SHA-256 为 `AB3E059F2BB099CD03C5B315547DDE034A214381828A137FC526A70A0070D648`，对应源文件 `ac7d29787dba8fd62fe482702273c5ce5f0ee76cd56d2b9a74d95161ab667e4d`。该轮主项目 Release 为 0 警告/0 错误，回归 1464 项通过、0 失败；测试项目有一条既有的 `Preview.ViewDef.Ortho` 未赋值警告。源/主程序集/测试程序集嵌入资源哈希一致，记录在 `out/swb6109-handedness/runtime-review/photo-final/`。该记录仅描述 SWB6109EV12G 的当时状态。

该历史轮次的 60 张三档内饰图覆盖驾驶区、前后走道、低位地板、双侧轮罩、顶棚，以及两门 0°/43°/86° 姿态。293 个背面诊断候选逐个重验，287 个最近命中为正面；余下 6 个为中门地板同两像素在三姿态重复，当时定位为中央地板与外侧地板倒角拼接细缝。该问题以及刷卡机、镜组的旧图结论均须在本轮最终资源上重验。`runtime-integration-review.json` 在该历史轮次记为 `NEEDS_SOURCE_REVISION`，不能作为当前通过报告。

本轮已实现逐件证据驱动的高/低纵杆与短吊带、删除旧通道横跨杆和后排框架、重做座椅弧肩/腰线/不锈钢上把手，并接入简化印花及 27 座可用性修正。v5 诊断已被 v6 替代；其轮罩分段折光与当时旧 SWB6710 三项回归失败保留在 `out/swb6109-handedness/runtime-review/reference-v5/`，不能作为当前状态。

**v6 离线验收**绑定运行时 `5813AC07BB31F61877FA7B50D5422C3625369A21D3D71162C63F833BAAE94827`（26,031,479 bytes；三档 656708 / 561012 / 530468 源三角形）与最终源 `408d171148d8dada7d37d780082758477d519e03cbd9769f8039a5f3f147fa75`。该版已修正轮罩弧面平滑与封边，保留原坐标、拓扑及 43 个部件的 rest frame。主项目非部署 Release 编译为 0 警告/0 错误，6109 目标回归 295 项通过；随后锁定不可变程序集副本运行完整套件，1472 项通过、0 失败。同期旧车型代码/测试的变更已单独绑定实际程序集；没有用修改本车型或跳过检查来消除旧失败。

v6 的 60 张三档内饰图覆盖驾驶区、前后低走道、四处轮罩、顶棚与两门 0°/43°/86° 姿态，另逐图检查开闭门对照。63 个品红候选的最近命中均为正面，252 条邻域射线也没有背面；人工复看未确认新的开孔、穿插或轮罩分段折光。有限视角的零背面结果不能证明任意方向均无孔洞，仍须结合源几何、净空与实际游戏检查。记录位于 `out/swb6109-handedness/runtime-review/reference-v6/`，`runtime-integration-review.json` 只认定本轮离线范围通过；打包、两种加载方式、玻璃及实际游戏画面须按 [BUS_ASSET_SPEC.md](../../BUS_ASSET_SPEC.md) 的独立报告验收，不能据此宣称整体发布合格。

本次镜座修正后的源 SHA-256 为 `bea2201597a53515008edc4092c2a8c761632a364e81afe97a6ae8e48e19fd5e`，SWB1 为 `34e2514a049f491fbbc13ea7a25ad3c1b52f1c76f36dad07278b4eca2cf6a291`。20 个支撑零件变更，其他 1,070 个源对象几何保持；三档运行时仅 Outside(15) 发生变化，其他 42 个部件、全部静止变换、面数及 27 张 PNG 不变。底座接触、支臂曲率与实体关节检查不能替代新包和游戏内复测。
