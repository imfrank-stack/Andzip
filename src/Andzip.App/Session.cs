using Andzip.Core.Archive;

namespace Andzip.App;

/// <summary>
/// 一次运行之内的进程级状态。
/// <para>
/// 为什么单独立一个类型而不是挂在 <c>App</c> 上：在 <c>namespace Andzip.App</c> 内部写
/// <c>App.Passwords</c> 时，编译器会先把 <c>App</c> 解析成命名空间本身，于是要么报错要么
/// 得写成 <c>Andzip.App.App.Passwords</c>。一个名字响亮、不会撞车的容器比跟解析规则较劲省事。
/// </para>
/// </summary>
internal static class Session
{
    /// <summary>
    /// 本次运行的口令记忆：打开一个包时输过一次，解压它就不用再问。
    /// <para>只在内存里，进程退出就没了 —— 不写进设置文件，也不写进注册表。</para>
    /// </summary>
    public static PasswordCache Passwords { get; } = new();
}
