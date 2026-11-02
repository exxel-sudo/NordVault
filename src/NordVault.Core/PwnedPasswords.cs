using System.Net.Http.Headers;

namespace NordVault.Core;

/// <summary>
/// Have I Been Pwned "Pwned Passwords" range API with k-anonymity: only the first 5 hex characters of a password's SHA-1
/// are sent; the matching suffixes come back and are compared locally. Responses are padded so their size reveals nothing.
/// Only ever used after the user clicks the leak check.
/// </summary>
public static class PwnedPasswords
{
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.pwnedpasswords.com/"), Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NordVault", "0.4"));
        client.DefaultRequestHeaders.Add("Add-Padding", "true");
        return client;
    }

    public static async Task<string> FetchRangeAsync(string prefix, CancellationToken cancellation)
    {
        if (prefix.Length != 5 || !prefix.All(Uri.IsHexDigit)) throw new ArgumentException("Ogiltigt prefix.");
        using var response = await Client.GetAsync("range/" + prefix, cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellation);
    }

    /// <summary>True when the 35-character suffix is listed with a count above zero (padding lines have count 0).</summary>
    public static bool Contains(string rangeResponse, string suffix)
    {
        foreach (var line in rangeResponse.Split('\n'))
        {
            var parts = line.Trim().Split(':');
            if (parts.Length == 2 && string.Equals(parts[0], suffix, StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[1], out var count) && count > 0) return true;
        }
        return false;
    }
}
