using System.Runtime.CompilerServices;

//00 仅向 ZMAsset 的 Editor 测试程序集开放 internal 构建校验器，运行时程序集和业务程序集仍无法访问。
[assembly: InternalsVisibleTo("ZMAsset.EditorTests")]
