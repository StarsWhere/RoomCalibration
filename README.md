# RoomCalibration

按当前位置和朝向校准房间，支持设置快捷键与准备时间。

支持 Windows x64、Beat Saber **1.45.2_28848**，依赖环境包含本地移植的模组；其他依赖组合的兼容性需另行确认。

## 安装

从 [Releases](https://github.com/StarsWhere/RoomCalibration/releases) 下载压缩包，关闭游戏后将包内目录合并到游戏根目录。所需依赖及版本见 [manifest](scripts/room_calibration/RoomCalibration.manifest.json)。 [LocalModUI](https://github.com/StarsWhere/LocalModUI) 需要单独安装。

## 构建

准备 PowerShell 7、.NET SDK 和已安装兼容依赖的游戏，在 PowerShell 中运行：

```powershell
git clone --recurse-submodules https://github.com/StarsWhere/RoomCalibration
cd RoomCalibration
pwsh -File scripts/build_release.ps1 -GameRoot 'C:\Games\Beat Saber'
```

临时文件位于 `.tmp/`，发布文件位于 `releases/`。

## 许可

[MIT](LICENSE)，第三方声明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
