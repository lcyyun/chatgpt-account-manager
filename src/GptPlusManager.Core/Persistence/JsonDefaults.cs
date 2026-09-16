using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GptPlusManager.Core.Persistence;

// .NET 10 disables reflection-based JSON by default in several hosting modes, so every
// serializer call in this assembly must opt in through an explicit type-info resolver.
internal static class JsonDefaults
{
    internal static readonly JsonSerializerOptions Compact = Create(false);
    internal static readonly JsonSerializerOptions Pretty = Create(true);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = indented,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}
