using System.Text.Json;
using Microsoft.Extensions.Options;
using MkGreens.IdentitySync.Configuration;
using MkGreens.IdentitySync.Models;

namespace MkGreens.IdentitySync.Services;

public sealed class ActionNetworkClient
{
    private readonly HttpClient _httpClient;
    private readonly ActionNetworkOptions _options;
    private readonly ILogger<ActionNetworkClient> _logger;

    public ActionNetworkClient(HttpClient httpClient, IOptions<ActionNetworkOptions> options, ILogger<ActionNetworkClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ActionNetworkPerson>> GetPeopleAsync(CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var people = new List<ActionNetworkPerson>();
        var nextUrl = "people";

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var page = ParsePeoplePage(payload);
            people.AddRange(page.People);
            nextUrl = page.NextPageHref;
        }

        _logger.LogInformation("Retrieved {Count} Action Network people records.", people.Count);
        return people;
    }

    public async Task<IReadOnlySet<string>> GetPeopleIdsForTagAsync(string tagId, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        if (string.IsNullOrWhiteSpace(tagId))
        {
            throw new ArgumentException("Tag id must be provided.", nameof(tagId));
        }

        var personIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextUrl = $"tags/{Uri.EscapeDataString(tagId)}/taggings?per_page={Math.Clamp(_options.TaggingsPageSize, 1, 500)}";

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var page = ParseTaggingsPage(payload);
            foreach (var personId in page.PersonIds)
            {
                personIds.Add(personId);
            }

            nextUrl = page.NextPageHref;
        }

        _logger.LogInformation("Retrieved {Count} Action Network tag members for tag {TagId}.", personIds.Count, tagId);
        return personIds;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            throw new InvalidOperationException("ActionNetwork:ApiToken is not configured.");
        }
    }

    private static (IReadOnlyList<ActionNetworkPerson> People, string? NextPageHref) ParsePeoplePage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var people = new List<ActionNetworkPerson>();

        if (root.TryGetProperty("_embedded", out var embedded)
            && embedded.TryGetProperty("osdi:people", out var peopleArray))
        {
            foreach (var personElement in peopleArray.EnumerateArray())
            {
                var personId = ExtractActionNetworkId(personElement);
                if (string.IsNullOrWhiteSpace(personId))
                {
                    continue;
                }

                people.Add(new ActionNetworkPerson(
                    personId,
                    TryGetString(personElement, "given_name"),
                    TryGetString(personElement, "family_name"),
                    ExtractEmail(personElement),
                    TryGetDateTimeOffset(personElement, "modified_date"),
                    TryGetString(personElement, "source")));
            }
        }

        return (people, ExtractNextHref(root));
    }

    private static (IReadOnlyList<string> PersonIds, string? NextPageHref) ParseTaggingsPage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var personIds = new List<string>();

        if (root.TryGetProperty("_embedded", out var embedded)
            && embedded.TryGetProperty("osdi:taggings", out var taggingsArray))
        {
            foreach (var taggingElement in taggingsArray.EnumerateArray())
            {
                if (!taggingElement.TryGetProperty("_links", out var links)
                    || !links.TryGetProperty("osdi:person", out var personLink)
                    || !personLink.TryGetProperty("href", out var hrefProperty))
                {
                    continue;
                }

                var href = hrefProperty.GetString();
                if (string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                var personId = href.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (!string.IsNullOrWhiteSpace(personId))
                {
                    personIds.Add(personId);
                }
            }
        }

        return (personIds, ExtractNextHref(root));
    }

    private static string? ExtractActionNetworkId(JsonElement personElement)
    {
        if (personElement.TryGetProperty("identifiers", out var identifiers))
        {
            foreach (var identifier in identifiers.EnumerateArray())
            {
                var value = identifier.GetString();
                if (value?.StartsWith("action_network:", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return value["action_network:".Length..];
                }
            }
        }

        if (personElement.TryGetProperty("_links", out var links)
            && links.TryGetProperty("self", out var selfLink)
            && selfLink.TryGetProperty("href", out var hrefProperty))
        {
            var href = hrefProperty.GetString();
            return href?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        }

        return null;
    }

    private static string? ExtractEmail(JsonElement personElement)
    {
        if (!personElement.TryGetProperty("email_addresses", out var emailAddresses))
        {
            return null;
        }

        string? fallback = null;
        foreach (var emailEntry in emailAddresses.EnumerateArray())
        {
            var address = TryGetString(emailEntry, "address");
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            fallback ??= address;
            if (emailEntry.TryGetProperty("primary", out var primaryProperty)
                && primaryProperty.ValueKind == JsonValueKind.True)
            {
                return address;
            }
        }

        return fallback;
    }

    private static string? ExtractNextHref(JsonElement root)
    {
        if (root.TryGetProperty("_links", out var links)
            && links.TryGetProperty("next", out var next)
            && next.TryGetProperty("href", out var hrefProperty))
        {
            return hrefProperty.GetString();
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), out var result) ? result : null;
    }
}
