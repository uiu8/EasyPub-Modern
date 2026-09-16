# 发布一个新版本

面向维护者。发一个版本就三步。

## 1. 改版本号（四处，必须一致）

- `src/EasyPub.Desktop/EasyPub.Desktop.csproj`：`Version`、`AssemblyVersion`、`FileVersion`
- `installer/EasyPubModern.iss`：`AppVersion`，以及 `PublishDir`（目录名要含本次版本号与代号）

漏改 `AssemblyVersion` 时窗口标题会显示旧版本；漏改 `PublishDir` 时 ISCC 报 "No files found"。
`build-release.ps1` 会先校验这几处，不一致直接退出，不会打出一个版本号自相矛盾的包。

## 2. 写发布说明

新建 `docs/RELEASE_v<版本>.md`。**它同时是 GitHub Release 的正文**，没有这份文件就不发布
（发布脚本会拦下），这样不会出现没有说明的版本。

写清楚三件事：改了什么、**怎么验证的**、**还有什么没验证**。第三项和前两项一样重要——
例如 v1.57.0 就明确写了安装版自更新路径没有单独实测。

## 3. 构建并发布

```powershell
pwsh tools/build-release.ps1   -Version 1.57.3 -Codename some-change
pwsh tools/publish-github.ps1  -Version 1.57.3 -Codename some-change
```

- `build-release.ps1`：校验版本号 → 停掉锁住构建输出的进程 → 清 `EasyPub.Core` 的陈旧输出
  （改过 Core 必须清，MSBuild 会静默跳过 CoreCompile）→ 发布 → 补 kindlegen → 打便携包
  → 出安装包。便携包有**体积闸门**：跌破 60MB 说明漏了 kindlegen，直接报错。
- `publish-github.ps1`：推送 → 创建 Release → 上传资产 → **回校验远端资产大小与本地一致**
  （上传中断会留下截断的资产）。

## 3.5 同步到 AtomGit 镜像（可选，给国内用户）

```powershell
pwsh tools/publish-atomgit.ps1 -Version 1.57.3 -Codename some-change
```

客户端的更新检查按 `GitHub → AtomGit` 的顺序尝试，**第一个应答正常的源胜出**。
所以同步到 AtomGit 之后，国内直连 GitHub 失败的用户仍能检查到新版本并下载。

前提：仓库里放一份 AtomGit 访问令牌文件 `.atomgit-token`（已在 `.gitignore` 里，
**绝不提交**）。没有这个文件脚本会直接报错退出。

### 三个实测出来的坑

1. **AtomGit 上的 release 删不掉。** `DELETE /releases/{tag}` 返回 405，响应里也没有文档声称的
   `id` 字段。也就是说发出去就收不回来——**发之前务必确认版本号**，别指望像 GitHub 那样
   发错了删掉重发。脚本因此会在创建前先查一次，重复版本直接拒绝。
2. **附件上传是两步**：先 `GET .../releases/{tag}/upload_url?file_name=...` 拿到预签名地址和
   一组必需请求头，再用 `PUT` 传文件（实际落在 `file.gitcode.com`）。
3. **assets 里混着四个平台自动生成的源码包**（`type=source`），必须靠 `type` 过滤，
   否则回校验会数错附件个数。

### 两边不一致会怎样

客户端不做"跨源比版本取最高"，主源能通时它就是权威。所以**忘了同步 AtomGit** 的后果是：
国内用户看到的是旧版本，而 GitHub 用户正常。发完版顺手跑一下同步脚本就行。

## 网络

本机直连 `github.com:443` 不通，但仓库级代理已配置（`git config http.proxy`，只对这个仓库生效）；
`gh` 走 `api.github.com` 与 `uploads.github.com` 可直连，因此不需要额外设置。
换机器若 `git push` 失败，先确认 git 本身能否访问 github.com。

## 发版之后：跑一次真实更新验证

**这一步不能省。** 单元测试全绿也可能流程是断的——v1.57.0 就是这么发现两个真实缺陷的
（更新包被自己的清理删掉、发布包带顶层目录导致替换错位）。

```powershell
# 1) 把上一版的便携版复制到临时目录当作"用户已装的旧版本"
# 2) 设置 → 更新与关于 → 检查更新，或用 work/update-probe 直接驱动
# 3) 关闭程序，观察它是否自动覆盖并重启到新版本
# 4) 检查：exe 版本号变了、用户自己放的文件还在、出现了 .backup-<被替换的版本> 目录
```

第 4 步的备份目录名值得多看一眼：它必须跟着**被替换掉的版本**走。
v1.57.2 修的就是这里——此前它跟着目标版本走，于是从 1.57.0 更新到 1.57.1 之后，
目录叫 `.backup-1.57.1` 里面装的却是 1.57.0。

## 相关文件

| 文件 | 作用 |
|---|---|
| `tools/build-release.ps1` | 构建与打包（带版本号、体积两道闸门） |
| `tools/publish-github.ps1` | 推送与发布（带远端资产校验） |
| `work/update-probe/` | 更新链路探针：检查、下载、解压、落地、闭环验证（Git 忽略） |
| `docs/RELEASE_v*.md` | 各版本发布说明 |
