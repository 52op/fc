# Windows 软件数据/缓存目录环境变量重定位调研报告

> 结论前置：**几乎没有 Windows 软件把"数据目录"绑到专用环境变量**（除 vcpkg 与 Podman 这类开发/容器工具）。浏览器、IM、游戏平台、Windows 邮件全部为 **不支持 env，仅参数/配置文件/注册表**。这些条目归属"可用 FC junction 迁移"。

## 图例
- **Official** = 官方文档/官方源码明确支持该环境变量
- **Community** = 源码/社区支持存在但无官方文档，或仅配置文件/参数/注册表可改（非 env）
- **Unsupported** = 不支持通过环境变量重定位（数据目录），仅设置/参数/注册表/junction

---

## 一、包管理器 / 构建工具

| 软件 | 默认数据目录 (Windows) | 环境变量 | 可信度 | 依据 |
|---|---|---|---|---|
| **vcpkg** 二进制缓存 | `%LOCALAPPDATA%\vcpkg\archives` | `VCPKG_DEFAULT_BINARY_CACHE` | Official | [MS Learn: Binary Caching 默认路径](https://learn.microsoft.com/en-us/vcpkg/users/binarycaching) |
| **vcpkg** 下载目录 | 内嵌 `<vcpkg-root>\downloads\` | `VCPKG_DOWNLOADS`（须为绝对路径） | Official | [MS Learn: vcpkg 环境变量](https://learn.microsoft.com/en-us/vcpkg/users/config-environment) |
| **vcpkg** 实例根 | 可执行文件所在目录 | `VCPKG_ROOT` | Official | [MS Learn: vcpkg 环境变量](https://learn.microsoft.com/en-us/vcpkg/users/config-environment) |
| **vcpkg** 注册表缓存 | （内嵌） | `X_VCPKG_REGISTRIES_CACHE` | Official | [vcpkg issue #38329](https://github.com/microsoft/vcpkg/issues/38329) |
| **Chocolatey** 安装目录 | `C:\ProgramData\chocolatey` | `ChocolateyInstall` | Official | [Choco Setup 安装到不同位置](https://docs.chocolatey.org/en-us/choco/setup/) |
| **Chocolatey** 工具目录 | `C:\tools` | `ChocolateyToolsLocation` | Official | [Choco Get-ToolsLocation](https://docs.chocolatey.org/en-us/create/functions/get-toolslocation/) |
| **Chocolatey** 下载缓存 | `%LOCALAPPDATA%\Temp\chocolatey`（默认走 `%TEMP%`） | **无 env**，用 `choco config set cacheLocation` | Community | [Choco 修改缓存目录](https://docs.chocolatey.org/en-us/guides/usage/change-cache/) |
| **winget** 可移植包根(用户) | `%LOCALAPPDATA%\Microsoft\WinGet\Packages` | **无 env**，settings.json `portablePackageUserRoot` | Official | [MS Learn: winget settings](https://learn.microsoft.com/en-us/windows/package-manager/winget/settings) |
| **winget** 可移植包根(机器) | `%PROGRAMFILES%\WinGet\Packages` | **无 env**，settings.json `portablePackageMachineRoot` | Official | [winget-cli Settings.md](https://github.com/microsoft/winget-cli/blob/master/doc/Settings.md) |
| **winget** 默认安装根 | 包清单要求时按包 ID 追加 | **无 env**，settings.json `defaultInstallRoot` | Official | [winget-cli Settings.md](https://github.com/microsoft/winget-cli/blob/master/doc/Settings.md) |

---

## 二、容器 / 虚拟化

| 软件 | 默认数据目录 (Windows) | 环境变量 | 可信度 | 依据 |
|---|---|---|---|---|
| **Docker** 客户端配置 | `%USERPROFILE%\.docker` | `DOCKER_CONFIG`（仅重定向客户端配置，**非数据/镜像目录**） | Official | [Docker CLI env 变量](https://docs.docker.com/reference/cli/docker/) |
| **Docker** 守护进程数据根 | Linux: `/var/lib/docker`；Windows 容器: `C:\ProgramData\Docker\config\daemon.json` 内 `data-root` | **无 `DOCKER_GRAPH`**（已软废弃，`data-root` 取代） | Official | [Docker daemon 配置](https://docs.docker.com/engine/daemon/)、[docker/docs #5922](https://github.com/docker/docs/issues/5922) |
| **Docker Desktop** WSL vhdx | `%LOCALAPPDATA%\Docker\wsl\data\ext4.vhdx` | **无 env**，仅安装参数 `--wsl-default-data-root` / junction | Official | [docker/for-win #7348 迁移 vhdx](https://github.com/docker/for-win/issues/7348)、[SO 重定位 docker images](https://stackoverflow.com/questions/62441307) |
| **Docker Desktop** 设置 | `%APPDATA%\Docker\settings-store.json` | **无 env**（JSON 设置） | Official | [Docker Desktop settings](https://docs.docker.com/desktop/settings-and-maintenance/settings/) |
| **WSL** 发行版 vhdx | `%LOCALAPPDATA%\Packages\<Distro>\LocalState\ext4.vhdx` | **无 env**；位置存注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss` 的 `BasePath` | Official | [MS Learn: 管理 WSL 磁盘空间](https://learn.microsoft.com/en-us/windows/wsl/disk-space) |
| **Podman** rootless 存储 | `%USERPROFILE%\.local\share\containers\storage` | `XDG_DATA_HOME`、`CONTAINERS_STORAGE_CONF`（storage.conf 位置） | Official | [Podman 环境变量/man](https://docs.podman.io/en/stable/markdown/podman.1.html) |
| **Podman** 镜像临时下载 | Linux: `/var/tmp` | `TMPDIR` | Official | [Podman --tmpdir/TMPDIR](https://docs.podman.io/en/stable/markdown/podman.1.html) |

---

## 三、Windows 系统级

| 软件 | 默认数据目录 (Windows) | 环境变量 | 可信度 | 依据 |
|---|---|---|---|---|
| **Windows 临时目录**(用户) | `%LOCALAPPDATA%\Temp` | `TEMP` / `TMP`（用户级） | Official | [MS Q&A: 改 TEMP 到另一盘](https://learn.microsoft.com/en-us/answers/questions/2421592/) |
| **Windows 临时目录**(系统) | `C:\Windows\Temp` | **不受用户 `TEMP`/`TMP` 影响**（系统级独立） | Official | [TenForums: TEMP 位置](https://www.tenforums.com/general-support/194825-can-windows-temp-folder-location-changed.html) |
| **OneDrive** | `%USERPROFILE%\OneDrive` | **无 env**；移动靠注册表 `HKCU\...\User Shell Folders` 或组策略 `KFMSilentOptIn...` | Community | [MS Learn: 重定向已知文件夹](https://learn.microsoft.com/en-us/sharepoint/redirect-known-folders) |
| **Outlook** OST/PST | `%LOCALAPPDATA%\Microsoft\Outlook` | **无 env**；注册表 `ForceOSTPath` / `ForcePSTPath` | Official | [MS Learn: 无法改 OST 位置](https://learn.microsoft.com/en-us/troubleshoot/outlook/data-files/cannot-change-the-location-of-ost-file) |
| **Windows 邮件/日历(UWP)** | `%LOCALAPPDATA%\Packages\microsoft.windowscommunicationsapps_*\LocalState` | **无 env** | Community | [MS Q&A: Mail 占用](https://learn.microsoft.com/en-us/answers/questions/5572079/) |

---

## 四、浏览器（均为"不支持 env，可 junction"）

| 软件 | 默认数据目录 (Windows) | 环境变量 | 可信度 | 依据 |
|---|---|---|---|---|
| **Google Chrome** | `%LOCALAPPDATA%\Google\Chrome\User Data` | **Windows 无 env**，仅 `--user-data-dir` 参数（env `$CHROME_USER_DATA_DIR` 仅 Linux） | Official | [Chromium: User Data Directory](https://chromium.googlesource.com/chromium/src/+/HEAD/docs/user_data_dir.md) |
| **Microsoft Edge** | `%LOCALAPPDATA%\Microsoft\Edge\User Data` | **无 env**，仅 `--user-data-dir` / 策略 `UserDataDir` | Official | [MS Learn: Edge UserDataDir 策略](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/userdatadir) |
| **Firefox** | `%APPDATA%\Mozilla\Firefox\Profiles` | **无专门 env**；`-profile` 参数 + 改 `profiles.ini` 的 `Path` 指向任意位置 | Community | [Mozilla: 配置文件位置](https://support.mozilla.org/en-US/kb/profiles-where-firefox-stores-user-data)、[Mozillazine: Profile 文件夹](https://kb.mozillazine.org/Profile_folder_-_Firefox) |
| **Electron 应用(通用)** | 依应用名 `%APPDATA%\<AppName>` | **无 env overlay**；`app.getPath`/`app.setPath` 为应用内 API，支持 `--user-data-dir` | Official | [Electron: app 文档](https://electronjs.org/docs/latest/api/app) |

---

## 五、IM / 协作软件（均不支持 env，设置内或 junction）

| 软件 | 默认数据目录 (Windows) | 环境变量 | 可信度 | 依据 |
|---|---|---|---|---|
| **微信 3.x** | `%USERPROFILE%\Documents\WeChat Files` | **无 env**；仅设置内改 | Community | [华为: 微信记录备份恢复](https://consumer.huawei.com/cn/support/content/zh-cn16061661/) |
| **微信 4.x** | `%USERPROFILE%\Documents\xwechat_files` | **无 env**；仅设置内改 | Community | [知乎: 微信4.0 迁移](https://www.zhihu.com/question/1928764136638093009) |
| **QQ** | `%USERPROFILE%\Documents\Tencent Files` | **无 env**；设置内"个人文件夹保存位置"改 | Community | [CSDN: QQ 记录迁移](https://blog.csdn.net/qq_43477726/article/details/99314982) |
| **企业微信** | `%USERPROFILE%\Documents\WXWork` | **无 env**；设置-存储管理改 | Community | [企业微信帮助](https://open.work.weixin.qq.com/help2/pc/13352) |
| **钉钉** | 缓存 `%LOCALAPPDATA%\DingTalk_87\Cache` | **无 env**；设置内"文件保存位置"改 | Community | [知乎: 钉钉改缓存](https://www.zhihu.com/question/379375357)、[CSDN: 钉钉改存储](https://blog.csdn.net/qq_45797116/article/details/145567808) |
| **飞书** | `%APPDATA%\LarkShell`（缓存） | **无 env**；手动/命令行/清理缓存 | Community | [飞书帮助: 重置数据](https://www.feishu.cn/hc/zh-CN/articles/885232563698)、[CSDN: 改飞书缓存路径](https://blog.csdn.net/honortech/article/details/155776241) |

---

## 六、游戏平台（均不支持 env，配置文件/设置内或 junction）

| 软件 | 默认数据目录 (Windows) | 环境变量 | 可信度 | 依据 |
|---|---|---|---|---|
| **Steam** 安装/游戏库 | `C:\Program Files (x86)\Steam\`、`...\steamapps\common\` | **无 env**；库路径写 `config\libraryfolders.vdf` | Official | [Steam Help: 迁移安装与游戏](https://help.steampowered.com/en/faqs/view/4BD4-4528-6B2E-8327)、[Steam 讨论: libraryfolders.vdf](https://steamcommunity.com/discussions/forum/1/3165461141521444201/) |
| **Epic Games** 游戏库 | `C:\Program Files\Epic Games` | **无 env**；启动器设置内改默认安装路径 | Official | [Epic: 改启动器安装路径](https://www.epicgames.com/help/epic-games-store-c-32735058/launcher-support-c-36403860/hl-ymknny-tghyyr-msar-tthbyt-mshghl-epic-games-a13696485) |
| **Origin / EA app** | `C:\Program Files\EA Games`（示例） | **无 env**；设置内改库目录 | Community | [Reddit: EA App 改安装位置](https://www.reddit.com/r/origin/comments/11n7p3w/) |
| **战网 Battle.net** | 游戏库经设置配置 | **无 env**；设置-下载-游戏安装位置改 | Community | [Blizzard 论坛: 改安装文件夹](https://us.forums.blizzard.com/en/d4/t/how-do-i-change-installation-folder/175886) |

---

## 关键结论

1. **唯一真正支持数据目录 env 重定位**：vcpkg（`VCPKG_DEFAULT_BINARY_CACHE` / `VCPKG_DOWNLOADS`）、Podman（`XDG_DATA_HOME` / `TMPDIR`）、Windows 用户 `TEMP`/`TMP`。
2. **Chocolatey** 三处都是官方 env（`ChocolateyInstall`、`ChocolateyToolsLocation`）；仅下载缓存例外，用 `config` 非 env。
3. **Docker** 客户端配置有 `DOCKER_CONFIG`，但**镜像/数据目录无 env**（`DOCKER_GRAPH` 已软废弃，改走 `data-root`）。Windows 上 Docker Desktop 数据 = WSL vhdx，靠安装参数/junction。
4. **浏览器、IM、游戏平台、Windows 邮件、OneDrive**：**全不支持 env**。归属"FC junction 迁移"能力——标注软件与默认路径，用户 junction 即可。
5. **Electron 应用**：无 env overlay，仅应用内 `app.setPath` API 或 `--user-data-dir`，逐应用判断，多数不支持。

> 注：微信 4.x 与 3.x 数据目录不同（`xwechat_files` vs `WeChat Files`），迁移/junction 需按版本区分。