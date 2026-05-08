using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.Identifier;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class LogicAppEnvComponent : DockerAzureEnvComponent
{
    private const string LogicAppRoot = "/home/site/wwwroot";
    private const string ManagedRuntimeImage = "testframework-logicapp-runtime:coretools4";
    private static readonly object ManagedRuntimeSync = new();

    public override EnvComponentIdentifier Id => DockerAzureEnvironment.LogicAppComponentId;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies =>
    [
        DockerAzureEnvironment.NetworkComponentId,
        DockerAzureEnvironment.AzuriteComponentId,
    ];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);
        if (dockerEnvironment.UsedLogicAppIdentifiers.Count == 0)
            return Array.Empty<LogicAppRuntimeState>();

        ConfigStore<LogicAppConfig>? logicAppStore = EnvComponentConfigStoreGuard.GetRequiredStore<LogicAppConfig>(dockerEnvironment, serviceProvider, dockerEnvironment.UsedLogicAppIdentifiers, "Logic App environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        DockerEndpointMap endpointMap = dockerEnvironment.GetEndpointMap();
        List<LogicAppRuntimeState> runtimes = [];

        foreach (string identifier in dockerEnvironment.UsedLogicAppIdentifiers)
        {
            LogicAppDefinitionDescriptor descriptor = dockerEnvironment.GetRequiredLogicAppDescriptor(new LogicAppIdentifier(identifier));
            string logicAppPath = LogicAppPathLocator.Resolve(descriptor.Path);
            MaterializedLogicAppHost materializedHost = MaterializeLogicAppHost(logicAppPath, cancellationToken);
            Dictionary<string, string> appSettings = BuildAppSettings(dockerEnvironment, descriptor);
            LogicAppConfig current = logicAppStore!.GetConfig(identifier);
            string workflowName = string.IsNullOrWhiteSpace(current.WorkflowName) ? identifier : current.WorkflowName;
            string containerImage = ResolveContainerImage(descriptor.Image, logger, cancellationToken);

            ContainerBuilder builder = new ContainerBuilder(containerImage)
                .WithNetwork(network)
                .WithPortBinding(80, true)
                .WithBindMount(materializedHost.HostRootPath, LogicAppRoot, AccessMode.ReadOnly);

            foreach ((string key, string value) in appSettings)
                builder = builder.WithEnvironment(key, value);

            IContainer container = builder.Build();
            await container.StartAsync(cancellationToken).ConfigureAwait(false);

            string baseUrl = endpointMap.GetFunctionAppBaseUrl(container);
            try
            {
                await WaitForWorkflowReadyAsync(baseUrl, materializedHost, workflowName, current, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                string containerLogs = await TryGetContainerLogsAsync(container, cancellationToken).ConfigureAwait(false);
                throw CreateStartupFailure(identifier, workflowName, baseUrl, containerImage, containerLogs, exception);
            }

            logicAppStore.AddConfig(identifier, current with
            {
                Standard = current.Standard with
                {
                    BaseUrl = baseUrl,
                },
            });
            runtimes.Add(new LogicAppRuntimeState(container, materializedHost.HostRootPath));
        }

        return runtimes;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (state is IEnumerable<LogicAppRuntimeState> runtimes)
        {
            foreach (LogicAppRuntimeState runtime in runtimes)
            {
                await LogContainerOutputAsync(runtime.Container, logger, cancellationToken).ConfigureAwait(false);
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static Dictionary<string, string> BuildAppSettings(DockerAzureEnvironment dockerEnvironment, LogicAppDefinitionDescriptor descriptor)
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["APP_KIND"] = "workflowApp",
            ["AzureWebJobsSecretStorageType"] = "Files",
            ["FUNCTIONS_EXTENSION_VERSION"] = "~4",
            ["FUNCTIONS_V2_COMPATIBILITY_MODE"] = "true",
            ["WEBSITE_NODE_DEFAULT_VERSION"] = "~18",
            ["AzureWebJobsStorage"] = dockerEnvironment.GetEndpointMap().RewriteStorageForContainer(DockerAzureDefaults.DefaultAzuriteConnectionString),
        };

        if (UsesManagedRuntime(descriptor.Image))
        {
            settings["AzureWebJobsFeatureFlags"] = "EnableWorkerIndexing";
            settings["AzureWebJobsScriptRoot"] = LogicAppRoot;
            settings["ASPNETCORE_URLS"] = "http://0.0.0.0:80";
            settings["PORT"] = "80";
            settings["ProjectDirectoryPath"] = LogicAppRoot;
            settings["WEBSITES_PORT"] = "80";
            settings["FUNCTIONS_WORKER_RUNTIME"] = "dotnet";
            settings["FUNCTIONS_INPROC_NET8_ENABLED"] = "1";
        }
        else
        {
            settings["AzureWebJobsFeatureFlags"] = "EnableWorkerIndexing";
            settings["AzureWebJobsScriptRoot"] = LogicAppRoot;
            settings["AzureWebJobsSecretStorageType"] = "Files";
            settings["ASPNETCORE_URLS"] = "http://0.0.0.0:80";
            settings["PORT"] = "80";
            settings["ProjectDirectoryPath"] = LogicAppRoot;
            settings["WEBSITES_PORT"] = "80";
            settings["FUNCTIONS_WORKER_RUNTIME"] = "dotnet";
            settings["FUNCTIONS_INPROC_NET8_ENABLED"] = "1";
        }

        foreach ((string key, string value) in descriptor.AdditionalSettings)
            settings[key] = value;

        return settings;
    }

    private static async Task LogContainerOutputAsync(IContainer container, ScopedLogger logger, CancellationToken cancellationToken)
    {
        try
        {
            (string stdout, string stderr) = await container.GetLogsAsync(
                since: DateTime.UnixEpoch,
                until: DateTime.UtcNow,
                timestampsEnabled: false,
                ct: cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stdout))
                logger.LogInformation($"Logic App container stdout ({container.Id}):{Environment.NewLine}{stdout}");

            if (!string.IsNullOrWhiteSpace(stderr))
                logger.LogWarning($"Logic App container stderr ({container.Id}):{Environment.NewLine}{stderr}");
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Failed to capture Logic App container logs ({container.Id}): {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static async Task<string> TryGetContainerLogsAsync(IContainer container, CancellationToken cancellationToken)
    {
        try
        {
            (string stdout, string stderr) = await container.GetLogsAsync(
                since: DateTime.UnixEpoch,
                until: DateTime.UtcNow,
                timestampsEnabled: false,
                ct: cancellationToken).ConfigureAwait(false);

            return string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(static value => !string.IsNullOrWhiteSpace(value))).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatContainerLogSuffix(string containerLogs)
    {
        if (string.IsNullOrWhiteSpace(containerLogs))
            return string.Empty;

        const int maxLength = 4000;
        string snippet = containerLogs.Length <= maxLength
            ? containerLogs
            : containerLogs[^maxLength..];

        return $"{Environment.NewLine}{Environment.NewLine}Container logs:{Environment.NewLine}{snippet}";
    }

    private static Exception CreateStartupFailure(string identifier, string workflowName, string baseUrl, string image, string containerLogs, Exception innerException)
    {
        if (LooksLikeMissingNet8InProcHost(containerLogs))
        {
            return new InvalidOperationException(
                $"Docker-hosted Logic App '{identifier}' could not start in image '{image}'. "
                + "The Linux Azure Functions Core Tools runtime does not contain the .NET 8 in-process host required by the current package-based Logic Apps Standard project shape. "
                + "This Docker path is not supported with the tested public image. Use Azure-hosted Logic Apps, a custom image that includes the required Logic Apps runtime, or a future container path that stages a compatible bundle-based workflow host."
                + FormatContainerLogSuffix(containerLogs),
                innerException);
        }

        if (LooksLikeWorkflowBundleRuntimeMismatch(containerLogs))
        {
            return new InvalidOperationException(
                $"Docker-hosted Logic App '{identifier}' could not start in image '{image}'. "
                + "The workflow extension bundle loaded, but the Linux Core Tools host failed with a runtime assembly mismatch while bootstrapping Logic Apps Standard. "
                + "This indicates the tested public Docker runtime is incompatible with the workflow bundle needed for local Logic Apps Standard execution."
                + FormatContainerLogSuffix(containerLogs),
                innerException);
        }

        return new InvalidOperationException(
            $"The Logic App workflow host for '{identifier}' did not activate workflow management endpoints at '{baseUrl}'. "
                + $"The container became reachable, but workflow '{workflowName}' never exposed its trigger management surface. "
                + "This usually means the mounted app payload or runtime bootstrap is incomplete for Logic Apps Standard in the selected image."
            + FormatContainerLogSuffix(containerLogs),
            innerException);
    }

    private static bool LooksLikeMissingNet8InProcHost(string containerLogs)
    {
        return containerLogs.Contains("in-proc8/func.exe", StringComparison.OrdinalIgnoreCase)
            || containerLogs.Contains("Failed to locate the in-process model host", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeWorkflowBundleRuntimeMismatch(string containerLogs)
    {
        return containerLogs.Contains("Loaded extension 'WorkflowExtension'", StringComparison.Ordinal)
            && containerLogs.Contains("Could not load type 'System.Runtime.InteropServices.OSPlatform'", StringComparison.Ordinal);
    }

    private static string ResolveContainerImage(string configuredImage, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (!UsesManagedRuntime(configuredImage))
            return configuredImage;

        lock (ManagedRuntimeSync)
        {
            if (DockerImageExists(ManagedRuntimeImage, cancellationToken))
                return ManagedRuntimeImage;

            BuildManagedRuntimeImage(logger, cancellationToken);
            return ManagedRuntimeImage;
        }
    }

    private static bool UsesManagedRuntime(string configuredImage)
    {
        return string.Equals(configuredImage, DockerAzureDefaults.LogicAppImage, StringComparison.OrdinalIgnoreCase);
    }

    private static MaterializedLogicAppHost MaterializeLogicAppHost(string logicAppPath, CancellationToken cancellationToken)
    {
        string hostRootPath = Path.Combine(Path.GetTempPath(), "TestFramework", "logicapp-hosts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hostRootPath);

        WriteRuntimeHostJson(Path.Combine(hostRootPath, "host.json"));
        WriteRuntimeLocalSettings(Path.Combine(hostRootPath, "local.settings.json"));
        WriteConnectionsJson(logicAppPath, Path.Combine(hostRootPath, "connections.json"));

        string[] workflowPaths = Directory.GetFiles(logicAppPath, "workflow.json", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (workflowPaths.Length == 0)
            throw new InvalidOperationException($"Could not locate any Logic App workflow.json files under '{logicAppPath}'.");

        Dictionary<string, LogicAppWorkflowDefinition> workflowDefinitions = new(StringComparer.OrdinalIgnoreCase);

        foreach (string workflowPath in workflowPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string workflowDirectory = Path.GetDirectoryName(workflowPath)!;
            string workflowName = new DirectoryInfo(workflowDirectory).Name;
            string targetDirectory = Path.Combine(hostRootPath, workflowName);
            Directory.CreateDirectory(targetDirectory);
            File.Copy(workflowPath, Path.Combine(targetDirectory, "workflow.json"), overwrite: true);
            workflowDefinitions[workflowName] = ReadWorkflowDefinition(workflowPath, workflowName);
        }

        return new MaterializedLogicAppHost(hostRootPath, workflowDefinitions);
    }

    private static void WriteRuntimeHostJson(string targetPath)
    {
        File.WriteAllText(targetPath, """
{
  "version": "2.0",
  "extensionBundle": {
    "id": "Microsoft.Azure.Functions.ExtensionBundle.Workflows",
    "version": "[1.*, 2.0.0)"
  }
}
""");
    }

    private static void WriteRuntimeLocalSettings(string targetPath)
    {
        string storageConnectionString = $"DefaultEndpointsProtocol=http;AccountName={DockerAzureDefaults.AzuriteAccountName};AccountKey={DockerAzureDefaults.AzuriteAccountKey};BlobEndpoint=http://{DockerAzureEnvironment.AzuriteNetworkAlias}:10000/{DockerAzureDefaults.AzuriteAccountName};QueueEndpoint=http://{DockerAzureEnvironment.AzuriteNetworkAlias}:10001/{DockerAzureDefaults.AzuriteAccountName};TableEndpoint=http://{DockerAzureEnvironment.AzuriteNetworkAlias}:10002/{DockerAzureDefaults.AzuriteAccountName};";

                File.WriteAllText(targetPath, $$"""
{
  "IsEncrypted": false,
  "Values": {
        "APP_KIND": "workflowApp",
        "AzureWebJobsStorage": "{{storageConnectionString}}",
        "AzureWebJobsSecretStorageType": "Files",
    "FUNCTIONS_EXTENSION_VERSION": "~4",
        "FUNCTIONS_INPROC_NET8_ENABLED": "1",
    "FUNCTIONS_V2_COMPATIBILITY_MODE": "true",
        "FUNCTIONS_WORKER_RUNTIME": "dotnet",
        "ProjectDirectoryPath": "{{LogicAppRoot}}",
    "WEBSITE_NODE_DEFAULT_VERSION": "~18"
  }
}
""");
    }

    private static void WriteConnectionsJson(string projectDirectory, string targetPath)
    {
        string sourcePath = Path.Combine(projectDirectory, "connections.json");
        if (File.Exists(sourcePath) && HasMeaningfulConnections(sourcePath))
        {
            File.Copy(sourcePath, targetPath, overwrite: true);
            return;
        }
    }

    private static bool HasMeaningfulConnections(string sourcePath)
    {
        using FileStream stream = File.OpenRead(sourcePath);
        using JsonDocument document = JsonDocument.Parse(stream);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return true;

        return HasObjectEntries(document.RootElement, "managedApiConnections")
            || HasObjectEntries(document.RootElement, "serviceProviderConnections");
    }

    private static bool HasObjectEntries(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Object
            && property.EnumerateObject().Any();
    }

    private static bool DockerImageExists(string imageName, CancellationToken cancellationToken)
    {
        CommandResult result = RunDockerCommand($"image inspect {imageName}", cancellationToken, throwOnError: false);
        return result.ExitCode == 0;
    }

    private static void BuildManagedRuntimeImage(ScopedLogger logger, CancellationToken cancellationToken)
    {
        string buildContext = Path.Combine(Path.GetTempPath(), "TestFramework", "logicapp-image-build", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(buildContext);

        try
        {
            File.WriteAllText(Path.Combine(buildContext, "Dockerfile"), """
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl gnupg apt-transport-https ca-certificates nodejs npm \
    && npm install -g azure-functions-core-tools@4 --unsafe-perm true \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /home/site/wwwroot
CMD ["func", "start", "--verbose", "--port", "80", "--address", "0.0.0.0"]
""");

            logger.LogInformation($"Building managed Logic App runtime image '{ManagedRuntimeImage}'.");
            CommandResult result = RunDockerCommand($"build -t {ManagedRuntimeImage} \"{buildContext}\"", cancellationToken, throwOnError: false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to build the managed Logic App runtime image '{ManagedRuntimeImage}'."
                    + FormatCommandOutputSuffix(result.StandardOutput, result.StandardError));
            }
        }
        finally
        {
            if (Directory.Exists(buildContext))
                Directory.Delete(buildContext, recursive: true);
        }
    }

    private static CommandResult RunDockerCommand(string arguments, CancellationToken cancellationToken, bool throwOnError = true)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new()
        {
            FileName = "docker",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        NormalizeDockerHost(startInfo.Environment);

        using Process process = new()
        {
            StartInfo = startInfo,
        };

        process.Start();
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        CommandResult result = new(process.ExitCode, standardOutput, standardError);
        if (throwOnError && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker command 'docker {arguments}' failed with exit code {result.ExitCode}."
                + FormatCommandOutputSuffix(result.StandardOutput, result.StandardError));
        }

        return result;
    }

    private static void NormalizeDockerHost(IDictionary<string, string?> environment)
    {
        const string dockerHost = "DOCKER_HOST";

        if (!environment.TryGetValue(dockerHost, out string? value) || string.IsNullOrWhiteSpace(value))
            return;

        if (value.StartsWith("npipe://./pipe/", StringComparison.OrdinalIgnoreCase))
            environment[dockerHost] = $"npipe:////./pipe/{value[15..]}";
    }

    private static string FormatCommandOutputSuffix(string stdout, string stderr)
    {
        string combined = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(static value => !string.IsNullOrWhiteSpace(value))).Trim();
        if (string.IsNullOrWhiteSpace(combined))
            return string.Empty;

        const int maxLength = 4000;
        string snippet = combined.Length <= maxLength ? combined : combined[^maxLength..];
        return $"{Environment.NewLine}{Environment.NewLine}Command output:{Environment.NewLine}{snippet}";
    }

    private static async Task WaitForWorkflowReadyAsync(string baseUrl, MaterializedLogicAppHost host, string workflowName, LogicAppConfig config, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { BaseAddress = new Uri(baseUrl) };
        DateTime deadline = DateTime.UtcNow.AddMinutes(5);
        string workflowsPath = "runtime/webhooks/workflow/api/management/workflows?api-version=2022-03-01";
        string? lastObservedHealthState = null;
        string? lastObservedHealthMessage = null;
        LogicAppWorkflowDefinition? definition = host.WorkflowDefinitions.TryGetValue(workflowName, out LogicAppWorkflowDefinition? resolvedDefinition)
            ? resolvedDefinition
            : null;
        string triggerName = ResolveReadinessTriggerName(definition);
        LogicAppTriggerReadinessMode readinessMode = ResolveReadinessMode(definition);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using HttpResponseMessage workflowResponse = await client.GetAsync(workflowsPath, cancellationToken).ConfigureAwait(false);
                if (workflowResponse.IsSuccessStatusCode)
                {
                    string payload = await workflowResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (TryReadWorkflowHealth(payload, workflowName, out string? healthState, out string? healthMessage))
                    {
                        lastObservedHealthState = healthState;
                        lastObservedHealthMessage = healthMessage;

                        if (readinessMode == LogicAppTriggerReadinessMode.ManagementRun)
                            return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }

            try
            {
                using HttpRequestMessage request = CreateReadinessRequest(baseUrl, workflowName, triggerName, readinessMode, config);
                using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        string healthSuffix = string.IsNullOrWhiteSpace(lastObservedHealthState)
            ? string.Empty
            : $" Last observed workflow health: {lastObservedHealthState}."
                + (string.IsNullOrWhiteSpace(lastObservedHealthMessage) ? string.Empty : $" {lastObservedHealthMessage}");

        throw new TimeoutException($"The Logic App workflow '{workflowName}' at '{baseUrl}' did not expose its trigger management endpoint within five minutes.{healthSuffix}");
    }

    private static HttpRequestMessage CreateReadinessRequest(string baseUrl, string workflowName, string triggerName, LogicAppTriggerReadinessMode mode, LogicAppConfig config)
    {
        Uri requestUri = mode switch
        {
            LogicAppTriggerReadinessMode.HttpCallback => new Uri(new Uri(baseUrl), $"runtime/webhooks/workflow/api/management/workflows/{Uri.EscapeDataString(workflowName)}/triggers/{Uri.EscapeDataString(triggerName)}/listCallbackUrl?api-version=2022-03-01"),
            LogicAppTriggerReadinessMode.ManagementRun => new Uri(new Uri(baseUrl), $"runtime/webhooks/workflow/api/management/workflows/{Uri.EscapeDataString(workflowName)}/triggers/{Uri.EscapeDataString(triggerName)}/run?api-version=2022-03-01"),
            _ => throw new InvalidOperationException($"Unsupported readiness mode '{mode}'.")
        };

        HttpRequestMessage request = new(HttpMethod.Post, requestUri);
        string? hostKey = config.Standard.AdminCode ?? config.Standard.Code;
        if (!string.IsNullOrWhiteSpace(hostKey))
            request.Headers.Add("x-functions-key", hostKey);
        if (mode == LogicAppTriggerReadinessMode.HttpCallback)
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    private static LogicAppWorkflowDefinition ReadWorkflowDefinition(string workflowPath, string workflowName)
    {
        using FileStream stream = File.OpenRead(workflowPath);
        using JsonDocument document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("definition", out JsonElement definitionElement)
            || !definitionElement.TryGetProperty("triggers", out JsonElement triggersElement)
            || triggersElement.ValueKind != JsonValueKind.Object)
        {
            return new LogicAppWorkflowDefinition(workflowName, null, null);
        }

        JsonProperty? firstTrigger = triggersElement.EnumerateObject().FirstOrDefault();
        if (firstTrigger is null || string.IsNullOrWhiteSpace(firstTrigger.Value.Name))
            return new LogicAppWorkflowDefinition(workflowName, null, null);

        string? triggerType = firstTrigger.Value.Value.TryGetProperty("type", out JsonElement typeElement)
            ? typeElement.GetString()
            : null;

        return new LogicAppWorkflowDefinition(workflowName, firstTrigger.Value.Name, triggerType);
    }

    private static string ResolveReadinessTriggerName(LogicAppWorkflowDefinition? definition)
        => !string.IsNullOrWhiteSpace(definition?.TriggerName) ? definition.TriggerName! : "manual";

    private static LogicAppTriggerReadinessMode ResolveReadinessMode(LogicAppWorkflowDefinition? definition)
    {
        return definition?.TriggerType?.Trim().ToLowerInvariant() switch
        {
            "request" => LogicAppTriggerReadinessMode.HttpCallback,
            "recurrence" => LogicAppTriggerReadinessMode.ManagementRun,
            _ => LogicAppTriggerReadinessMode.HttpCallback,
        };
    }

    private static bool TryReadWorkflowHealth(string payload, string workflowName, out string? healthState, out string? healthMessage)
    {
        healthState = null;
        healthMessage = null;

        using JsonDocument document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return false;

        foreach (JsonElement workflow in document.RootElement.EnumerateArray())
        {
            if (!workflow.TryGetProperty("name", out JsonElement nameElement)
                || !string.Equals(nameElement.GetString(), workflowName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (workflow.TryGetProperty("health", out JsonElement healthElement))
            {
                if (healthElement.TryGetProperty("state", out JsonElement stateElement))
                    healthState = stateElement.GetString();

                if (healthElement.TryGetProperty("errorMessage", out JsonElement errorElement))
                    healthMessage = errorElement.ToString();
            }

            return true;
        }

        return false;
    }

    private sealed record MaterializedLogicAppHost(string HostRootPath, IReadOnlyDictionary<string, LogicAppWorkflowDefinition> WorkflowDefinitions);

    private sealed record LogicAppWorkflowDefinition(string WorkflowName, string? TriggerName, string? TriggerType);

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    private enum LogicAppTriggerReadinessMode
    {
        HttpCallback,
        ManagementRun,
    }

    private sealed class LogicAppRuntimeState(IContainer container, string temporaryHostRootPath) : IAsyncDisposable
    {
        public IContainer Container { get; } = container;

        public async ValueTask DisposeAsync()
        {
            await ForceRemoveContainerAsync(Container, CancellationToken.None).ConfigureAwait(false);

            if (Directory.Exists(temporaryHostRootPath))
                Directory.Delete(temporaryHostRootPath, recursive: true);
        }
    }
}