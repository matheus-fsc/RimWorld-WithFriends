// netstandard2.0 não declara IsExternalInit, exigido por propriedades `init`.
// Shim padrão, sem efeito em runtime.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
#endif
