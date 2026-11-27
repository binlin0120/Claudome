# Claudome · 给 Claude 一个独立的空间

> **Claudome = Claude + Dome（穹顶）** — 为 Windows 11 打造硬件级隔离的独立安全工作区，让 Claude 的使用环境与日常主机彻底分离、互不打扰。开箱即用，无需安装 Go、.NET SDK 或 Codex。

- **最新版本**：[**Claudome v0.5.0 专线版（Windows 11 x64）**](https://github.com/binlin0120/Claudome/releases/tag/hyperv-v0.5.0)
- **历史兼容版**：[**Claudome v0.4.10（Windows 家庭版 / 旧多线路任务）**](https://github.com/binlin0120/Claudome/releases/tag/v0.4.10)
- **开源许可**：[MIT](LICENSE) · 第三方与素材说明见 [NOTICE](NOTICE.md)

---

## 核心特性

- **硬件级独立工作区**：为任务创建独立的 Windows 11 隔离环境与虚拟磁盘，禁用宿主机磁盘、USB 与设备重定向，资料仅保存在本机并采用当前 Windows 用户 DPAPI 加密。
- **双层受控网络边界**：关闭来宾普通虚拟网卡，经独立受控总线与外层网关出站，结合来宾 Clash TUN 与默认拒绝出站防火墙，防止系统或软件直连旁路；异常或关闭助手后自动切断外层通路。
- **16:9 流畅桌面与纯文本剪贴板**：内置 16:9 连续缩放内嵌桌面、独立窗口模式与原生控制台显式回退，支持无弹窗双向纯文本剪贴板直贴。
- **一键四维安全验真与重启持久复核**：顺序核验「防护与运行配置 · 来宾出口 · DNS 与旁路 · 断线阻断与恢复」，支持重启后持久策略回读，并引导在 AdsPower 同窗口核对出口后再打开 Claude。
- **断点续装与存储清理**：安装或配置中断后可按阶段自动续接；内置存储与备份清理及事务级原地更新保护。

---

## 快速开始

### 1. 全新安装（Windows 11 专业版 / 企业版 / 教育版 x64）
1. 前往 [**v0.5.0 发布页**](https://github.com/binlin0120/Claudome/releases/tag/hyperv-v0.5.0) 下载最新版 ZIP 安装包并**完整解压到一个单独的本地目录**。
2. 双击目录中的 `Claudome.exe` 启动（开源自建未签名程序，如 Windows SmartScreen 提示“已保护你的电脑”，点击「更多信息 → 仍要运行」并建议对照发布页 `SHA256SUMS.txt` 核验）。
3. 按顶部「安装向导 → 安全验真 → 专属桌面」三步完成部署，日常使用直接一键进入桌面。

### 2. 旧版（如 `v0.4.10`）一键迁移到专线版
> 已发布的 `v0.4.10` 客户端本身不含迁移按钮，请通过**新版程序**发起一键迁移：
1. 从 [**v0.5.0 发布页**](https://github.com/binlin0120/Claudome/releases/tag/hyperv-v0.5.0) 下载最新版 ZIP 并完整解压到**单独目录**；
2. 运行解压后的**新版 `Claudome.exe`**，在概览页或顶部「设置」点击 **「从旧版升级到专线版」**（命令行也可运行新版 `Claudome.exe --migrate-legacy`）；
3. 选择你原来的旧版程序所在目录（已运行安装或未运行解压副本均可），通过宿主机预检并确认后自动完成**完整备份 + 原地升级**：
   - **符合条件的宿主（Win11 专业/企业/教育版）**：原有硬件隔离任务核验身份后无缝续用；原有 VirtualBox / VMware 任务的磁盘、快照与记录 100% 保留，可通过备份目录中的独立旧版程序（`legacy-multi-route-backup-*\Claudome.exe`）继续打开。
   - **Windows 家庭版或其他不合格宿主**：在任何文件替换前自动停止并说明原因，零改动保留旧版与原任务。

### 3. 后续在线更新
`v0.5.0` 专线版使用专属在线更新通道（与旧版公共清单严格隔离），后续在软件概览页点击 **「检查在线更新」** 或 **「从本地 ZIP 更新」** 即可通过 SHA-256 校验后原地升级，保留全部任务与虚拟磁盘。

---

## 适用范围与版本政策

| 系统 / 场景 | 对应版本与说明 |
| --- | --- |
| **Windows 11 x64 专业版 / 企业版 / 教育版** | **主力维护**：使用 [**v0.5.0 专线版**](https://github.com/binlin0120/Claudome/releases/tag/hyperv-v0.5.0)（需开启硬件虚拟化，建议预留 95 GB 磁盘空间）。 |
| **Windows 家庭版 / 历史多线路任务** | **维护冻结（持续可用）**：停止功能更新，保留 [**v0.4.10**](https://github.com/binlin0120/Claudome/releases/tag/v0.4.10) 与旧更新清单供原环境继续使用或回退；后续升级至 Win11 专业版后可随时通过新版向导迁移。 |
| **macOS** | **开发中**：跨平台核心 [Claudome.Core](installer/Claudome.Core/Claudome.Core.csproj)、`darwin` 网关及 [Claudome.Mac](installer/Claudome.Mac/Claudome.Mac.csproj) 骨架已在仓库推进，待补全来宾一键编排并完成真机验收后发布成品。 |

**使用声明**：本工具仅部署本地隔离使用环境，Claude 模型仍在服务商处运行；网络节点、Windows 许可与第三方软件由用户按需自备并自行核对 [官方支持地区](https://www.anthropic.com/supported-countries) 与服务条款。公开仓库仅包含源码与通用文档，不包含任何个人镜像、订阅、账号或私有日志。

---

## 项目沿革与致谢

- **项目沿革与 Claude-Cage 的关系**：本项目在交流前已整理过自身方案与脚本，不是 Claude-Cage 的分支或移植，亦非复制其源码形成。沿革材料与按日期核对的技术差异见 [项目沿革与 Claude-Cage 对比](docs/项目沿革与Claude-Cage对比.md)。
- **主要开发作者**：李诗恩、林琛
- **参考与致谢**：感谢 [技术爬爬虾 @tech-shrimp](https://www.youtube.com/@tech-shrimp)、[余温 @gkxspace](https://x.com/gkxspace/status/2101993381303820704)、[薛定谔的叨叨](https://v.douyin.com/jTZQ-LRf8hM/)，以及 [@app_sail](https://x.com/app_sail)、[@suyuan1711](https://x.com/suyuan1711)、[@Nemo001ai](https://x.com/Nemo001ai)、[逸尘 @gengdaJ](https://x.com/gengdaJ)、[@zureA255062](https://x.com/zureA255062)、[@leo_xiaolei](https://x.com/leo_xiaolei)、[@JANEX0609](https://x.com/JANEX0609) 的公开分享。

### 延伸文档与历史归档
- 桌面端构建与架构说明：[installer/README.md](installer/README.md)
- 工具参考与购买入口：[02_采购清单与工具入口.md](02_采购清单与工具入口.md)
- 历史 V11 Agent 辅助脚本与手动部署文档（归档保留）：[第11版使用方法](docs/第11版使用方法.md) · [Codex Skill](skills/claude-environment-deploy/SKILL.md) · [Mac 部署流程](skills/claude-environment-deploy/references/Mac部署流程.md)

---

## 交流与反馈

项目交流 QQ 群：**1129410798**（群名：交流群）。欢迎交流使用体验、提交 Issue 或反馈建议。

<p>
  <img src="assets/community-qq.jpg" alt="项目交流 QQ 群 1129410798 的入群二维码" width="280" />
</p>

> 请勿在群聊或 Issues 中公开密码、订阅链接、令牌或带真实公网 IP 的诊断截图。
