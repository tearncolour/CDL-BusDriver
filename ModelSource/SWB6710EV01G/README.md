# 申沃 SWB6710EV01G / iEV7

独立车型设计源文件。默认采用上海车型常见的浅青绿车身、黑色窗带和白色细饰条。2026-10-14 依据官方正面、侧面及三分之四视角照片重新制作前后围、倾斜风挡和窗带，取代第一版通用小巴外形。

## 交付文件

- `SWB6710EV01G.blend`：米制可编辑模型，包含内外饰和 34 个 CDLC 部件定义。时间轴 0 帧关闭、24–48 帧全开；后门 48–72 帧先关，前门两叶晚 1.2 s 于 77–101 帧再关；双前门、单后门、车轮转向/滚动、方向盘和仪表指针均有独立动作。车门不用铰链：门轴曲柄加门头导轨滚轮，门框端直线移入车内，全开时门扇大部分在车内、只有中缝边伸出车外一点、斜拉杆朝向门口（依据与限制见 `Tools/SWB6710EV01G/door_linkage.py`，逐帧净空见 `door-linkage.json`）。
- `previews/`：从保存后的原件重新渲染前后斜侧、正面、侧面、驾驶室、开门状态，以及前脸轮廓、前玻璃和后部上下角近景。
- `model-spec.json`：已核实尺寸及仍属估算的细节。
- `kit-cabin.json`：由本项目 MeshKit 生成的驾驶区基础数据。实际复用仪表台、方向盘和指针；通用驾驶室壳体不用于新车外壳。
- `sunwin-mesh.bin.gz`：三档运行时网格，保留 CDLC 动态部件和规范坐标；由项目嵌入资源直接读取。
- `animation-report.json`、`wheel-clearance.json`：动作/部件定义以及原始模型四轮轮胎体积避让检查。
- `runtime-validation.json`：对最终压缩资源的三档 LOD 逐面检查，包括法线、退化面、四轮直行净空、前轮 0°/±15°/±25°/±35° 转向净空和文件 SHA-256。
- `front-glazing-report.json`：连续前玻璃的连通性、平面灯牌与弧形玻璃的避让距离，以及前脸曲率定义。灯牌位于玻璃内侧，顶部灯牌区与驾驶视野共用一个玻璃曲面。
- `front-surface-report.json`：整张前脸的 C2 曲率连续性、跨材质统一法线与顶盖过渡检查。保险杠、灯组面、黑边和玻璃均采样同一基础曲面。
- `mirror-surfaces.json`：门侧大镜、门侧独立凸面补盲镜、驾驶员侧小镜的镜面中心、朝向和轮廓。运行时反射面使用同一组参数；启用真实后视镜时，新车型有三个反射视角。
- `mirror-arm-validation.json`：两侧弯臂的解析曲率半径与完整截面包络检查；构建时强制保留至少 5 mm 曲率余量，拦截弯臂内侧折回。
- `mirror-calibration.json`：正面与两侧近侧官方照片的原始像素读数、轴距标尺、镜位校准前后结果和照片估算的局限。长镜壳超出前保险杠约 0.496 m；驾驶侧短镜中心位于保险杠后约 0.573 m，安装座到镜心前伸约 0.068 m。上述为照片校准后的模型值，不是厂家镜架尺寸认证。
- `../../Asset/SWB6710EV01G/`：三档车身/玻璃 FBX，及配套 PNG。`build-report.json` 记录实际面数。

## 参考与精度

[申沃官方 10 系 7 米车型](https://www.sunwinbus.com/mobilecn/product/details/282)：长 7145、宽 2350、高 3000 mm，轴距 5100 mm。前悬 1200、后悬 845 mm及轮距参考[公告车型报道](https://www.cntplus.com/bus/203861.html)。右侧前双门和后单门均位于轴间。

官方照片的缩小研究副本见 `references/`，仅用于本项目的建模核对，图片权利属于原权利人，未烘焙进车辆贴图。官方原图：

- [正面](https://www.sunwinbus.com/UploadFile/image/20260804/20260804092603_4898.jpg)
- [前侧](https://www.sunwinbus.com/UploadFile/image/20260804/20260804092439_6072.jpg)
- [左侧](https://www.sunwinbus.com/UploadFile/image/20260804/20260804092714_5921.jpg)
- [右侧](https://www.sunwinbus.com/UploadFile/image/20260804/20260804092738_3624.jpg)
- [同系列车尾](https://www.sunwinbus.com/UploadFile/image/20260804/20260804092726_4894.jpg)：该图铭牌为 iEV8，尾部细节属同系列参考，尚不能视为 iEV7 的逐项核实。

本模型是按照片重建，未取得厂家 CAD/三视工程图。门宽、玻璃分区、内饰、车顶设备及小附件位置为比例估算；内饰保留 CDLC 的驾驶硬件结构，不声称实车驾驶室的逐件复刻。17 座布局为公开车型允许范围内的设计布局，不对应某辆运营车。



## 重建与自定义涂装

在仓库根目录运行：

```powershell
./Tools/SWB6710EV01G/build.ps1 -Quick
```

需要本机 Blender 5.2、.NET 10 和现有 Unity.Mathematics 引用。脚本重新导出 MeshKit 数据、生成模型和三档 FBX/贴图、渲染并检查文件，不启动游戏。Blender Python 出错会中止构建，不会把旧文件当作成功输出。

只检查几何外观时，可通过 Blender 运行 `Tools/SWB6710EV01G/preview_geometry.py`，其局部预览写入 `out/sunwin-geometry/`，不会覆盖交付的 `.blend`、FBX 或运行时资源。`render_model.py` 则重新渲染已经保存的原件。

Blender 材质 `01 Shanghai aqua body | CDLC line colour` 控制主车身色。FBX 的 BaseColor 在对应区域保持中性白，ControlMask 红通道标记该区域，供 CDLC/游戏线路颜色相乘；玻璃、轮胎、白色饰条不参与换色。默认预览色为线性 RGB `(0.34, 0.69, 0.65)`。

原件同时提供 `CDLCWrapUV` 五区展开坐标，布局见 `CDLC-wrap-guide.svg`（左侧、右侧、前、后、车顶）。自行绘制 PNG 后，可在下面的命令加入 `--texture 路径.png`，生成嵌入图片的 Blender 涂装变体；绘图时应移除指南中的文字与虚线。

可生成单独的蓝色预览变体，保留原模型：

```powershell
& 'D:/SteamLibrary/steamapps/common/Blender/blender.exe' --background --python Tools/SWB6710EV01G/apply_livery.py -- --color 245DB7 --output ModelSource/SWB6710EV01G/SWB6710EV01G-CDLC.blend
```

运行时保留现有 `BusSkins` 的左、右、前、后、顶五面图片涂装接口。涂装面从新车型真实车身三角面重建，避开玻璃、车门和轮拱，而不是沿用通用公交的外壳尺寸。

## 游戏导入边界

FBX 使用已有 CS2 管线约定：厘米数据、预镜像 X、轴变换烘焙；游戏 reader 再翻转 X。Blender 原件维持米制、X 向右、−Y 向前、Z 向上。FBX 在普通查看器里可能呈镜像，游戏原生 reader 的方向才是验收依据。

部件左右按 CDLC 的车辆坐标定义：+X 为车门侧，−X 为驾驶员侧。Blender 与游戏的坐标手性不同，正面预览的画面左右也不同，不能据此交换非对称镜组。静态字体在导出时单独补偿朝向，源文件内仍保持正常可编辑文字。

四轮骨骼为 `WheelFL/FR/RL/RR`；前轮 SteeringTire，后轮 RollingTire。灯光 ID 1–9 对应日行灯、近光、尾灯、制动、左转向、右转向、倒车、车内灯、水牌；每档均保留。材质 atlas 中每个三角形有非退化 UV。

本车型运行时前、侧、后灯牌分别使用 `120×16`、`84×16`、`104×16` 点阵，发光区按相同横纵点距布置。短线路号保持等比例并居中，不会拉满后部宽屏；运行时与软件预览共用点阵定义，其他车型保留原定义。

本交付没有生成 `.cok`，没有部署进正在运行的游戏。可驾驶视角已经加入 `SWB6710EV01G` 名称识别和专用网格分派，使用精确尺寸、三扇门叶、方向盘/仪表/车轮枢轴、17 个乘客座位和驾驶员位置；其他车辆继续使用原有模型。现有 `BusVehicleAssetSystem` 的发布打包入口仍为 `CDLCBus12`，将新车型作为可选公交正式发布还需独立 prefab 打包与 EV 容量/音效设置。原有车辆资源未替换。

校验结果见 `asset-validation.json`、`runtime-validation.json` 和 `native-probe.log`。C# 回归测试包含 `SunwinTests.cs`，检查原有车辆兼容、三片门、仪表和方向盘动作、17 座布局、驾驶视点与五面涂装 UV。渲染不是游戏内性能验证。
