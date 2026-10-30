# 源码发布准备

本项目采用 [SKPDB 源码公开限制许可](../LICENSE)，允许本地学习、审计、编译与自用修改，
禁止未经授权的 Fork 与二次分发，并明确禁止以 SKPDB 或 SKPDB 改进版名义二次分发。
这些限制不符合 [OSI 开源定义](https://opensource.org/osd) 的自由再分发和派生作品要求；
发布页应使用“源码公开”或“source-available”，不应标注 MIT、Apache 或 OSI 开源许可。

[GitHub 服务条款 D.5](https://docs.github.com/en/site-policy/github-terms/github-terms-of-service#5-license-grant-to-other-users)
对公开仓库授予站内查看与 Fork 权利。选择公开 GitHub 仓库就不能保证禁止站内 Fork。
如果“不得 Fork”是发布的硬约束，应采用与这一要求相容的托管/分发方式。

发布步骤：

1. 执行 `python tools/check_repository.py`，运行相关回归并构建。
2. 执行 `python tools/export_source.py`，检查 `dist/` 下生成的源码 ZIP 和 SHA-256 文件。
3. 解压至空目录，按 BUILDING 文档重新构建，确认没有依赖开发机私有文件。
4. 源码包不包含 `.git`。不要直接发布开发工作区或旧 Git 历史；发布新历史前单独审查历史内容。
5. 确认拥有原创代码和所附依赖的发布权。SKRoot SDK 未附带，外部 SDK 的授权须由获取者自行满足。
6. 二进制发布时附带 LICENSE、THIRD_PARTY_NOTICES 及相应依赖必须保留的许可声明。

导出工具使用明确的路径/扩展名白名单，同时排除 SDK、无关项目、缓存、构建结果、截图、
用户数据与内部验证记录。扫描只能发现已定义的模式，不能替代人工审查版权来源和所有敏感信息。
用户反馈包仅用于本地复核，不随源码发布。导出使用当前工作区文件，包含尚未提交的修改；
不会自动提交、推送、重写 Git 历史或改变远程仓库的可见性。
