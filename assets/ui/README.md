# 待机页 3D 产品渲染（phone-turntable.png）

- **素材来源**：`D:\Blender\projects\phone-anim\vivo_x500pro.blend`（高精度 vivo X500 Pro 模型）。
- **渲染脚本**：`D:\Blender\projects\phone-anim\scripts\render_ui_vivo.py`
  - 60 帧、20fps、320×400、纯黑背景；
  - 手机绕 Z 轴做 ±25° 的正弦摇摆（首尾可无缝循环）；
  - 屏幕换成**无前摄开孔**的圆角哑光黑玻璃平面；隐藏地面/摄影棚球与品牌字。
- **合成**：10 列 × 6 行 → `phone-turntable.png`（3200×2400）。
- **运行**：`CastReceiver.App/PhoneSpriteView` 只取前 31 帧（-25° → +25° 单调变化）裁成
  `CroppedBitmap`，鼠标左右直接映射旋转角度，上下给视差；连接时回正、放大、淡出。
  不逐帧解码，也不依赖视频解码器。
- **重新生成**：

```powershell
& "D:\Blender\app\blender-4.5.14-windows-x64\blender.exe" -b -P "D:\Blender\projects\phone-anim\scripts\render_ui_vivo.py"
# 再把 renders\vivo_ui_0001..0060.png 按 10×6 合成到本目录的 phone-turntable.png
```

素材缺失时程序会自动回落到 `Phone3DView`（纯 WPF 网格），所以这个文件丢了也不会白屏。
