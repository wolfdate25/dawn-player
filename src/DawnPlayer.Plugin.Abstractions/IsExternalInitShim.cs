// netstandard2.0 does not ship IsExternalInit, which records with init-only
// properties compile against. Plugins target netstandard2.0 so they stay
// decoupled from the app's exact runtime, so the SDK carries the shim.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }
