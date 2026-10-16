# 内置开场素材（assets/intro/）

uiltin-intro.mp4 是随安装包发布的内置开场片段，由 build.ps1 复制到发布目录的
Assets/intro/ 下（IntroClips.BuiltIn 里的 FileName 必须与它同名）。仓库用
.gitignore 忽略视频本体（体积大），所以**克隆仓库后这里可能是空的**——
此时 build.ps1 会提示缺少内置素材，但不会让构建失败。

## 它是什么

- 源片：作者提供的 4K 片段（3840×1634，24fps，H.264 High，~46 Mbps）
- 处理方式：**ffmpeg 流复制（-c copy）**，只按时长裁剪，**没有任何重新编码**，
  因此画质与源文件逐比特一致（已用"帧哈希逐比特相同"验证过裁剪起点）
- 裁剪窗口：源文件 46.00s → 片尾（源总长 80.71s），产出约 191 MB / 34.76s
- 设置里的播放窗口（IntroStart/IntroEnd）默认 1.55–34.70s，
  即对应源文件 47.55–80.70s——正是作者原本选定的那一段
- 交付体积：安装包因此从 55.7 MB 增到约 246 MB（视频压不动，Inno 的 LZMA 对它几乎无效）

## 想换素材 / 重新裁剪

```
ffmpeg -ss <起点> -i <源视频> -c copy -avoid_negative_ts make_zero -movflags +faststart builtin-intro.mp4
```

-ss 只能落在关键帧上，所以裁出来的开头可能比你指定的点略早——把设置里的
IntroStart 补上这个差值即可（用"导出首帧比对哈希"的办法可以量出精确差值）。
**必须用 -c copy**：一旦重新编码就不再是无损了。