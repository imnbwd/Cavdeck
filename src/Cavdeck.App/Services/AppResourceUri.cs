namespace Cavdeck.App.Services;

// Pack URIs must track the compiled assembly name, not the namespace or project name.
internal static class AppResourceUri
{
    private static readonly string AssemblyName = typeof(AppResourceUri).Assembly.GetName().Name!;

    public static Uri Relative(string path) =>
        new($"/{AssemblyName};component/{path}", UriKind.Relative);

    public static Uri Absolute(string path) =>
        new($"pack://application:,,,/{AssemblyName};component/{path}", UriKind.Absolute);
}
