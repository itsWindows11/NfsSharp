#if NETSTANDARD2_0
// Polyfill so C# 9 init-only property setters compile on netstandard2.0.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
