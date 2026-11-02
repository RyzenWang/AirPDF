using System.Text.Json.Serialization;

namespace AirPdf.Core.Services;

/// <summary>
/// Source-generated serializer for the settings file.
/// </summary>
/// <remarks>
/// The reflection-based <c>JsonSerializer</c> overloads are marked as requiring unreferenced
/// and dynamic code, so trimming can drop the metadata they need and NativeAOT cannot supply
/// it at all. A source-generated context keeps every member known at build time; verified by
/// publishing trimmed and confirming the settings file is still written.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
