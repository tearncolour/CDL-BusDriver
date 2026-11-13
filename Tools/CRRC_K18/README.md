# 中车 K18 模型与车包

按用户选择的官网新巴客龙展示车制作。正式源是 `ModelSource/CRRC_K18/CRRC_K18.blend`；当前验收状态、哈希和未完成项以同目录 `construction-status.json` 为准。Blender 图不代表 CSL2 材质验收。

## 源与参考

前后节、风琴罩、转盘、门、车轮、驾驶区和客舱均为可编辑几何。统一使用 Blender +X 驾驶员侧、−Y 车头、+Z 向上；Unity=(-Bx,Bz,-By)。灯具包括熄灭时的壳体、透明罩和反射器，发光件独立。原厂六轮廓徽章来自实车与官方标志核对。

`reference/references.json`、`completion-reference.json` 记录原图、来源和适用范围。厂家网页的不同参数块互相矛盾；公告型号、完整工厂座位图及部分后围细节没有充分证据。34 座布局和被遮挡结构为保留净空的估计，不标成实测。前后节 85/66、合计 151 人是本车包的可调运营容量。

## 构建顺序

使用本机 Blender 5.2（路径可调整），所有命令从仓库根目录运行：

1. `blender --background --python-exit-code 1 --python Tools/CRRC_K18/build_model.py`（依次调用 `interior_completion.py`、`saloon_refinement.py`、`cab_refinement.py`、`mirror_refinement.py`；驾驶台与客舱按官方照片 `reference/official-k18-*.jpg`、`official-c18-en-*.jpg`、`new-bakelong-2024-04.png`）
2. `verify_source.py`、`verify_front.py`、`verify_completion.py`：通过 `--blend` 指定上述源；分别用 `--output`、`--report` 保存新报告。
3. `blender --background --python-exit-code 1 --python Tools/CRRC_K18/export_model.py`
4. `blender --background --python-exit-code 1 --python Tools/CRRC_K18/export_articulation.py -- --blend ModelSource/CRRC_K18/CRRC_K18.blend --output-dir ModelSource/CRRC_K18/runtime-sections`
5. `build_profile.py --blend ... --section-summary ModelSource/CRRC_K18/export-summary.json --joint-report ModelSource/CRRC_K18/runtime-sections/joint-export.json`（仍在 Blender 中运行）。只接受同一源哈希的两份导出。
6. 使用游戏原生 `PipelinePlugin` 检查 12 份 FBX；`verify_native.py` 将 reader 日志绑定到导出摘要。`verify_export.py` 独立解码两份 SWB1、全部 PNG 和各三角角点。
7. 游戏关闭时运行 `cs2_research/bus_asset/pack_asset.sh CRRC_K18`：游戏导入 `Asset/CRRCK18`、`Asset/CRRCK18Rear` 打成 `Asset/CRRC_K18.cok`，`make_pack.py --require-cok` 生成并安装 `Mods/CDLC.Bus.CRRC_K18`，再冷启动按前节 prefab `CRRCK18` 校验。发布包：`make_pack.py CRRC_K18 --require-cok --zip`。

公共接口为 CDLB v2 显式双节清单和独立 CJA1 活动接头，详见规范附录 A。固定 34 部件和独立吊环编号不变，不用吊环部件伪装铰接。原生 AI 有真实牵引节/拖挂节；可变风琴依赖 BusDriver 公共运行时。

## 检查与游戏测试

源时间轴：1 直行关门，85 三门打开，160/190 为正负 25°，250/280 为正负 50°。两侧镜座均检查真实固定壳体接触；辅助镜是 10 mm 凸面球冠。客舱检查含座脚/椅背支承、脚部/膝部空间、后排台阶、内围和轮罩、门关闭/半开/全开、前轮七档转角及各档法线。

`render_front_review.py` 支持照片标定机位、灯具明暗与近景；`verify_completion.py --render-dir ...` 产生完整装配正常/背面诊断图。必须人工审阅最终源生成的图。

三档面数高于规范建议值，具体数量在状态报告中逐项列明。不得把用户允许超预算理解为无需实际游戏性能测试。

游戏中使用 `Tools/VehicleReview/review_session.ps1 -Model CRRCK18 -Stages Verify,Glass,Assembly`（`-Model` 是原生前节名，不是逻辑包名；`Vehicle`/`Mirrors` 阶段含 SWB6109 专用材质夹具，不适用本车），明确指定源、前节 SWB1 和 COK 路径。此入口复用进程，完成后不退出游戏。主菜单夹具不代替道路 AI、接管/释放、进出站、昼夜玻璃和保存重载验证。不得覆盖用户正在运行的 DLL 或资源。
