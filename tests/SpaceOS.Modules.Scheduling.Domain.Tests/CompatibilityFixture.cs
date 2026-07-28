using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// Loads the Doorstar planning input pack and enforces its SHA-256 pin.
/// </summary>
/// <remarks>
/// The pack is an EXTERNAL contract input owned by the Doorstar instance
/// (<c>doorstar-instance/docs/projects/doorstar-production-planning/fixtures/</c>).
/// The copy here is byte-identical and pinned: if Doorstar publishes a new revision,
/// this gate fails loudly instead of silently following a changed source. Updating the
/// pin is therefore a deliberate, reviewable act.
/// </remarks>
internal static class CompatibilityFixture
{
    private const string FixtureFileName = "doorstar-planning-input-pack.v1.json";
    private const string PinFileName = "doorstar-planning-input-pack.v1.sha256";

    private static readonly Lazy<JsonDocument> LazyDocument = new(Load);

    internal static JsonElement Root => LazyDocument.Value.RootElement;

    internal static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureFileName);

    private static JsonDocument Load()
    {
        var path = FixturePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Compatibility fixture missing at '{path}'. It must be copied to the output directory.", path);
        }

        var expected = ReadPinnedHash();
        var actual = ComputeSha256(path);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Doorstar input pack hash mismatch.{Environment.NewLine}" +
                $"  expected (pinned): {expected}{Environment.NewLine}" +
                $"  actual   (file):   {actual}{Environment.NewLine}" +
                "The compatibility gate is pinned on purpose: re-verify the new pack against the " +
                "published contract, then update the .sha256 file in the same commit.");
        }

        return JsonDocument.Parse(File.ReadAllBytes(path));
    }

    private static string ReadPinnedHash()
    {
        var pinPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", PinFileName);
        if (!File.Exists(pinPath))
        {
            throw new FileNotFoundException($"Fixture pin file missing at '{pinPath}'.", pinPath);
        }

        // "<hash>  <filename>" -- the sha256sum convention.
        var firstToken = File.ReadAllText(pinPath).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return firstToken.Length > 0
            ? firstToken[0]
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
