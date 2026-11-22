# 图标资源

- `app-icon.png`：用户提供的校徽经内置 imagegen 抠图得到的 RGBA PNG。仅圆形校徽外部背景透明，校徽内部白色区域保留。
- `app.ico`：从 PNG 转换的 Windows 多尺寸图标，包含 16、20、24、32、40、48、64、128、256 像素尺寸。
- 编译时将 ICO 同时作为 Win32 EXE 图标及托管嵌入资源，窗口和托盘共用该图标。

校徽及其中的文字、标识权利归原权利人。图像处理不改变其权属。

## 抠图提示

工具：内置 `image_gen.imagegen`，`transparent_background=true`。

```text
Use case: background-extraction. Edit target: the attached Guangzhou College of Technology and Business circular blue-and-white seal. Remove ONLY the white square background OUTSIDE the outermost circular blue ring, replacing those exterior corners/background with true transparency. Preserve the ENTIRE circular seal, including all white interior areas, blue outer rings, Chinese characters, English lettering and central blue-and-white emblem exactly in their original shapes and positions. Do not redraw or redesign, do not add lettering, no decorative effects, no shadow, no 3D. Preserve internal white areas opaque. Center the complete circle on a square transparent canvas with very small transparent margin so it is suitable as a Windows application icon. Output an RGBA PNG with a clean anti-aliased circular perimeter. The original text, blue color and circular proportions must remain unchanged.
```
