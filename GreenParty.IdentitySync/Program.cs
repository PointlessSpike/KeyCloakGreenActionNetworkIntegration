using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using GreenParty.IdentitySync.Configuration;
using GreenParty.IdentitySync.Services;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    var runOnce = args.Any(arg => string.Equals(arg, "--once", StringComparison.OrdinalIgnoreCase));
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSingleton(new SyncRuntimeOptions(runOnce));
    builder.Services.AddOptions<ActionNetworkOptions>()
        .Bind(builder.Configuration.GetSection(ActionNetworkOptions.SectionName));
    builder.Services.AddOptions<KeycloakOptions>()
        .Bind(builder.Configuration.GetSection(KeycloakOptions.SectionName));
    builder.Services.AddOptions<SyncOptions>()
        .Bind(builder.Configuration.GetSection(SyncOptions.SectionName));

    builder.Services.AddSingleton<ISyncStateStore, SqliteSyncStateStore>();
    builder.Services.AddSingleton<IdentitySyncOrchestrator>();

    builder.Services.AddHttpClient<ActionNetworkClient>((serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<ActionNetworkOptions>>().Value;
        client.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
        if (!string.IsNullOrWhiteSpace(options.ApiToken))
        {
            client.DefaultRequestHeaders.Add("OSDI-API-Token", options.ApiToken);
        }
    });

    builder.Services.AddHttpClient<KeycloakAdminClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<KeycloakOptions>>().Value;
            client.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
        })
        .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<KeycloakOptions>>().Value;
            var handler = new HttpClientHandler();
            if (!options.VerifySsl)
            {
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            return handler;
        });

    if (!runOnce)
    {
        builder.Services.AddHostedService<IdentitySyncHostedService>();
    }

    using var host = builder.Build();
    if (!runOnce)
    {
        await host.RunAsync();
        return 0;
    }

    using var scope = host.Services.CreateScope();
    var orchestrator = scope.ServiceProvider.GetRequiredService<IdentitySyncOrchestrator>();
    var completedSuccessfully = await orchestrator.RunAsync(CancellationToken.None);
    return completedSuccessfully ? 0 : 1;
}

static string EnsureTrailingSlash(string baseUrl)
{
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        throw new InvalidOperationException("A required base URL is missing.");
    }

    return baseUrl.EndsWith('/') ? baseUrl : $"{baseUrl}/";
}
