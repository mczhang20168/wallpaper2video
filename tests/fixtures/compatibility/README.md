# 兼容性测试数据

`expected.json` 记录旧版 Python 实现的帧舍入、循环次数、参数校验、路径和素材包读取预期。`packages/` 下的素材包为测试专门生成。

`casefold.json` 保留 Python 3.12.14 使用的 Unicode 15.0.0 完整默认大小写折叠映射，用于兼容归档中的 Unicode 重名检查。 [Unicode 数据及许可](../../../THIRD-PARTY-NOTICES.txt)

旋转周期与依赖签名回归在运行时生成测试素材。 [SpinChecks.cs](../../../csharp/tests/Wallpaper2Video.CompatibilityTests/SpinChecks.cs)
