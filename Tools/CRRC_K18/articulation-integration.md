# K18 通用双节集成：当前合同与验证边界

更新于 2026-10-15。本文描述当前已实现的公共接口；车辆规范以根目录 [BUS_ASSET_SPEC.md](../../BUS_ASSET_SPEC.md) 为准，具体数据以 [vehicle.json](vehicle.json) 为准。先前设计保存在[历史副本](../../out/k18-articulation/history/articulation-integration-before-current.md)，其中旧名称、待定格式和“未实施”表述不再作为当前合同。用户原始历史报告未修改。

## 当前状态

v2 解析、双节驾驶装配、专用接头渲染、原生双节打包和车辆实例后节创建代码均已实现。模型与导出资源已有离线检查，原生导入已成功生成过候选 COK。**冷加载与完整道路使用尚未通过，不能将候选称为已完成游戏验收。**

首次冷加载在菜单就绪前发生原生崩溃。检查发现候选包前后节双向固定依赖，而原生解锁依赖遍历没有递归访问保护；当前修复采用下述单向合同。崩溃归因及修复有效性仍须受控冷加载证明。

K18 尺寸、细节和座位布局仍有照片估算项。151 人及前 85 / 后 66 是模组容量分配，不是制造商分节载客量声明。允许适度超出建议面数，不等于预算或性能验收通过。

## 名称、文件与坐标

| 用途 | 当前值 |
|---|---|
| assembly / 模型资料目录 ID | `CRRC_K18` |
| 原生前节 | `CRRCK18`，`CarPrefab` |
| 原生后节 | `CRRCK18Rear`，`CarTrailerPrefab` |
| 包目录 | `Packs/CDLC.Bus.CRRC_K18` |
| 同内容 manifest 入口 | `CRRCK18.cdlb.json`、`CRRCK18Rear.cdlb.json` |
| 两节 SWB1 | `CRRCK18.swb1.gz`、`CRRCK18Rear.swb1.gz` |
| 独立 CJA1 接头 | `CRRC_K18.joint.gz` |

原生主名无下划线，遵守游戏 `AssetUtils.ParseName`；assembly ID 不得当成 prefab 名猜测。车包无车型代码，依赖公共 `CDLC.BusDriver` 的 0.9.0 v2 接口。

Unity 坐标为 +X 门侧、+Y 向上、+Z 车头，最终源映射 `Unity = (-Bx, Bz, -By)`。SWB1、门、镜、座位及挂点使用本节局部坐标。前节静止原点为装配原点，后节约为 `(0,0,-1.32)`。运行时使用真实两节实体姿态，不能按车长合成尾车姿态。

## 固定 v2 数据合同

顶层 `format="cdlb-vehicle"`、`version=2`。`sections` 恰有两节，每项是 `id`、精确 `prefab`、`restPosition`、单位 `restRotation` 和完整本节 `profile`。

本节复用 v1 字段，并明确 `spec.section`、`axles`、资源 SHA256 和 `paletteRows`。前节两桥，后节一根非转向桥；转向轮固定部件 19/20，非转向轮 21/22，未声明轮为空。三 LOD 检查实际轮几何和 pivot。前 34 部件与 34 起吊环合同保持，接头不占用吊环编号。

`articulation` 声明 `frontSection`、`rearSection`、本节局部 `frontAnchor` / `rearAnchor`、`maxSeparation` 和 `joint={format,mesh,meshSha256,maxBendDegrees}`。当前 joint 格式 `cdlb-joint-v1`、折角 50°；静止挂点要求 1 mm 内重合。CJA1 顶点保留静止装配位置、法线和后节权重，实际两节变换驱动变形，端点贴合各自实体。

`doors[]` 含动画 pivot / 朝向 / `openDegrees`，以及原生 `boardingPosition`、单位 `boardingRotation`、`boarding`、`alighting`。K18 前门上车，中后门下车；门位来自源门槛，仍须实际乘客验证。`nextStop.enabled=false` 禁用参考不足的下一站灯箱。刷卡机按共享生成器和 reader 合同动态生成，不重复烘焙进 SWB1。

`pack.sections[id]` 含 `capacity`、独立 `guid`、`meshSource`，两节容量和等于 `pack.capacity`。`pack.jointSource` / `paletteSource` 指向真实来源。来源与声明 SHA256 不符时拒绝打包。当前 86 行调色板保持旧行编号；两份入口是同一完整 manifest，不另行推测后节配置。

## 原生固定关系与生命周期

新包只写**前节 `CarTractor.m_FixedTrailer → 声明后节`**，后节 `CarTrailerPrefab.m_FixedTractor` 留空。已实读的原生 17 个牵引车与 17 个拖车没有双向固定引用，也没有 `Unlockable`。不增加解锁条件来掩盖依赖环。

运行时遵从原生选择器：前节固定引用必须精确匹配；后节反向限制可为空或等于当前前节，指向其他前节必须拒绝。manifest 身份、包目录、节角色、拖挂类型和源挂点校验继续有效。`TrailerMovementType.Free` 由原生 `CarTrailerMoveSystem` 计算后节转向和速度，不能用 `Locked` 冒充活动铰接。

原生 Bus 选择器只造前节。`BusAssemblySpawnSystem` 在 `Modification2` 为实际新前节创建尾车，使用尾车自己的原生 archetype，填入 `PrefabRef`、初始姿态、seed、`Controller(front)` 和 lane，最后发布 `Layout[front,rear]`。完整 pair 不重复创建，外来或异常布局不覆盖。Depot `Owner` 仅在前节，避免重复计数。

`BusAssemblyLateCreateSystem` 在 `Modification5` 只记录原生 `Modification4B` 晚创建的前节，下轮 `Modification2` 才建尾，使其经过完整初始化。它不扫描旧存档补造尾车。已有 pair 停车/出库按尾车原生 moving/stopped archetype 差集同步，保留实体、乘客和共同字段；删除只处理身份与控制关系匹配的 pair。保存恢复复用原生 `Layout` 序列化及 `ControllerSystem`。以上生命周期仍待实机验证。

## 公共装配接口

- `VehicleProfileJson.LoadForPrefab(path, exactPrefab)` / `VehicleModels.RegisterAssembly`：严格读取并一次注册两节，保留 v1 兼容。
- `VehiclePacks.ForPrefab`：按真实 prefab 与包目录读取入口，无车型字符串特例。
- `BusCabSystem.VehicleAssembly.cs`：核对真实实体、资源、玻璃、挂点和姿态；两节及 `AuthoredJointReady()` 都有效后才隐藏原生车体，失去姿态时恢复原生。
- `BusCabSystem.Articulation.cs` / `BusArticulationSystem`：共用 joint renderer，以认领关系避免驾驶与 AI 重复显示；AI 使用原生插值姿态。
- `BusVehicleAssetSystem` / `BusPaletteImport` / `BusPackageGlass`：逐节建立并验证原生网格、骨骼、灯光、材质、纹理及上下客活动。

接头变形依赖公共 BusDriver renderer。原生汽车分支不等同列车 `VehicleConnection` 骨骼路径；不能宣称只安装 COK 就有完整活动风琴。

## 验收范围

模型/导出/资源报告在 `ModelSource/CRRC_K18` 与 `out/k18-completion/reviewed-candidate`。公共回归与创建规则在 `out/k18-articulation`；单向修正单独记录 `single-fixed-validation.json`，旧报告保留为历史。离线测试不执行真实 Unity EntityManager，不证明冷加载或道路行为。

同一冻结源码、DLL、manifest、COK 与资源组合仍须完成：

1. 原生双节包冷加载、ECS 类型/挂点/光学材质及日志检查。
2. 菜单完整装配三 LOD、三门关/半/全开、正负折角、灯态、线路牌与镜面校准。
3. 测试地图由原生 factory 只造前节，生产系统恰好补一节，验证独立尾车运动与挂点误差、驾驶接管/释放及接头显示。
4. 实际上下客和两节容量、停车/出库、删除、保存重载。85+66 的算术不能代替 151 人实际可用验证。

测试地图与进程操作由主代理统一协调；本文不授权擅自加载或覆盖用户存档、替换正在使用的资源。
