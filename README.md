# 手机音频控制台 · Phone Audio Console

> 基于 [scrcpy](https://github.com/Genymobile/scrcpy) 的 Android 手机音频转发桌面控制台

把安卓手机的音频实时转发到电脑，用一个图形界面管理连接、播放和音量。
不再需要手敲命令行参数，也不会有个黑色命令行窗口挂在桌面上。

> [!NOTE]
> **本项目由 AI 辅助完成。**
> 代码、界面与文档均在 AI 结对协作下产出，经人工实机测试与验收。
> 提交与维护遵循常规开源流程；欢迎通过 Issue / PR 反馈问题。

本项目是 **scrcpy 的独立 GUI 外壳**：通过命令行调用 `scrcpy.exe`，不修改、不包含、不链接 scrcpy 的任何源码。

---

## 功能

| 功能 | 说明 |
|---|---|
| 一键连接播放 | 填写手机 IP 即可，自动补全 `:5555` 端口 |
| 音量调节 | 控制 scrcpy 这一路输出，不影响系统总音量 |
| 明暗主题 | 宣纸 / 墨夜两套配色，一键切换 |
| 音源选择 | 输出声音、麦克风、通话等 11 种来源 |
| 码率选择 | 64K ~ 512K 六档 |
| 系统托盘 | 关窗口不退出播放，右键菜单控制 |
| 自动清理 | 退出时关掉 scrcpy 与 adb，不留后台进程 |
| 无窗口模式 | scrcpy 不弹出镜像窗口，只有音频 |

## 界面

玻璃拟态 + 水墨质感，横版 640×440。
左侧声波封面与播放时长，右侧状态、音量、参数与主按钮。

## 环境要求

| 依赖 | 版本 | 说明 |
|---|---|---|
| Windows | 10 / 11 x64 | 仅支持 64 位 |
| [.NET Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) | **8.0** | 运行本程序必需 |
| [scrcpy](https://github.com/Genymobile/scrcpy/releases) | **5.0.1** | 核心音频转发引擎 |
| Android | 11+ | 需支持音频转发（scrcpy 5.x 要求） |

## 快速开始

### 1. 准备 scrcpy

下载 [scrcpy 5.0.1](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1) Windows 版并解压，记住路径（例如 `D:\scrcpy-win64-v5.0.1`）。

### 2. 编译

```powershell
cd src
dotnet build -c Release
```

或用 Visual Studio 2022 打开 `src/PhoneAudioConsole.csproj`。

### 3. 部署

把编译产物里的 `手机音频控制台.exe`、`.dll`、`.deps.json`、`.runtimeconfig.json`
复制到 **scrcpy 解压目录**（需与 `scrcpy.exe`、`adb.exe` 同级）。

### 4. 连接手机

1. 手机开启「开发者选项 → USB 调试」和「无线调试」
2. 查看手机 IP：设置 → 关于手机 → 状态信息
3. 在控制台填入 IP，点「开始播放」

> 首次连接需在手机上确认授权弹窗。

## 技术说明

### 音量控制

scrcpy 本身没有任何音量参数，Adobe 的 `--audio-buffer` 也只控制缓冲不控制音量。
本项目通过 **Windows Core Audio API** 直接调节 scrcpy 进程的音频会话音量：

```
面板滑杆 → 找到 scrcpy 进程 → 枚举音频会话 → ISimpleAudioVolume.SetMasterVolume
```

效果等同于在 Windows 音量合成器里单独拖动 scrcpy 那一格，**不影响系统总音量**。

滑杆范围为 0–100%，默认 100% 表示不衰减，只能往低处调。

### 无窗口模式

启动参数包含 `--no-window`，scrcpy 不创建镜像窗口但音频照常输出，
因此不会有额外的安卓画面弹出来。

### 退出清理

按顺序执行三步，避免后台残留：

1. 终止 scrcpy 进程（含子进程）—— 释放音频会话
2. `adb disconnect <设备>` —— 断开无线连接
3. `adb kill-server` —— 关闭 adb 服务

第 3 步有保护：启动前若检测到 adb 已在运行（比较进程启动时间），
会判定为其他程序的 adb，**不会**被关闭。

## 已知限制

- **部分国产 ROM 禁用了 adb 修改音量**（如 OriginOS），本项目的方案绕开了这个问题，但意味着面板无法控制手机侧音量
- 音频延迟取决于 scrcpy 的默认缓冲（50ms + 10ms），本项目不提供缓冲调节
- 仅支持无线连接（USB 连接需自行改用序列号）
- 框架依赖发布，目标机必须安装 .NET 8 运行时

## 项目结构

```
src/
├── App.xaml / App.xaml.cs        程序入口、单实例锁
├── MainWindow.xaml / .cs         界面与全部交互逻辑
├── AdbService.cs                 adb 命令封装（带超时）
├── WindowsVolume.cs              Core Audio 会话音量
├── Models.cs                     状态枚举、音源与码率选项
├── Styles.xaml                   样式资源
├── app.ico / app.manifest        图标与 DPI 清单
└── PhoneAudioConsole.csproj      项目配置
```

## 隐私说明

本项目**不收集、不上传任何数据**：

- 无遥测、无统计、无网络上报
- 所有配置（手机地址、码率、音量）仅保存在本机
- 不包含任何开发者的个人信息、设备标识或硬编码地址
- 源码中的默认地址 `192.168.1.100` 仅为占位示例

## 致谢

本项目的音频转发能力完全来自 [scrcpy](https://github.com/Genymobile/scrcpy)（Apache-2.0），
由 [@rom1v](https://github.com/rom1v) 开发维护。请优先支持原项目。

## 许可

[MIT](LICENSE)
