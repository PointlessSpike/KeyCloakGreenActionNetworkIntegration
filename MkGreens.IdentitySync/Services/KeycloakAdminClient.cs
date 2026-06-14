using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MkGreens.IdentitySync.Configuration;
using MkGreens.IdentitySync.Models;

namespace MkGreens.IdentitySync.Services;

public sealed class KeycloakAdminClient
{
    private const string ActionNetworkPersonIdAttribute = "action_network_person_id";
    private readonly HttpClient _httpClient;
    private readonly KeycloakOptions _options;
    private readonly ILogger<KeycloakAdminClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly SemaphoreSlim _groupCacheLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;
    private Dictionary<string, KeycloakGroupSummary>? _groupCache;

    public KeycloakAdminClient(HttpClient httpClient, IOptions<KeycloakOptions> options, ILogger<KeycloakAdminClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<KeycloakUserSummary?> GetUserByIdAsync(string userId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildAdminPath($"users/{Uri.EscapeDataString(userId)}"));
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var representation = await DeserializeAsync<KeycloakUserRepresentation>(response, cancellationToken);
        return representation is null ? null : ToSummary(representation);
    }

    public async Task<KeycloakUserSummary?> FindUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildAdminPath($"users?email={Uri.EscapeDataString(email)}&exact=true"));
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var users = await DeserializeAsync<List<KeycloakUserRepresentation>>(response, cancellationToken) ?? [];
        return users
            .Where(user => string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
            .Select(ToSummary)
            .FirstOrDefault();
    }

    public async Task<string> CreateUserAsync(ActionNetworkPerson person, CancellationToken cancellationToken)
    {
        var representation = BuildRepresentationForCreate(person);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildAdminPath("users"))
        {
            Content = SerializeContent(representation)
        };

        using var response = await SendAuthorizedAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var locationId = response.Headers.Location?.Segments.LastOrDefault()?.Trim('/');
        if (!string.IsNullOrWhiteSpace(locationId))
        {
            return locationId;
        }

        var createdUser = await FindUserByEmailAsync(person.Email!, cancellationToken);
        return createdUser?.Id
               ?? throw new InvalidOperationException($"Keycloak user creation succeeded but no user id could be resolved for {person.Email}.");
    }

    public async Task UpdateUserAsync(string userId, ActionNetworkPerson person, CancellationToken cancellationToken)
    {
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, BuildAdminPath($"users/{Uri.EscapeDataString(userId)}"));
        using var getResponse = await SendAuthorizedAsync(getRequest, cancellationToken);
        getResponse.EnsureSuccessStatusCode();

        var representation = await DeserializeAsync<KeycloakUserRepresentation>(getResponse, cancellationToken)
            ?? throw new InvalidOperationException($"Could not load Keycloak user {userId} for update.");

        representation.FirstName = person.GivenName;
        representation.LastName = person.FamilyName;
        representation.Email = person.Email;
        representation.Enabled = true;
        representation.EmailVerified = _options.MarkEmailVerified;
        representation.Attributes ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        representation.Attributes[ActionNetworkPersonIdAttribute] = [person.PersonId];
        representation.Attributes["action_network_last_modified"] = [person.ModifiedDate?.ToString("O") ?? string.Empty];
        representation.Attributes["action_network_source"] = [person.Source ?? string.Empty];

        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, BuildAdminPath($"users/{Uri.EscapeDataString(userId)}"))
        {
            Content = SerializeContent(representation)
        };

        using var updateResponse = await SendAuthorizedAsync(updateRequest, cancellationToken);
        updateResponse.EnsureSuccessStatusCode();
    }

    public async Task DisableUserAsync(string userId, CancellationToken cancellationToken)
    {
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, BuildAdminPath($"users/{Uri.EscapeDataString(userId)}"));
        using var getResponse = await SendAuthorizedAsync(getRequest, cancellationToken);
        if (getResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        getResponse.EnsureSuccessStatusCode();
        var representation = await DeserializeAsync<KeycloakUserRepresentation>(getResponse, cancellationToken)
            ?? throw new InvalidOperationException($"Could not load Keycloak user {userId} for disable.");

        if (!representation.Enabled)
        {
            return;
        }

        representation.Enabled = false;
        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, BuildAdminPath($"users/{Uri.EscapeDataString(userId)}"))
        {
            Content = SerializeContent(representation)
        };

        using var updateResponse = await SendAuthorizedAsync(updateRequest, cancellationToken);
        updateResponse.EnsureSuccessStatusCode();
    }

    public async Task<KeycloakGroupSummary> EnsureGroupPathAsync(string groupPath, CancellationToken cancellationToken)
    {
        var normalisedPath = NormaliseGroupPath(groupPath);
        await EnsureGroupCacheAsync(cancellationToken);

        await _groupCacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_groupCache!.TryGetValue(normalisedPath, out var cached))
            {
                return cached;
            }

            string? parentId = null;
            var currentPath = string.Empty;
            foreach (var segment in normalisedPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = $"{currentPath}/{segment}";
                if (_groupCache.TryGetValue(currentPath, out var existing))
                {
                    parentId = existing.Id;
                    continue;
                }

                var created = await CreateGroupAsync(parentId, segment, cancellationToken);
                _groupCache = null;
                await EnsureGroupCacheAsync(cancellationToken);
                _groupCache!.TryGetValue(currentPath, out existing);

                parentId = existing?.Id ?? created.Id;
            }

            if (_groupCache!.TryGetValue(normalisedPath, out var createdOrExisting))
            {
                return createdOrExisting;
            }

            throw new InvalidOperationException($"Could not ensure Keycloak group path {normalisedPath}.");
        }
        finally
        {
            _groupCacheLock.Release();
        }
    }

    public async Task<IReadOnlyList<KeycloakGroupSummary>> GetUserGroupsAsync(string userId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildAdminPath($"users/{Uri.EscapeDataString(userId)}/groups"));
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var groups = await DeserializeAsync<List<KeycloakGroupRepresentation>>(response, cancellationToken) ?? [];
        return groups
            .Where(group => !string.IsNullOrWhiteSpace(group.Id) && !string.IsNullOrWhiteSpace(group.Path))
            .Select(group => new KeycloakGroupSummary(group.Id!, group.Name ?? string.Empty, group.Path!))
            .ToList();
    }

    public async Task AddUserToGroupAsync(string userId, string groupId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, BuildAdminPath($"users/{Uri.EscapeDataString(userId)}/groups/{Uri.EscapeDataString(groupId)}"));
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveUserFromGroupAsync(string userId, string groupId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, BuildAdminPath($"users/{Uri.EscapeDataString(userId)}/groups/{Uri.EscapeDataString(groupId)}"));
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<KeycloakGroupSummary> CreateGroupAsync(string? parentId, string groupName, CancellationToken cancellationToken)
    {
        var relativePath = parentId is null
            ? BuildAdminPath("groups")
            : BuildAdminPath($"groups/{Uri.EscapeDataString(parentId)}/children");

        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = SerializeContent(new KeycloakGroupRepresentation
            {
                Name = groupName
            })
        };

        using var response = await SendAuthorizedAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var locationId = response.Headers.Location?.Segments.LastOrDefault()?.Trim('/');
        _logger.LogInformation("Created Keycloak group {GroupName} under parent {ParentId}.", groupName, parentId ?? "<root>");
        return new KeycloakGroupSummary(locationId ?? string.Empty, groupName, string.Empty);
    }

    private async Task EnsureGroupCacheAsync(CancellationToken cancellationToken)
    {
        if (_groupCache is not null)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildAdminPath("groups?briefRepresentation=false&max=500"));
        using var response = await SendAuthorizedAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var groups = await DeserializeAsync<List<KeycloakGroupRepresentation>>(response, cancellationToken) ?? [];
        var cache = new Dictionary<string, KeycloakGroupSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            AddGroupToCache(group, cache);
        }

        _groupCache = cache;
    }

    private static void AddGroupToCache(KeycloakGroupRepresentation group, IDictionary<string, KeycloakGroupSummary> cache)
    {
        if (!string.IsNullOrWhiteSpace(group.Id) && !string.IsNullOrWhiteSpace(group.Path))
        {
            cache[group.Path!] = new KeycloakGroupSummary(group.Id!, group.Name ?? string.Empty, group.Path!);
        }

        if (group.SubGroups is null)
        {
            return;
        }

        foreach (var subGroup in group.SubGroups)
        {
            AddGroupToCache(subGroup, cache);
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _accessToken;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"realms/{Uri.EscapeDataString(_options.AuthenticationRealm)}/protocol/openid-connect/token")
            {
                Content = BuildTokenRequestContent()
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var token = await DeserializeAsync<KeycloakTokenResponse>(response, cancellationToken)
                ?? throw new InvalidOperationException("Keycloak token response was empty.");

            _accessToken = token.AccessToken;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private FormUrlEncodedContent BuildTokenRequestContent()
    {
        if (!string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            return new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.AdminClientId,
                ["client_secret"] = _options.ClientSecret
            });
        }

        if (string.IsNullOrWhiteSpace(_options.AdminUsername) || string.IsNullOrWhiteSpace(_options.AdminPassword))
        {
            throw new InvalidOperationException("Configure either Keycloak:ClientSecret or Keycloak:AdminUsername and Keycloak:AdminPassword.");
        }

        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _options.AdminClientId,
            ["username"] = _options.AdminUsername,
            ["password"] = _options.AdminPassword
        });
    }

    private string BuildAdminPath(string relativePath)
    {
        return $"admin/realms/{Uri.EscapeDataString(_options.Realm)}/{relativePath}";
    }

    private HttpContent SerializeContent<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, _jsonOptions);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken);
    }

    private KeycloakUserSummary ToSummary(KeycloakUserRepresentation representation)
    {
        var attributes = representation.Attributes?
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        return new KeycloakUserSummary(
            representation.Id ?? string.Empty,
            representation.Username ?? string.Empty,
            representation.Email,
            representation.Enabled,
            attributes);
    }

    private KeycloakUserRepresentation BuildRepresentationForCreate(ActionNetworkPerson person)
    {
        return new KeycloakUserRepresentation
        {
            Username = $"an-{person.PersonId}",
            Email = person.Email,
            FirstName = person.GivenName,
            LastName = person.FamilyName,
            Enabled = true,
            EmailVerified = _options.MarkEmailVerified,
            Attributes = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                [ActionNetworkPersonIdAttribute] = [person.PersonId],
                ["action_network_last_modified"] = [person.ModifiedDate?.ToString("O") ?? string.Empty],
                ["action_network_source"] = [person.Source ?? string.Empty]
            }
        };
    }

    private static string NormaliseGroupPath(string groupPath)
    {
        if (string.IsNullOrWhiteSpace(groupPath))
        {
            throw new ArgumentException("Keycloak group path must not be empty.", nameof(groupPath));
        }

        return "/" + string.Join('/', groupPath.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class KeycloakTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class KeycloakUserRepresentation
    {
        public string? Id { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }

        [JsonPropertyName("firstName")]
        public string? FirstName { get; set; }

        [JsonPropertyName("lastName")]
        public string? LastName { get; set; }

        public bool Enabled { get; set; }

        [JsonPropertyName("emailVerified")]
        public bool EmailVerified { get; set; }

        public Dictionary<string, List<string>>? Attributes { get; set; }
    }

    private sealed class KeycloakGroupRepresentation
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Path { get; set; }

        [JsonPropertyName("subGroups")]
        public List<KeycloakGroupRepresentation>? SubGroups { get; set; }
    }
}
