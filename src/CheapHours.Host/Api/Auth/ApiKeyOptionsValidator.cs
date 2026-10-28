using CheapHours.Core.Ids;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Api.Auth;

/// <summary>
/// Validates the configured keys (section <c>ApiKeys</c>) with the same rules as stored keys, at startup,
/// so a bad configured key stops the API even while MongoDB is unreachable. Errors name the key, never its hash.
/// </summary>
internal sealed class ApiKeyOptionsValidator : IValidateOptions<ApiKeyOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, ApiKeyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in options.Keys)
        {
            if (!TryCreate(entry, out var record, out var error))
            {
                errors.Add(error);
            }
            else if (!names.TryAdd(record.KeyHash, record.Name))
            {
                errors.Add($"API key '{record.Name}' has the same hash as '{names[record.KeyHash]}'.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>Turns a configured entry into a key record, or explains why it breaks the key rules.</summary>
    /// <param name="entry">The configured key.</param>
    /// <param name="record">The record, when valid.</param>
    /// <param name="error">Why the entry is invalid, naming the key and never its hash.</param>
    /// <returns><see langword="true"/> when the entry is valid.</returns>
    public static bool TryCreate(ApiKeyEntry entry, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ApiKeyRecord? record, out string error)
    {
        ArgumentNullException.ThrowIfNull(entry);
        record = null;
        if (!OwnerId.TryParse(entry.Owner, out var owner))
        {
            error = $"API key '{entry.Name}': the owner '{entry.Owner}' is not a valid owner id.";
            return false;
        }

        try
        {
            record = new ApiKeyRecord(entry.Name, entry.Hash, owner, entry.Scopes.ToArray());
            error = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
