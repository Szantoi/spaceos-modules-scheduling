using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// Loads a Doorstar input pack and enforces its SHA-256 pin.
/// </summary>
/// <remarks>
/// <para>
/// The packs are EXTERNAL contract inputs owned by the Doorstar instance. Each copy here is
/// byte-identical and pinned: a republished pack fails this gate loudly instead of silently
/// changing what the core is measured against. Updating a pin is therefore a deliberate,
/// reviewable act.
/// </para>
/// <para>
/// Two packs are pinned side by side, not one mutable file. v1 is the original 13-entry
/// pack; v2 supersedes it with the settled partial-release vector. Keeping both means the
/// core stays provably compatible with what an older consumer may still hold, and the
/// version number keeps identifying exactly one content — the discipline a single mutated
/// file broke on 2026-07-28.
/// </para>
/// </remarks>
internal static class CompatibilityFixture
{
    /// <summary>The original pack: 13 entries, no warning vector.</summary>
    internal const string V1 = "v1";

    /// <summary>Supersedes v1 with the settled partial-release rule: 14 entries.</summary>
    internal const string V2 = "v2";

    private static readonly ConcurrentDictionary<string, JsonDocument> Loaded = new();

    /// <summary>Root element of the requested pack.</summary>
    internal static JsonElement Root(string version) =>
        Loaded.GetOrAdd(version, Load).RootElement;

    private static JsonDocument Load(string version)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"doorstar-planning-input-pack.{version}.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Compatibility fixture missing at '{path}'.", path);
        }

        var expected = ReadPinnedHash(version);
        var actual = ComputeSha256(path);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Doorstar input pack {version} hash mismatch.{Environment.NewLine}" +
                $"  expected (pinned): {expected}{Environment.NewLine}" +
                $"  actual   (file):   {actual}{Environment.NewLine}" +
                "The gate is pinned on purpose: re-verify the pack against the Doorstar " +
                "announcement, then update the .sha256 file in the same commit.");
        }

        return JsonDocument.Parse(File.ReadAllBytes(path));
    }

    private static string ReadPinnedHash(string version)
    {
        var pinPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"doorstar-planning-input-pack.{version}.sha256");
        if (!File.Exists(pinPath))
        {
            throw new FileNotFoundException($"Fixture pin file missing at '{pinPath}'.", pinPath);
        }

        // "<hash>  <filename>" -- the sha256sum convention.
        var tokens = File.ReadAllText(pinPath).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0
            ? tokens[0]
            : throw new InvalidOperationException($"Fixture pin file '{pinPath}' is empty.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Reads a decimal property, treating JSON null as absent.</summary>
    internal static decimal? OptionalDecimal(this JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null
            ? value.GetDecimal()
            : null;

    /// <summary>Reads a string property, treating JSON null as absent.</summary>
    internal static string? OptionalString(this JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null
            ? value.GetString()
            : null;

    /// <summary>Reads an int property, treating JSON null as absent.</summary>
    internal static int? OptionalInt(this JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null
            ? value.GetInt32()
            : null;
}
